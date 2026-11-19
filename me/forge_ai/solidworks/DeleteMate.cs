using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DeleteMateResult
    {
        public string MateName;
        public int MatesBefore, MatesAfter;
        public bool NotFound;      // idempotency: the named mate isn't there (already gone)
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 61 — delete_mate (WRITE, destructive). Permanently removes ONE named mate. "delete the Concentric2 mate".
    /// Distinct from suppress_mate (reversible toggle) — this deletes the feature. Walks the Mates folder, resolves the
    /// named mate (ONE question on 0/many — Rule #2), deletes it, and verifies by an INDEPENDENT re-count: the total
    /// mate count fell by exactly 1. One Ctrl+Z restores it; Forge never saves. Idempotent (not there → nothing to do).
    /// </summary>
    public static class DeleteMate
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(delete|remove|kill|drop)\b") &&
                   (Regex.IsMatch(c, @"\bmate\b") || Regex.IsMatch(c, @"\b(coincident|concentric|distance|parallel|tangent|angle|width)\s*\d*\b")) &&
                   !Regex.IsMatch(c, @"\b(component|components|part|parts|feature|features|all the mate|mate errors|red)\b");   // those are other handlers
        }

        public static async Task<DeleteMateResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DeleteMateResult();
            if (model as AssemblyDoc == null) { res.Error = "Open the assembly (.SLDASM) to delete a mate."; return res; }

            string want = null;
            var mn = Regex.Match(intent ?? "", @"\b(coincident|concentric|distance|parallel|tangent|angle|width|lock)\s*\d*\b", RegexOptions.IgnoreCase);
            if (mn.Success) want = mn.Value.Replace(" ", "");
            if (want == null) { var m2 = Regex.Match(intent ?? "", @"mate\s+([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase); if (m2.Success) want = m2.Groups[1].Value; }
            if (want == null) { var m3 = Regex.Match(intent ?? "", @"\b(?:delete|remove|kill|drop)\s+(?:the\s+)?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase); if (m3.Success && !m3.Groups[1].Value.Equals("mate", StringComparison.OrdinalIgnoreCase)) want = m3.Groups[1].Value; }
            if (string.IsNullOrWhiteSpace(want)) { res.Error = "Which mate? e.g. \"delete the Concentric2 mate\"."; return res; }

            await emit("Gauge", "finding mate '" + want + "'", "run", null);
            var mates = CollectMates(model);
            res.MatesBefore = mates.Count;

            var hits = new List<Feature>();
            foreach (var f in mates) { string nm = null; try { nm = f.Name; } catch { } if (nm != null && nm.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(f); }
            if (hits.Count == 0)
            {
                res.NotFound = true; res.Verified = true; res.MatesAfter = res.MatesBefore;
                res.Info = "No mate named '" + want + "' — nothing to delete.";
                await emit("Sentinel", null, "done", "not present — nothing to do");
                return res;
            }
            if (hits.Count > 1) { var ns = new List<string>(); foreach (var f in hits) { try { ns.Add(f.Name); } catch { } if (ns.Count >= 5) break; } res.Error = "'" + want + "' matches " + hits.Count + " mates (" + string.Join(", ", ns.ToArray()) + "…). Which one?"; await emit("Gauge", null, "fail", "ambiguous"); return res; }

            var mate = hits[0]; try { res.MateName = mate.Name; } catch { }
            await emit("Mender", "deleting '" + res.MateName + "'", "run", null);
            try
            {
                model.ClearSelection2(true);
                mate.Select2(false, 0);
                model.EditDelete();
                model.ClearSelection2(true);
            }
            catch (Exception ex) { res.Error = "Couldn't delete the mate (" + ex.GetType().Name + ") — unchanged."; await emit("Mender", null, "fail", res.Error); return res; }
            try { model.ForceRebuild3(false); } catch { }

            // ---- Sentinel: independent re-count (fail closed) ----
            await emit("Sentinel", "verifying", "run", null);
            res.MatesAfter = CollectMates(model).Count;
            res.Verified = res.MatesAfter == res.MatesBefore - 1;
            if (!res.Verified)
            {
                res.Error = "Mate count didn't fall by 1 (" + res.MatesBefore + " → " + res.MatesAfter + ") — the delete didn't apply.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "deleted '" + res.MateName + "' (" + res.MatesBefore + " → " + res.MatesAfter + " mates)");
            res.Info = "Deleted mate '" + res.MateName + "' (" + res.MatesBefore + " → " + res.MatesAfter + " mates). One Ctrl+Z restores it; Forge didn't save.";
            return res;
        }

        private static List<Feature> CollectMates(IModelDoc2 model)
        {
            var list = new List<Feature>();
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "MateGroup")
                    {
                        var s = f.GetFirstSubFeature() as Feature;
                        while (s != null) { list.Add(s); s = s.GetNextSubFeature() as Feature; }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return list;
        }
    }
}
