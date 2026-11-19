using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SheetRefRow
    {
        public string StoredModel;
        public int ViewCount;
        public string FoundPath;
        public bool Repaired;
        public string Error;
    }

    public class UpdateSheetReferencesResult
    {
        public int TotalViews;
        public int MissingFound;
        public int Repaired;
        public int StillMissing;
        public List<SheetRefRow> Rows = new List<SheetRefRow>();
        public bool AlreadyDone;
        public bool Verified;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// UpdateSheetReferences (tool #114 update_sheet_references, WRITE) — for the ACTIVE drawing, find every view
    /// whose referenced model file no longer resolves on disk (the part/assembly it was drawn from was renamed or
    /// moved) and re-point it at a resolved path via `IDrawingDoc.ReplaceViewModel`. Sibling of tool 132
    /// repair_missing_references, same relink-by-search-folder shape, but operates on DRAWING VIEWS instead of
    /// ASSEMBLY COMPONENTS — a brand-new, previously-unproven API surface for this build
    /// (`ReplaceViewModel(string NewModelPathName, object Views, object Instances)`, confirmed live via redist
    /// DLL reflection while building this tool; `Instances` is for per-component swaps inside an assembly-drawing
    /// view and is left null here since this handler only repairs the whole-model reference).
    ///
    /// Detection: `IView.GetReferencedModelName()` returns the STORED name (often bare/relative to the drawing's
    /// own folder) regardless of whether the file resolves — exactly the same signal GetDrawingViews (tool 258)
    /// already surfaces as MissingModels, checked here per VIEW (not just once) since ReplaceViewModel needs the
    /// actual View objects to act on.
    ///
    /// Search: an explicit folder named in the request ("...using files from C:\Library"), else the drawing's own
    /// folder, else the drawing's own folder's immediate subfolders.
    ///
    /// FAIL CLOSED: Verified requires every found-missing view group to be independently re-read post-rebuild and
    /// resolve to a real file on disk. IDEMPOTENT (Rule #5): a rerun with nothing missing reports AlreadyDone.
    /// </summary>
    public static class UpdateSheetReferences
    {
        // Scoped to DRAWING vocabulary (sheet/drawing/view) so this never shadows repair_missing_references
        // (assembly-scoped, no drawing noun) or rename/move_file_with_references (require "file"+"to"). Checked
        // BEFORE repair_missing_references in dispatch since this is the strictly more specific of the two.
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(update|repair|fix|resolve|relink|reconnect|re-?point|reattach)\b")) return false;
            bool drawingScope = Regex.IsMatch(c, @"\b(sheet|sheets|drawing|drawings|drw|slddrw|view|views)\b");
            if (!drawingScope) return false;
            return Regex.IsMatch(c, @"\b(reference|references|refs|link|links)\b");
        }

        public static async Task<UpdateSheetReferencesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new UpdateSheetReferencesResult();
            var dd = model as DrawingDoc;
            if (dd == null) { res.Error = "Open the drawing with broken references you want to update."; return res; }

            string drwPath = null; try { drwPath = model.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(drwPath))
            { res.Error = "This drawing has never been saved, so there is no folder to search from."; return res; }
            string drwFolder = null; try { drwFolder = Path.GetDirectoryName(drwPath); } catch { }
            if (string.IsNullOrWhiteSpace(drwFolder) || !Directory.Exists(drwFolder))
            { res.Error = "Couldn't read the folder this drawing lives in."; return res; }

            var searchFolders = new List<string>();
            string explicitFolder = ParseSearchFolder(intent);
            if (!string.IsNullOrEmpty(explicitFolder) && Directory.Exists(explicitFolder)) searchFolders.Add(explicitFolder);
            searchFolders.Add(drwFolder);
            try
            {
                foreach (var sub in Directory.GetDirectories(drwFolder))
                    if (!searchFolders.Contains(sub, StringComparer.OrdinalIgnoreCase)) searchFolders.Add(sub);
            }
            catch { }

            await emit("Scout", "checking every view's referenced model against disk", "run", null);
            var groups = new Dictionary<string, List<View>>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            var view = dd.GetFirstView() as IView; // sheet itself, per SW's own linked-list convention (GT's independent walk starts the same way)
            bool first = true;
            while (view != null)
            {
                if (!first)
                {
                    total++;
                    string rm = null; try { rm = view.GetReferencedModelName(); } catch { }
                    if (!string.IsNullOrEmpty(rm))
                    {
                        string resolved = rm;
                        try { if (!Path.IsPathRooted(resolved)) resolved = Path.Combine(drwFolder, resolved); } catch { }
                        bool exists = false; try { exists = File.Exists(resolved); } catch { }
                        if (!exists)
                        {
                            if (!groups.TryGetValue(rm, out var list)) { list = new List<View>(); groups[rm] = list; }
                            list.Add(view as View);
                        }
                    }
                }
                first = false;
                view = view.GetNextView() as IView;
            }
            res.TotalViews = total;
            res.MissingFound = groups.Sum(g => g.Value.Count);
            await emit("Scout", null, "done", groups.Count + " distinct missing model(s), " + res.MissingFound + " view(s)");

            if (groups.Count == 0)
            {
                res.AlreadyDone = true; res.Verified = true;
                res.Info = "No missing/stale view references found — nothing to update.";
                return res;
            }

            await emit("Scribe", "searching " + searchFolders.Count + " folder(s) for the missing model(s)", "run", null);

            // ---- PHASE 1: apply every group's ReplaceViewModel swap first (no per-group verification yet). Found
            // live: GetReferencedModelName() on a freshly-swapped view still reports the OLD stored name for a
            // while after ReplaceViewModel + ForceRebuild3 — even several rebuild+delay retries in a row — but DOES
            // reflect the swap once the document has been through a real Save3. So verification is deferred to a
            // single PHASE 2 pass after ONE save, rather than trying to catch each group's own transient lag. ----
            var candidateByRow = new Dictionary<SheetRefRow, string>();
            foreach (var kv in groups)
            {
                string storedName = kv.Key;
                var row = new SheetRefRow { StoredModel = storedName, ViewCount = kv.Value.Count };
                res.Rows.Add(row);

                string fileName = Path.GetFileName(storedName);
                string candidate = null;
                foreach (var folder in searchFolders)
                {
                    string tryPath = null; try { tryPath = Path.Combine(folder, fileName); } catch { continue; }
                    if (File.Exists(tryPath)) { candidate = tryPath; break; }
                }
                if (candidate == null)
                { row.Error = "no candidate found for " + fileName + " (searched " + searchFolders.Count + " folder(s))"; res.StillMissing += kv.Value.Count; continue; }
                row.FoundPath = candidate;

                try
                {
                    object viewsArr = kv.Value.Cast<object>().ToArray();
                    // ReplaceViewModel's own bool return is NOT trusted alone — found live returning false even
                    // though the swap actually took effect (confirmed by GT's independent re-read post-save), the
                    // same class of landmine as tool 129's self-heal false-negative. PHASE 2 below (independent
                    // re-read of GetReferencedModelName() after rebuild+save) is the real fail-closed verification,
                    // so always advance to it rather than aborting on the API's own claim.
                    try { dd.ReplaceViewModel(candidate, viewsArr, null); } catch { }
                    candidateByRow[row] = candidate;
                }
                catch (Exception ex) { row.Error = "threw (" + ex.GetType().Name + ")"; res.StillMissing += kv.Value.Count; }
            }

            try { model.ForceRebuild3(true); } catch { }
            int se = 0, sw = 0; bool saved = false;
            try { saved = model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref se, ref sw); } catch { }

            // ---- PHASE 2: ONE fresh walk of the saved document's CURRENT views, counting how many resolve to each
            // row's candidate. Fresh objects only (the View refs captured pre-swap go stale, same lesson tools
            // 128/129 already applied to Component2). ----
            var countByCandidate = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            {
                var freshView = dd.GetFirstView() as IView; bool freshFirst = true;
                while (freshView != null)
                {
                    if (!freshFirst)
                    {
                        string rmf = null; try { rmf = freshView.GetReferencedModelName(); } catch { }
                        string resolvedf = rmf;
                        try { if (!string.IsNullOrEmpty(resolvedf) && !Path.IsPathRooted(resolvedf)) resolvedf = Path.Combine(drwFolder, resolvedf); } catch { }
                        if (!string.IsNullOrEmpty(resolvedf))
                        {
                            string full = null; try { full = Path.GetFullPath(resolvedf); } catch { full = resolvedf; }
                            countByCandidate[full] = countByCandidate.TryGetValue(full, out var c) ? c + 1 : 1;
                        }
                    }
                    freshFirst = false;
                    freshView = freshView.GetNextView() as IView;
                }
            }
            foreach (var row in candidateByRow.Keys.ToList())
            {
                string candidate = candidateByRow[row];
                string fullCandidate = null; try { fullCandidate = Path.GetFullPath(candidate); } catch { fullCandidate = candidate; }
                int nowOnCandidate = countByCandidate.TryGetValue(fullCandidate, out var c) ? c : 0;
                if (nowOnCandidate < row.ViewCount)
                { row.Error = "nowOnCandidate=" + nowOnCandidate + " expected>=" + row.ViewCount + " candidate=" + candidate; res.StillMissing += row.ViewCount; continue; }
                row.Repaired = true;
                res.Repaired += row.ViewCount;
            }

            res.Diag = "missing=" + res.MissingFound + " repaired=" + res.Repaired + " stillMissing=" + res.StillMissing + " saved=" + saved;
            res.Verified = res.StillMissing == 0 && res.Repaired == res.MissingFound && saved;
            await emit("Scribe", null, res.Verified ? "done" : "fail",
                res.Verified ? "updated " + res.Repaired + " view(s) across " + groups.Count + " missing model(s)" : res.Diag);

            if (!res.Verified)
            { res.Error = "Updated some, but not everything verified clean (" + res.Diag + ")."; return res; }

            res.Info = "Updated " + res.Repaired + " view(s) across " + groups.Count + " missing model(s).";
            return res;
        }

        // an EXPLICIT absolute/UNC folder named in the request ("...using files from C:\Library"); a bare/relative
        // name is deliberately NOT supported here (same reasoning as tool 132) — a wrong guess would silently
        // relink to the wrong part, so only an unambiguous path is trusted, otherwise fall back to the drawing's
        // own folder + immediate subfolders.
        private static string ParseSearchFolder(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var m = Regex.Match(intent, @"\b(?:from|in)\s+(?:the\s+)?[""']?([A-Za-z]:\\[^""']+|\\\\[^""']+)[""']?\s*$", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
    }
}
