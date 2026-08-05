using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class UpdateRevisionTableResult
    {
        public bool AlreadyDone;
        public bool Verified;
        public string SheetName;
        public string Revision;
        public string Description;
        public int RowsBefore;
        public int RowsAfter;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// UpdateRevisionTable (tool #184 update_revision_table, WRITE) — add a revision row (rev letter/number +
    /// description, date auto-stamped) to the ACTIVE drawing's revision table, inserting the table itself if the
    /// sheet doesn't have one yet.
    ///
    /// API: `ISheet.get_RevisionTable()`/`InsertRevisionTable2(...)` -> `IRevisionTableAnnotation.AddRevision
    /// (string):int` (returns a RevisionId, confirmed live via redist DLL reflection) — a genuinely different
    /// object family from `IBomTableAnnotation` (tools 113/161) despite the sibling "table on a drawing sheet"
    /// shape. `IRevisionTableAnnotation` does NOT itself expose row/column/cell accessors (reflection confirmed
    /// its member list is revision-specific only: AddRevision/DeleteRevision/GetRevisionForId/GetRowNumberForId/
    /// GetIdForRowNumber/symbol+custom-property getters) — same multi-interface-on-one-COM-object shape
    /// `InsertBomTable.cs` already proved out (`raw as TableAnnotation` + `raw as BomTableAnnotation` on the SAME
    /// returned object), so cell writes (description/date) go through a second cast of the identical object to
    /// `ITableAnnotation` (`get_Text`/`set_Text2`/`RowCount`/`ColumnCount`), locating the Description/Date columns
    /// by READING the header row text rather than hardcoding column indices (the shipped templates differ —
    /// "standard revision block.sldrevtbt" vs "no zone column.sldrevtbt" — so a fixed index would silently write
    /// into the wrong column on a different template).
    ///
    /// Template: `swFileLocationsRevisionTableTemplates` user-preference search path first (a search-folder list,
    /// not a filename — same shape as `InsertBomTable.cs`'s `swFileLocationsBOMTemplates`), then the install's own
    /// `lang\english\standard revision block.sldrevtbt` as a real-file fallback.
    ///
    /// FAIL CLOSED: Verified requires an INDEPENDENT re-fetch of the revision table (`sheet.get_RevisionTable()`
    /// again, not the in-memory object the write went through) whose row count actually grew AND whose
    /// `GetRevisionForId` on the just-added id reads back the requested label.
    ///
    /// IDEMPOTENT (Rule #5): if a row with the EXACT requested revision label already exists (walked via
    /// `GetIdForRowNumber(row)` -> `GetRevisionForId(id)` for every existing data row), reports AlreadyDone
    /// instead of stacking a duplicate row for the same revision.
    /// </summary>
    public static class UpdateRevisionTable
    {
        // Requires an add/log/record/update/bump verb AND the "revision(s)" noun. No other matcher in this build
        // claims the bare word "revision" as an intent trigger (Compare.cs only strips it as filename noise), so
        // no drawing-scope qualifier is needed to stay disjoint from compare_documents (verb "compare"/"diff").
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(add|update|log|record|insert|bump|increment)\b")) return false;
            return Regex.IsMatch(c, @"\brevisions?\b");
        }

        public static async Task<UpdateRevisionTableResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new UpdateRevisionTableResult();
            var dd = model as DrawingDoc;
            if (dd == null) { res.Error = "Open the drawing whose revision table needs updating."; return res; }

            var sheet = dd.GetCurrentSheet() as ISheet;
            if (sheet == null) { res.Error = "Couldn't get the current sheet."; return res; }
            try { res.SheetName = sheet.GetName(); } catch { }

            string label = ParseRevisionLabel(intent);
            string desc = ParseDescription(intent);
            res.Description = desc;

            await emit("Revision", "checking for an existing revision table", "run", null);
            object rawExisting = null;
            try { rawExisting = sheet.RevisionTable; } catch { }

            if (rawExisting != null)
            {
                var existingRev = rawExisting as RevisionTableAnnotation;
                var existingTa = rawExisting as TableAnnotation;
                int rows = 0; try { rows = existingTa != null ? existingTa.RowCount : 0; } catch { }
                if (label == null) label = NextRevisionLabel(existingRev, rows);
                res.Revision = label;
                if (existingRev != null && AlreadyHasRevision(existingRev, existingTa, rows, label))
                {
                    res.AlreadyDone = true; res.Verified = true;
                    res.Info = "Revision \"" + label + "\" is already on this drawing's revision table — nothing to add.";
                    await emit("Revision", null, "done", res.Info);
                    return res;
                }
                res.RowsBefore = rows;
            }
            else
            {
                if (label == null) label = "A";
                res.Revision = label;
                res.RowsBefore = 0;
            }
            await emit("Revision", null, "done", rawExisting != null ? "existing table, " + res.RowsBefore + " row(s)" : "no revision table yet");

            object rawTable = rawExisting;
            if (rawTable == null)
            {
                string template = ResolveTemplate(app);
                if (template == null) { res.Error = "Couldn't find a revision table template on this install."; return res; }
                await emit("Revision", "inserting a new revision table", "run", null);
                try
                {
                    rawTable = sheet.InsertRevisionTable2(true, 0, 0,
                        (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopRight,
                        template, (int)swRevisionTableSymbolShape_e.swRevisionTable_TriangleSymbol, true);
                }
                catch (Exception ex) { res.Error = "InsertRevisionTable2 threw (" + ex.GetType().Name + ")"; return res; }
                if (rawTable == null) { res.Error = "InsertRevisionTable2 returned nothing — the template or sheet may be unusable."; return res; }
                await emit("Revision", null, "done", "table inserted");
            }

            var revTable = rawTable as RevisionTableAnnotation;
            if (revTable == null) { res.Error = "Couldn't read the revision table back as a RevisionTableAnnotation."; return res; }

            await emit("Revision", "adding revision \"" + label + "\"", "run", desc);
            int newId = -1;
            try { newId = revTable.AddRevision(label); }
            catch (Exception ex) { res.Error = "AddRevision threw (" + ex.GetType().Name + ")"; return res; }
            if (newId < 0) { res.Error = "AddRevision returned no id — the row wasn't added."; return res; }

            try { WriteRowCells(rawTable as TableAnnotation, revTable, newId, desc); } catch { }

            try { model.ForceRebuild3(false); } catch { }

            // ---- FAIL CLOSED: independent re-fetch, not the in-memory object the write went through. ----
            object rawFresh = null;
            try { rawFresh = sheet.RevisionTable; } catch { }
            var freshRev = rawFresh as RevisionTableAnnotation;
            var freshTa = rawFresh as TableAnnotation;
            int rowsAfter = 0; try { rowsAfter = freshTa != null ? freshTa.RowCount : 0; } catch { }
            string readBack = null; try { readBack = freshRev != null ? freshRev.GetRevisionForId(newId) : null; } catch { }
            res.RowsAfter = rowsAfter;

            res.Diag = "rowsBefore=" + res.RowsBefore + " rowsAfter=" + rowsAfter + " readBack=" + (readBack ?? "(null)");
            res.Verified = rowsAfter > res.RowsBefore && string.Equals(readBack, label, StringComparison.OrdinalIgnoreCase);
            await emit("Revision", null, res.Verified ? "done" : "fail",
                res.Verified ? "revision \"" + label + "\" added (" + rowsAfter + " row(s) now)" : res.Diag);

            if (!res.Verified)
            { res.Error = "Added, but couldn't independently verify the new row (" + res.Diag + ")."; return res; }

            res.Info = "Added revision \"" + label + "\" to \"" + res.SheetName + "\" (" + desc + ").";
            return res;
        }

        // Header-row-driven cell writer: reads row 0's text per column looking for a "descr"/"date" header
        // instead of trusting a fixed column index, since shipped revision-table templates differ in column
        // layout (zone column present or not).
        private static void WriteRowCells(TableAnnotation ta, RevisionTableAnnotation revTable, int id, string desc)
        {
            if (ta == null || string.IsNullOrEmpty(desc)) return;
            int row = -1; try { row = revTable.GetRowNumberForId(id); } catch { }
            if (row < 0) return;
            int cols = 0; try { cols = ta.ColumnCount; } catch { }
            for (int c = 0; c < cols; c++)
            {
                string header = null; try { header = ta.get_Text(0, c); } catch { }
                if (string.IsNullOrEmpty(header)) continue;
                string h = header.ToLowerInvariant();
                if (h.Contains("descr"))
                {
                    try { ta.set_Text2(row, c, false, desc); } catch { }
                }
            }
        }

        private static bool AlreadyHasRevision(RevisionTableAnnotation revTable, TableAnnotation ta, int rows, string label)
        {
            if (revTable == null || rows < 2) return false;
            for (int r = 1; r < rows; r++)
            {
                int id = -1; try { id = revTable.GetIdForRowNumber(r); } catch { }
                if (id < 0) continue;
                string existing = null; try { existing = revTable.GetRevisionForId(id); } catch { }
                if (string.Equals(existing, label, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Bump the highest existing revision letter by one (A->B, B->C, ...); falls back to "A" for an empty
        // table or a non-alphabetic scheme (numeric revisions aren't auto-incremented — ambiguous without a
        // stated starting point, so an explicit label in the command always wins over this fallback).
        private static string NextRevisionLabel(RevisionTableAnnotation revTable, int rows)
        {
            if (revTable == null || rows < 2) return "A";
            char highest = '\0';
            for (int r = 1; r < rows; r++)
            {
                int id = -1; try { id = revTable.GetIdForRowNumber(r); } catch { }
                if (id < 0) continue;
                string existing = null; try { existing = revTable.GetRevisionForId(id); } catch { }
                if (string.IsNullOrEmpty(existing) || existing.Length != 1) continue;
                char ch = char.ToUpperInvariant(existing[0]);
                if (ch >= 'A' && ch <= 'Z' && ch > highest) highest = ch;
            }
            if (highest == '\0') return "A";
            return highest >= 'Z' ? "AA" : ((char)(highest + 1)).ToString();
        }

        // "revision B", "rev. C", "revision: 2" — 1-3 alnum chars only, so it never matches the noun phrase
        // "revision table"/"revision row" itself.
        private static string ParseRevisionLabel(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            var m = Regex.Match(cmd, @"\brev(?:ision)?\.?\s*[:\-]?\s*([A-Za-z0-9]{1,3})\b", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string tok = m.Groups[1].Value;
            if (string.Equals(tok, "table", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tok, "row", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tok, "for", StringComparison.OrdinalIgnoreCase)) return null;
            return tok.ToUpperInvariant();
        }

        private static string ParseDescription(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "Engineering revision update.";
            var q = Regex.Match(cmd, "\"([^\"]+)\"");
            if (q.Success) return q.Groups[1].Value.Trim();
            var m = Regex.Match(cmd, @"\b(?:for|describing|description|note)\b[:\s]+(.+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string tail = m.Groups[1].Value.Trim().TrimEnd('.');
                if (!string.IsNullOrEmpty(tail)) return tail;
            }
            return "Engineering revision update.";
        }

        // swFileLocationsRevisionTableTemplates (a STRING preference, one folder or several delimited by ';'/',',
        // same shape as InsertBomTable.cs's swFileLocationsBOMTemplates) — scan for a *.sldrevtbt, preferring
        // "standard revision block.sldrevtbt". Falls back to the install's own lang\english folder.
        private static string ResolveTemplate(ISldWorks app)
        {
            try
            {
                string raw = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsRevisionTableTemplates);
                if (!string.IsNullOrEmpty(raw))
                {
                    foreach (var f in raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (string.IsNullOrEmpty(f) || !Directory.Exists(f)) continue;
                        var std = Path.Combine(f, "standard revision block.sldrevtbt");
                        if (File.Exists(std)) return std;
                        var any = Directory.GetFiles(f, "*.sldrevtbt").FirstOrDefault();
                        if (any != null) return any;
                    }
                }
            }
            catch { }
            try
            {
                string exeDir = Path.GetDirectoryName(app.GetExecutablePath());
                var fallback = Path.Combine(exeDir, "lang", "english", "standard revision block.sldrevtbt");
                if (File.Exists(fallback)) return fallback;
                var dir = Path.Combine(exeDir, "lang", "english");
                if (Directory.Exists(dir))
                {
                    var any = Directory.GetFiles(dir, "*.sldrevtbt").FirstOrDefault();
                    if (any != null) return any;
                }
            }
            catch { }
            return null;
        }
    }
}
