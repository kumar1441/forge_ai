using System;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ScoutResult
    {
        public int TopLevel;      // top-level components
        public int Total;         // all components (incl. subassembly children + pattern instances)
        public int Mates;         // mate features in the Mates folder
        public int Fasteners;     // components that look like bolts/nuts/screws/washers
        public int OverDefined;   // components reporting over/no-solution/invalid
        public int RebuildErrors; // GetWhatsWrongCount
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Scout - a READ-ONLY assembly scan (the seed of demo #12 "assembly doctor"). Reports what's in the
    /// assembly: component / mate / fastener counts, over-defined parts, and rebuild flags. Never writes.
    /// Every number it reports is independently re-derivable by the harness (GroundTruth) for cross-checking.
    /// </summary>
    public static class Scout
    {
        public static bool IsScanIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(cmd,
                @"\b(scan|diagnose|assembly doctor|inspect|inventory|health check|what.?s in|whats in|check this assembly)\b");
        }

        public static async Task<ScoutResult> Run(ISldWorks app, IModelDoc2 model, Func<string, string, string, string, Task> emit)
        {
            var res = new ScoutResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open an assembly to scan."; return res; }

            await emit("Scout", "reading the assembly", "run", null);
            object[] top = asm.GetComponents(true) as object[];
            object[] all = asm.GetComponents(false) as object[];
            // Count ACTIVE (non-suppressed) components only — a suppressed component is deactivated, not really
            // "in" the working assembly, and every doer-handler skips them. Keeps Scout consistent with GroundTruth.
            int topActive = 0;
            foreach (var o in top ?? new object[0]) { var c = o as Component2; if (c == null) continue; bool s = false; try { s = c.IsSuppressed(); } catch { } if (!s) topActive++; }
            res.TopLevel = topActive;
            int fast = 0, over = 0, active = 0;
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2;
                if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                active++;
                string nm = null; try { nm = c.Name2; } catch { }
                if (LooksFastener(nm)) fast++;
                int st = 0; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) over++;
            }
            res.Total = active;
            res.Fasteners = fast;
            res.OverDefined = over;
            res.Mates = CountMates(model);
            await emit("Scout", null, "done",
                res.Total + " components (" + res.TopLevel + " top-level) · " + res.Mates + " mates · " + res.Fasteners + " fasteners");

            await emit("Sentinel", "checking assembly health", "run", null);
            try { res.RebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }
            await emit("Sentinel", null, "done",
                (res.OverDefined == 0 ? "no over-defined parts" : res.OverDefined + " over-defined") +
                " · " + (res.RebuildErrors == 0 ? "rebuild clean" : res.RebuildErrors + " rebuild flags"));

            res.Info = "Scanned: " + res.Total + " components, " + res.Mates + " mates, " + res.Fasteners +
                       " fasteners, " + res.OverDefined + " over-defined, " + res.RebuildErrors + " rebuild flags.";
            return res;
        }

        // Canonical fastener vocabulary — kept IN SYNC with GroundTruth.IsFastenerName so the handler's count and
        // the independent count agree on any model. Broadened from the old narrow list ("iso 40"/"din 9") to catch
        // standard ISO/DIN Toolbox fasteners, after a shaft-coupling scan showed the old list missed 6 of them.
        internal static readonly string[] FastenerHints =
            { "bolt", "screw", "nut", "washer", "hcs", "shcs", "cap", "socket", "hex", "stud", "vis", "boulon", "bulong", "ecrou", "rondelle", "iso", "din", "b18" };

        private static bool LooksFastener(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            foreach (var h in FastenerHints) if (n.Contains(h)) return true;
            return false;
        }

        // count mate features by walking the Mates folder (tree traversal works on this 3DEXPERIENCE build)
        private static int CountMates(IModelDoc2 model)
        {
            int t = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "MateGroup")
                    {
                        var s = f.GetFirstSubFeature() as Feature;
                        while (s != null) { t++; s = s.GetNextSubFeature() as Feature; }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return t;
        }
    }
}
