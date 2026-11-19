using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SuppressMateResult
    {
        public string MateName;
        public bool Unsuppress;       // the command asked to UN-suppress
        public bool AlreadyInState;   // idempotency: mate already in the requested state
        public int SuppressedBefore, SuppressedAfter;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 60 — suppress_mate / unsuppress_mate (WRITE). Toggles ONE named mate's suppression. "suppress the
    /// Concentric2 mate", "unsuppress Distance1". Walks the Mates folder (the mate COM-read APIs are dead here),
    /// resolves the named mate (ONE question on 0/many — Rule #2), sets suppression, and verifies by an INDEPENDENT
    /// re-count of suppressed mates (fail closed). Idempotent (already in state → nothing to do), undoable, never saves.
    /// </summary>
    public static class SuppressMate
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(un)?suppress\b") && Regex.IsMatch(c, @"\bmate\b|\b(coincident|concentric|distance|parallel|tangent|angle|width)\d*\b") &&
                   !Regex.IsMatch(c, @"\b(component|components|part|parts|feature|features)\b");   // those are suppress_component/feature
        }

        public static async Task<SuppressMateResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SuppressMateResult();
            if (model as AssemblyDoc == null) { res.Error = "Open the assembly (.SLDASM) to suppress a mate."; return res; }
            string c = (intent ?? "").ToLowerInvariant();
            res.Unsuppress = Regex.IsMatch(c, @"\bunsuppress\b|\bun-suppress\b|\bre-?activate\b|\benable\b");

            // parse the mate name: an explicit token like "Concentric2", else the word after "mate"
            string want = null;
            var mn = Regex.Match(intent ?? "", @"\b(coincident|concentric|distance|parallel|tangent|angle|width|lock)\s*\d*\b", RegexOptions.IgnoreCase);
            if (mn.Success) want = mn.Value.Replace(" ", "");
            if (want == null) { var m2 = Regex.Match(intent ?? "", @"mate\s+([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase); if (m2.Success) want = m2.Groups[1].Value; }
            if (want == null) { var m3 = Regex.Match(intent ?? "", @"\b(?:suppress|unsuppress)\s+(?:the\s+)?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase); if (m3.Success && !m3.Groups[1].Value.Equals("mate", StringComparison.OrdinalIgnoreCase)) want = m3.Groups[1].Value; }
            if (string.IsNullOrWhiteSpace(want)) { res.Error = "Which mate? e.g. \"suppress the Concentric2 mate\"."; return res; }

            await emit("Gauge", "finding mate '" + want + "'", "run", null);
            var mates = CollectMates(model);
            res.SuppressedBefore = CountSuppressed(mates);

            var hits = new List<Feature>();
            foreach (var f in mates) { string nm = null; try { nm = f.Name; } catch { } if (nm != null && nm.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(f); }
            if (hits.Count == 0) { res.Error = "No mate matches '" + want + "'."; await emit("Gauge", null, "fail", "no match"); return res; }
            if (hits.Count > 1) { var ns = new List<string>(); foreach (var f in hits) { try { ns.Add(f.Name); } catch { } if (ns.Count >= 5) break; } res.Error = "'" + want + "' matches " + hits.Count + " mates (" + string.Join(", ", ns.ToArray()) + "…). Which one?"; await emit("Gauge", null, "fail", "ambiguous"); return res; }

            var mate = hits[0]; try { res.MateName = mate.Name; } catch { }
            bool isSup = false; try { isSup = mate.IsSuppressed(); } catch { }
            if (isSup == !res.Unsuppress)
            {
                res.AlreadyInState = true; res.Verified = true; res.SuppressedAfter = res.SuppressedBefore;
                res.Info = "Mate '" + res.MateName + "' is already " + (res.Unsuppress ? "active" : "suppressed") + " — nothing to do.";
                await emit("Sentinel", null, "done", "already " + (res.Unsuppress ? "active" : "suppressed"));
                return res;
            }

            await emit("Scribe", (res.Unsuppress ? "unsuppressing" : "suppressing") + " '" + res.MateName + "'", "run", null);
            int action = res.Unsuppress ? (int)swFeatureSuppressionAction_e.swUnSuppressFeature : (int)swFeatureSuppressionAction_e.swSuppressFeature;
            try { mate.SetSuppression2(action, (int)swInConfigurationOpts_e.swThisConfiguration, null); }
            catch (Exception ex) { res.Error = "Couldn't change the mate (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { model.ForceRebuild3(false); } catch { }

            // ---- Sentinel: independent re-count (fail closed) ----
            await emit("Sentinel", "verifying", "run", null);
            var after = CollectMates(model);
            res.SuppressedAfter = CountSuppressed(after);
            int delta = res.Unsuppress ? -1 : +1;
            res.Verified = res.SuppressedAfter == res.SuppressedBefore + delta;
            if (!res.Verified)
            {
                res.Error = "Suppressed-mate count didn't change as expected (" + res.SuppressedBefore + " → " + res.SuppressedAfter + ").";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "mate '" + res.MateName + "' " + (res.Unsuppress ? "active" : "suppressed") + " (" + res.SuppressedBefore + " → " + res.SuppressedAfter + " suppressed)");
            res.Info = (res.Unsuppress ? "Un-suppressed" : "Suppressed") + " mate '" + res.MateName + "'. One Ctrl+Z undoes it; Forge didn't save.";
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

        private static int CountSuppressed(List<Feature> mates)
        {
            int n = 0;
            foreach (var f in mates) { bool s = false; try { s = f.IsSuppressed(); } catch { } if (s) n++; }
            return n;
        }
    }
}
