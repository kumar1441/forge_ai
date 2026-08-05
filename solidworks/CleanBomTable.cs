using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CleanBomTableResult
    {
        public bool AlreadyDone;
        public bool Verified;
        public int RowsBefore;
        public int RowsAfter;
        public int RemovedOrphaned;
        public int RemovedDuplicate;
        public string SortedByColumn;
        public bool SortDescending;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CleanBomTable (tool #161 clean_bom_table, WRITE) — for the ACTIVE drawing's existing BOM table, remove
    /// stale/orphaned rows (backed by zero live components — can linger after a component is deleted/excluded
    /// post-insert), remove duplicate Part-Number rows, then resort. `IView.GetBomTable()` is the DEAD accessor
    /// `InsertBomTable.cs` (tool 113) already documented (never reflects a just-inserted table); this instead
    /// walks the SAME "BomFeat" feature-tree entry InsertBomTable.cs uses for its own idempotency check, then
    /// reaches the live table through a genuinely different API surface — `Feature.GetSpecificFeature2() ->
    /// IBomFeature -> IGetTableAnnotations(1) -> BomTableAnnotation` — confirmed live via redist DLL reflection
    /// (`GetTableAnnotationCount`, `IGetTableAnnotations`, `Sort(BomTableSortData)`, `DeleteRow2`, `get_Text`
    /// all resolve on this build; `ITableAnnotation.Text` is a NAMED indexed COM property, not the default
    /// indexer, so C# must call the raw `get_Text(row, col)`/`set_Text` accessors — `obj.Text[r,c]` is VB-only
    /// syntax and does not compile here).
    ///
    /// "Resort" picks whichever is more useful: if a QTY/QUANTITY column exists, sorts DESCENDING by quantity
    /// (highest-volume parts first — the common "clean up the BOM" ask); otherwise falls back to ascending by
    /// column 0 (Item Number, the table's natural insertion order).
    /// </summary>
    public static class CleanBomTable
    {
        // Requires the "bom"/"bill of materials" noun (no other handler in this build claims it — see
        // InsertBomTable.cs) AND a clean-ish verb, and explicitly EXCLUDES insert-ish verbs so this can never
        // shadow InsertBomTable regardless of dispatch order (the verb sets are disjoint, not just noun-gated).
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bboms?\b|\bbill\s+of\s+materials\b")) return false;
            if (Regex.IsMatch(c, @"\b(insert|add|create|put|generate|make)\b")) return false;
            return Regex.IsMatch(c, @"\b(clean(?:\s*up)?|tidy|resort|re-sort|sort|dedup(?:e)?|organi[sz]e|fix|repair)\b");
        }

        public static async Task<CleanBomTableResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CleanBomTableResult();
            var dd = model as DrawingDoc;
            if (dd == null) { res.Error = "Open the drawing whose BOM table you want cleaned."; return res; }

            await emit("Scout", "finding the BOM table", "run", null);
            var bomFeat = FindBomFeature(model);
            if (bomFeat == null)
            { res.Error = "This drawing has no BOM table to clean — insert one first."; return res; }

            var ibf = bomFeat.GetSpecificFeature2() as IBomFeature;
            if (ibf == null) { res.Error = "Found a BOM feature but couldn't read it as IBomFeature."; return res; }

            int tableCount = 0; try { tableCount = ibf.GetTableAnnotationCount(); } catch { }
            if (tableCount <= 0) { res.Error = "BOM feature has no table annotation attached."; return res; }
            var bta = ibf.IGetTableAnnotations(1) as BomTableAnnotation;
            var ita = bta as ITableAnnotation;
            if (ita == null) { res.Error = "Couldn't read the BOM table."; return res; }
            await emit("Scout", null, "done", "found BOM table");

            res.RowsBefore = SafeRowCount(ita);
            if (res.RowsBefore <= 1)
            {
                res.AlreadyDone = true; res.Verified = true; res.RowsAfter = res.RowsBefore;
                res.Info = "BOM table has no data rows — nothing to clean.";
                return res;
            }

            await emit("Scribe", "removing stale/duplicate rows and resorting", "run", null);

            // ---- 1. Orphaned rows: zero live components backing them (can linger after a component is deleted
            // or excluded post-insert). Walk BACKWARD so deleting row r never shifts the index of a row not yet
            // visited. ----
            int removedOrphaned = 0;
            for (int r = SafeRowCount(ita) - 1; r >= 1; r--)
            {
                int cc = -1; try { cc = bta.GetComponentsCount(r); } catch { }
                if (cc == 0)
                {
                    bool ok = false; try { ok = ita.DeleteRow2(r, true); } catch { }
                    if (ok) removedOrphaned++;
                }
            }
            res.RemovedOrphaned = removedOrphaned;

            // ---- 2. Duplicate rows: same Part Number text seen twice. Walk FORWARD; a delete at index r pulls
            // the next row into r, so don't advance r on a delete. ----
            int removedDup = 0;
            int partCol = FindColumn(ita, "PART NUMBER", "PART NO", "PARTNO", "PART #");
            if (partCol >= 0)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int r = 1;
                while (r < SafeRowCount(ita))
                {
                    string val = null; try { val = ita.get_Text(r, partCol); } catch { }
                    val = (val ?? "").Trim();
                    if (val.Length > 0 && !seen.Add(val))
                    {
                        bool ok = false; try { ok = ita.DeleteRow2(r, true); } catch { }
                        if (ok) { removedDup++; continue; }
                    }
                    r++;
                }
            }
            res.RemovedDuplicate = removedDup;

            // ---- 3. Resort: QTY/QUANTITY column descending (highest-volume first) if present, else column 0
            // (Item Number) ascending. Only meaningful with >=2 data rows. ----
            bool sortOk = true;
            if (SafeRowCount(ita) > 2)
            {
                int qtyCol = FindColumn(ita, "QTY", "QUANTITY");
                int sortCol = qtyCol >= 0 ? qtyCol : 0;
                bool ascending = qtyCol < 0;
                res.SortedByColumn = SafeColumnTitle(ita, sortCol);
                res.SortDescending = !ascending;

                sortOk = false;
                try
                {
                    var sd = bta.GetBomTableSortData() as BomTableSortData;
                    if (sd != null)
                    {
                        // ColumnIndex/Ascending are per-SORT-LEVEL (multi-column sort support) — index 0 is the
                        // primary (and only) level this handler sets.
                        sd.set_ColumnIndex(0, sortCol);
                        sd.set_Ascending(0, ascending);
                        sd.SortMethod = (int)swBomTableSortMethod_e.swBomTableSortMethod_Numeric;
                        sortOk = bta.Sort(sd);
                    }
                }
                catch (Exception ex) { res.Diag = "Sort threw (" + ex.GetType().Name + ")"; }
            }

            try { model.ForceRebuild3(true); } catch { }
            int se = 0, sw = 0; try { model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref se, ref sw); } catch { }

            // ---- FAIL CLOSED: re-fetch the table fresh (not the same in-memory reference the writes went
            // through) and independently confirm the sort order actually holds. ----
            var bomFeat2 = FindBomFeature(model);
            var ibf2 = bomFeat2 != null ? bomFeat2.GetSpecificFeature2() as IBomFeature : null;
            var bta2 = ibf2 != null ? ibf2.IGetTableAnnotations(1) as BomTableAnnotation : null;
            var ita2 = bta2 as ITableAnnotation;
            res.RowsAfter = ita2 != null ? SafeRowCount(ita2) : -1;

            bool orderOk = true;
            if (ita2 != null && res.RowsAfter > 2 && !string.IsNullOrEmpty(res.SortedByColumn))
            {
                int sortCol2 = FindColumn(ita2, res.SortedByColumn);
                if (sortCol2 >= 0) orderOk = IsMonotonic(ita2, sortCol2, ascending: !res.SortDescending);
            }

            res.Diag = "rowsBefore=" + res.RowsBefore + " rowsAfter=" + res.RowsAfter +
                " removedOrphaned=" + removedOrphaned + " removedDuplicate=" + removedDup +
                " sortedBy=" + (res.SortedByColumn ?? "(none)") + " sortOk=" + sortOk + " orderOk=" + orderOk;
            res.Verified = ita2 != null && res.RowsAfter <= res.RowsBefore && sortOk && orderOk;

            await emit("Scribe", null, res.Verified ? "done" : "fail", res.Diag);
            if (!res.Verified)
            { res.Error = "Cleaned, but couldn't independently verify the result (" + res.Diag + ")."; return res; }

            res.Info = "Cleaned the BOM table: removed " + removedOrphaned + " orphaned + " + removedDup +
                " duplicate row(s), resorted by " + (res.SortedByColumn ?? "item number") +
                (res.SortDescending ? " (descending)" : " (ascending)") + ".";
            return res;
        }

        private static int SafeRowCount(ITableAnnotation ita) { try { return ita.RowCount; } catch { return 0; } }

        private static string SafeColumnTitle(ITableAnnotation ita, int col)
        { try { return ita.GetColumnTitle(col); } catch { return null; } }

        private static bool IsMonotonic(ITableAnnotation ita, int col, bool ascending)
        {
            double? prev = null;
            for (int r = 1; r < SafeRowCount(ita); r++)
            {
                string txt = null; try { txt = ita.get_Text(r, col); } catch { }
                var m = Regex.Match(txt ?? "", @"[-+]?[0-9]*\.?[0-9]+");
                if (!m.Success) continue;
                double v = double.Parse(m.Value);
                if (prev.HasValue)
                {
                    if (ascending && v < prev.Value - 1e-6) return false;
                    if (!ascending && v > prev.Value + 1e-6) return false;
                }
                prev = v;
            }
            return true;
        }

        private static int FindColumn(ITableAnnotation ita, params string[] candidates)
        {
            int cols = 0; try { cols = ita.ColumnCount; } catch { }
            for (int c = 0; c < cols; c++)
            {
                string title = null; try { title = ita.GetColumnTitle(c); } catch { }
                if (string.IsNullOrEmpty(title)) continue;
                string t = title.ToUpperInvariant();
                foreach (var cand in candidates)
                    if (t.Contains(cand.ToUpperInvariant())) return c;
            }
            return -1;
        }

        private static Feature FindBomFeature(IModelDoc2 model)
        {
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "BomFeat") return f;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return null;
        }
    }
}
