using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ManageDesignTableRow
    {
        public string Config;
        public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public class ManageDesignTableResult
    {
        public bool HasTable;
        public List<string> Headers = new List<string>();
        public List<ManageDesignTableRow> Rows = new List<ManageDesignTableRow>();
        public List<string> LiveConfigs = new List<string>();
        public List<string> OrphanConfigs = new List<string>();   // live configs NOT covered by any table row
        public List<string> OrphanRows = new List<string>();      // table rows whose config no longer exists live
        public bool LinkBroken;
        public string LinkedFile;
        public string EditedConfig;
        public string EditedHeader;
        public string EditedValueBefore;
        public string EditedValueAfter;
        public bool Verified;
        public bool AlreadyDone;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 194 — manage_design_table (WRITE + READ). Reads/writes design-table rows, validates the table's
    /// config rows against the part's ACTUAL live configurations, and repairs a broken linked-Excel-file
    /// reference. "update the design table", "in the design table set Variant-1's depth to 25mm",
    /// "validate the design table", "repair the design table's excel link".
    ///
    /// GOTCHA (confirmed 2026-07-31): IModelDoc2.InsertFamilyTableNew() does NOT hang (a real risk flagged
    /// before ever calling it live) but produces a DEGENERATE table — title row only, no config rows, no
    /// dimension columns; it needs the interactive OLE Excel edit session to actually populate (see
    /// the test fixture generator). This handler therefore never CREATES a table from scratch — it only
    /// reads/writes/validates/repairs an EXISTING one, matching the tool's own spec (repair/validate both
    /// presuppose a table already exists).
    ///
    /// GOTCHA: IDesignTable's row/col indexing is NOT what GetRowCount/GetColumnCount's raw ints suggest by
    /// themselves. Row 0 is the HEADER row; data rows start at row 1. Column 0 is the CONFIG-NAME column
    /// (GetEntryText(r,0)); data columns start at column 1, and GetHeaderText(c) for the data column at
    /// GetEntryText column index c uses c-1 (GetHeaderText is 0-indexed over just the PARAMETER columns,
    /// excluding the implicit config-name column). Reflection+live-confirmed: Attach/GetRowCount/GetColumnCount/
    /// GetHeaderText/GetEntryText/SetEntryText/UpdateTable(Type,Close)/LinkToFile/FileName/Detach.
    /// </summary>
    public static class ManageDesignTable
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return Regex.IsMatch(cmd.ToLowerInvariant(), @"\bdesign\s*table\b");
        }

        private static bool IsRepairIntent(string c) => Regex.IsMatch(c, @"\b(repair|fix|reconnect|relink)\b");
        private static bool IsValidateIntent(string c) => Regex.IsMatch(c, @"\b(validate|check|audit|verify)\b");

        public static async Task<ManageDesignTableResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ManageDesignTableResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to manage its design table."; return res; }

            await emit("Gauge", "reading the design table", "run", null);
            bool hasTable = false; try { hasTable = model.Extension.HasDesignTable(); } catch { }
            res.HasTable = hasTable;
            if (!hasTable)
            {
                res.Error = "This part has no design table to manage — insert one in SolidWorks first (Forge can't build a usable one headlessly on this build).";
                await emit("Gauge", null, "fail", "no design table");
                return res;
            }

            var dt = model.IGetDesignTable();
            if (dt == null) { res.Error = "Couldn't access the design table."; await emit("Gauge", null, "fail", "IGetDesignTable null"); return res; }

            // ---- link health (the repair path needs this regardless of what else runs) ----
            bool linkToFile = false; string fileName = null;
            try { linkToFile = dt.LinkToFile; } catch { }
            try { fileName = dt.FileName; } catch { }
            res.LinkedFile = fileName;
            res.LinkBroken = linkToFile && !string.IsNullOrEmpty(fileName) && !System.IO.File.Exists(fileName);

            // ---- read the table: row0=headers, col0=config name ----
            try { dt.Attach(); } catch { }
            int rowCount = 0, colCount = 0;
            try { rowCount = dt.GetRowCount(); } catch { }
            try { colCount = dt.GetColumnCount(); } catch { }
            for (int c = 1; c < colCount; c++) { string h = null; try { h = dt.GetHeaderText(c - 1); } catch { } res.Headers.Add(h ?? ("col" + c)); }
            for (int r = 1; r < rowCount; r++)
            {
                string cn = null; try { cn = dt.GetEntryText(r, 0); } catch { }
                if (string.IsNullOrWhiteSpace(cn)) continue;   // trailing blank padding row
                var row = new ManageDesignTableRow { Config = cn };
                for (int c = 1; c < colCount; c++) { string v = null; try { v = dt.GetEntryText(r, c); } catch { } row.Values[res.Headers[c - 1]] = v; }
                res.Rows.Add(row);
            }
            try { dt.Detach(); } catch { }

            string[] liveCfgsArr = null; try { liveCfgsArr = model.GetConfigurationNames() as string[]; } catch { }
            res.LiveConfigs = (liveCfgsArr ?? new string[0]).ToList();
            var tableCfgSet = new HashSet<string>(res.Rows.Select(r => r.Config), StringComparer.OrdinalIgnoreCase);
            var liveCfgSet = new HashSet<string>(res.LiveConfigs, StringComparer.OrdinalIgnoreCase);
            res.OrphanConfigs = res.LiveConfigs.Where(c => !tableCfgSet.Contains(c)).ToList();
            res.OrphanRows = res.Rows.Select(r => r.Config).Where(c => !liveCfgSet.Contains(c)).ToList();

            string cLower = (intent ?? "").ToLowerInvariant();

            // ---- REPAIR: broken linked-file reference -> unlink (embed as-is). This is the only headless-
            // recoverable fix without the human resupplying the missing xlsx. ----
            if (IsRepairIntent(cLower))
            {
                if (!res.LinkBroken)
                {
                    res.AlreadyDone = true; res.Verified = true;
                    res.Info = "The design table's Excel link is healthy — nothing to repair.";
                    await emit("Sentinel", null, "done", "link healthy");
                    return res;
                }
                await emit("Scribe", "unlinking the broken Excel reference (embedding the table as-is)", "run", null);
                try { dt.Attach(); } catch { }
                try { dt.LinkToFile = false; }
                catch (Exception lex) { res.Error = "Couldn't unlink: " + lex.Message; await emit("Scribe", null, "fail", res.Error); return res; }
                try { dt.UpdateTable((int)swDesignTableUpdateOptions_e.swUpdateDesignTableAll, true); } catch { }
                try { dt.Detach(); } catch { }
                model.ForceRebuild3(false);

                bool stillLinked = false;
                try { var dt2 = model.IGetDesignTable(); dt2.Attach(); stillLinked = dt2.LinkToFile; dt2.Detach(); } catch { }
                res.Verified = !stillLinked;
                if (!res.Verified)
                { res.Error = "Unlink didn't take — the table is still reporting LinkToFile=true."; await emit("Sentinel", null, "fail", res.Error); return res; }
                res.Info = "Repaired: the design table's broken Excel link (" + fileName + ") is now embedded/unlinked so it no longer depends on that missing file.";
                await emit("Sentinel", null, "done", "unlinked");
                return res;
            }

            bool wantsValidate = IsValidateIntent(cLower);

            // ---- WRITE: "set <config>'s <column> to <value>" — find the row by config name named in the
            // command, and the column: explicit header-word match if one is named, else the ONLY data column if
            // there's just one (the common case) — never silently guess across multiple ambiguous columns. ----
            ManageDesignTableRow targetRow = null;
            foreach (var row in res.Rows) if (cLower.Contains(row.Config.ToLowerInvariant())) { targetRow = row; break; }

            double? newValue = null;
            var numMatch = Regex.Match(cLower, @"(-?\d+(?:\.\d+)?)\s*mm\b");
            if (!numMatch.Success) numMatch = Regex.Match(cLower, @"\bto\s+(-?\d+(?:\.\d+)?)\b");
            if (numMatch.Success) newValue = double.Parse(numMatch.Groups[1].Value, CultureInfo.InvariantCulture);

            if (targetRow != null && newValue.HasValue && !wantsValidate)
            {
                string targetHeader = null;
                foreach (var h in res.Headers)
                {
                    if (string.IsNullOrEmpty(h)) continue;
                    string bare = h.Split('@')[0].ToLowerInvariant();
                    if (cLower.Contains(bare)) { targetHeader = h; break; }
                }
                if (targetHeader == null && res.Headers.Count == 1) targetHeader = res.Headers[0];
                if (targetHeader == null)
                {
                    res.Error = "The design table has " + res.Headers.Count + " columns (" + string.Join(", ", res.Headers) + ") — say which one to change.";
                    await emit("Gauge", null, "fail", "ambiguous column");
                    return res;
                }

                int rowIdx = res.Rows.IndexOf(targetRow) + 1;      // +1: row0 is the header row
                int colIdx = res.Headers.IndexOf(targetHeader) + 1; // +1: col0 is the config-name column
                string before = targetRow.Values[targetHeader];
                res.EditedConfig = targetRow.Config; res.EditedHeader = targetHeader; res.EditedValueBefore = before;

                double beforeNum;
                if (double.TryParse(before, NumberStyles.Float, CultureInfo.InvariantCulture, out beforeNum) && Math.Abs(beforeNum - newValue.Value) < 0.01)
                {
                    res.AlreadyDone = true; res.Verified = true; res.EditedValueAfter = before;
                    res.Info = targetRow.Config + "'s " + targetHeader + " is already " + newValue.Value + "mm — nothing to change.";
                    await emit("Sentinel", null, "done", "already " + newValue.Value + "mm");
                    return res;
                }

                await emit("Scribe", "setting " + targetRow.Config + "'s " + targetHeader + " to " + newValue.Value + "mm", "run", null);
                try { dt.Attach(); } catch { }
                try { dt.SetEntryText(rowIdx, colIdx, newValue.Value.ToString(CultureInfo.InvariantCulture)); }
                catch (Exception sex) { res.Error = "SetEntryText failed: " + sex.Message; try { dt.Detach(); } catch { } await emit("Scribe", null, "fail", res.Error); return res; }
                bool upd = false; try { upd = dt.UpdateTable((int)swDesignTableUpdateOptions_e.swUpdateDesignTableAll, true); } catch (Exception uex) { res.Error = "UpdateTable failed: " + uex.Message; }
                try { dt.Detach(); } catch { }
                if (!upd)
                { res.Error = res.Error ?? "UpdateTable returned false — the edit didn't commit."; await emit("Sentinel", null, "fail", res.Error); return res; }
                model.ForceRebuild3(false);

                // Sentinel: INDEPENDENT re-measurement — activate the edited config and read the driven dimension
                // straight off the feature tree (GroundTruth.ConfigSpecificDimension's proven idiom), never the
                // table's own cell text.
                await emit("Sentinel", "verifying the edited configuration's actual dimension", "run", null);
                string origActive = null; try { origActive = model.ConfigurationManager?.ActiveConfiguration?.Name; } catch { }
                double afterMm = -1;
                try
                {
                    model.ShowConfiguration2(targetRow.Config); model.ForceRebuild3(false);
                    string dimName = targetHeader.Split('@')[0];
                    string featName = targetHeader.Contains("@") ? targetHeader.Substring(targetHeader.IndexOf('@') + 1) : null;
                    var feat = model.FirstFeature() as Feature;
                    while (feat != null && afterMm < 0)
                    {
                        string fn = null; try { fn = feat.Name; } catch { }
                        if (featName == null || string.Equals(fn, featName, StringComparison.OrdinalIgnoreCase))
                        {
                            var dd = feat.GetFirstDisplayDimension() as DisplayDimension;
                            while (dd != null)
                            {
                                var d = dd.GetDimension2(0) as Dimension;
                                string dfull = null; try { dfull = d?.FullName; } catch { }
                                if (dfull != null && dfull.StartsWith(dimName + "@", StringComparison.OrdinalIgnoreCase))
                                { try { afterMm = d.SystemValue * 1000.0; } catch { } break; }
                                dd = feat.GetNextDisplayDimension(dd) as DisplayDimension;
                            }
                        }
                        feat = feat.GetNextFeature() as Feature;
                    }
                }
                catch { }
                try { if (!string.IsNullOrEmpty(origActive)) { model.ShowConfiguration2(origActive); model.ForceRebuild3(false); } } catch { }

                res.EditedValueAfter = afterMm.ToString(CultureInfo.InvariantCulture);
                res.Verified = Math.Abs(afterMm - newValue.Value) < 0.01;
                if (!res.Verified)
                {
                    res.Error = "Table cell updated but the " + targetRow.Config + " configuration measured " + afterMm + "mm, not the requested " + newValue.Value + "mm.";
                    await emit("Sentinel", null, "fail", res.Error);
                    return res;
                }

                res.Info = "Design table updated: " + targetRow.Config + "'s " + targetHeader + " is now " + newValue.Value + "mm (was " + before + "mm), confirmed by an independent re-measurement.";
                await emit("Sentinel", null, "done", res.Info);
                return res;
            }

            // ---- default / explicit validate: report + cross-check, never silently no-op a doable write ----
            res.Verified = true;
            var problems = new List<string>();
            if (res.OrphanConfigs.Count > 0) problems.Add(res.OrphanConfigs.Count + " configuration(s) not covered by the table: " + string.Join(", ", res.OrphanConfigs));
            if (res.OrphanRows.Count > 0) problems.Add(res.OrphanRows.Count + " table row(s) reference configurations that no longer exist: " + string.Join(", ", res.OrphanRows));
            if (res.LinkBroken) problems.Add("the table's Excel link is broken (file not found: " + fileName + ")");
            res.Info = problems.Count == 0
                ? "Design table is healthy: " + res.Rows.Count + " row(s), " + res.Headers.Count + " column(s), matches all " + res.LiveConfigs.Count + " live configuration(s)."
                : "Design table issues: " + string.Join("; ", problems) + ".";
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }
    }
}
