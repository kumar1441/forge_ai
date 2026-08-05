using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GhostMateRow
    {
        public string Mate;
        public string Component;      // the suppressed component the mate still points at
        public bool MateSuppressed;   // SW usually cascades, but a LIVE mate on a dead component is the loud case
    }

    public class DetectGhostReferencesResult
    {
        public int TotalMates;
        public int SuppressedComponents;
        public int GhostMates;        // mates referencing a suppressed component
        public int LiveGhostMates;    // ...that are themselves still unsuppressed — the dangerous subset
        public List<GhostMateRow> Rows = new List<GhostMateRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 247 — detect_ghost_references (READ). The "it works but it's haunted" state: mates still pointing at
    /// components that have been suppressed. The assembly rebuilds clean and reports zero errors, so nothing flags it
    /// — but un-suppress that component later and the mates come back with it, sometimes conflicting with whatever was
    /// mated in the meantime.
    ///
    /// This is the pre-pass fix_red_wave (H-2) needs: a "red wave" that is actually ghost references is a different
    /// problem from an over-defined one, and treating the first as the second removes healthy mates. Mates are read by
    /// feature-tree traversal of the Mates folder, because IComponent2.GetMates and AssemblyDoc.GetMates are dead on
    /// this build (see docs/SOLIDWORKS-GOTCHAS.md). Read-only.
    /// </summary>
    public static class DetectGhostReferences
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(ghost|haunted|stale|orphan(ed)?|dangling|leftover)\b") &&
                   Regex.IsMatch(c, @"\b(reference|references|refs|mate|mates|component|components)\b");
        }

        public static async Task<DetectGhostReferencesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DetectGhostReferencesResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open an assembly (.SLDASM) to hunt for ghost references."; return res; }

            await emit("Sentinel", "checking mates against suppressed components", "run", null);

            var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (!sup) continue;
                string n = null; try { n = c.Name2; } catch { }
                if (!string.IsNullOrEmpty(n)) suppressed.Add(n);
            }
            res.SuppressedComponents = suppressed.Count;

            // Mates folder by tree traversal — the mate-READ APIs are dead on this build
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "MateGroup")
                {
                    var s = f.GetFirstSubFeature() as Feature;
                    while (s != null)
                    {
                        res.TotalMates++;
                        string mateName = null; try { mateName = s.Name; } catch { }
                        bool mateSup = false; try { mateSup = s.IsSuppressed(); } catch { }

                        var hits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            var mate = s.GetSpecificFeature2() as Mate2;
                            if (mate != null)
                            {
                                int n = 0; try { n = mate.GetMateEntityCount(); } catch { }
                                for (int i = 0; i < n; i++)
                                {
                                    try
                                    {
                                        var me = mate.MateEntity(i) as MateEntity2;
                                        var comp = me == null ? null : me.ReferenceComponent as Component2;
                                        string cn = null; if (comp != null) try { cn = comp.Name2; } catch { }
                                        if (!string.IsNullOrEmpty(cn) && suppressed.Contains(cn)) hits.Add(cn);
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }

                        if (hits.Count > 0)
                        {
                            res.GhostMates++;
                            if (!mateSup) res.LiveGhostMates++;
                            foreach (var cn in hits)
                                res.Rows.Add(new GhostMateRow { Mate = mateName, Component = cn, MateSuppressed = mateSup });
                        }
                        s = s.GetNextSubFeature() as Feature;
                    }
                }
                f = f.GetNextFeature() as Feature;
            }

            await emit("Sentinel", null, "done", res.GhostMates + " of " + res.TotalMates + " mates point at suppressed components");

            if (res.SuppressedComponents == 0)
            { res.Info = "No suppressed components — nothing can be haunting this assembly. " + res.TotalMates + " mates, all pointing at live geometry."; return res; }

            if (res.GhostMates == 0)
            { res.Info = res.SuppressedComponents + " component" + (res.SuppressedComponents == 1 ? " is" : "s are") + " suppressed, but no mate references " + (res.SuppressedComponents == 1 ? "it" : "them") + " — clean."; return res; }

            var sb = new StringBuilder(res.GhostMates + " of " + res.TotalMates + " mates still point at suppressed components" +
                                       (res.LiveGhostMates > 0 ? ", " + res.LiveGhostMates + " of them still active" : ", all suppressed alongside") + ":");
            int shown = 0;
            foreach (var r in res.Rows)
            {
                if (shown++ >= 24) { sb.Append("\n… (" + (res.Rows.Count - 24) + " more)"); break; }
                sb.Append("\n• " + r.Mate + " → " + r.Component + (r.MateSuppressed ? " (mate suppressed too)" : " (mate still ACTIVE)"));
            }
            sb.Append(res.LiveGhostMates > 0
                ? "\nThe active ones are the risk — they constrain geometry that isn't there."
                : "\nThese come back if those components are un-suppressed; check them against whatever was mated since.");
            res.Info = sb.ToString();
            return res;
        }
    }
}
