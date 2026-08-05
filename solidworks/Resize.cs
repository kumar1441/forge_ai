using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ResizeResult
    {
        public int Found;
        public int Switched;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Bulk resize fasteners — "change all M6 to M8". THROW #1: fasteners are usually Toolbox parts with
    /// a configuration per size, so switch each M6 component to its M8 configuration. Instrumented to
    /// report each fastener's available configs — if there's no M8 config, we learn the model can't do it
    /// this way (and we throw component-replacement next / ask for a Toolbox model).
    /// </summary>
    public static class Resizer
    {
        private static readonly string[] Hints = { "iso", "bolt", "screw", "vis", "boulon", "din", "hcs", "shcs", "cap", "socket", "hex", "stud" };

        public static bool IsResizeIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return Regex.Matches(cmd, @"\bm(\d+)\b").Count >= 2 &&
                   Regex.IsMatch(cmd, @"\b(change|resize|upsize|swap|convert|replace|make|bump|upgrade|to|with)\b");
        }

        private static bool IsFastenerName(string n)
        {
            foreach (var h in Hints) if (n.Contains(h)) return true;
            return false;
        }

        public static async Task<ResizeResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ResizeResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open an assembly to resize fasteners."; return res; }
            string cmd = (intent ?? "").ToLowerInvariant();

            var sizes = Regex.Matches(cmd, @"\bm(\d+)\b");
            if (sizes.Count < 2) { res.Error = "Say e.g. \"change all M6 to M8\"."; return res; }
            string from = "m" + sizes[0].Groups[1].Value;
            string to = "m" + sizes[1].Groups[1].Value;

            await emit("Ripple", "finding " + from.ToUpper() + " fasteners", "run", null);
            object[] comps = asm.GetComponents(false) as object[];
            var targets = new List<Component2>();
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2;
                if (c == null || c.IsSuppressed()) continue;
                string nm = (c.Name2 ?? "").ToLowerInvariant();
                if (!IsFastenerName(nm)) continue;
                // the size lives in the Toolbox CONFIG (e.g. "M6x30"), not the component name — match there first.
                string cfgNm = "";
                try { cfgNm = (((dynamic)c).ReferencedConfiguration as string ?? "").ToLowerInvariant(); } catch { }
                // "m6" but not "m60"; also matches "m6x30" where \bm6\b would fail (no boundary between 6 and x).
                string fromPat = @"\b" + from + @"(?![0-9])";
                if (Regex.IsMatch(cfgNm, fromPat) || Regex.IsMatch(nm, fromPat)) targets.Add(c);
            }
            res.Found = targets.Count;

            string diag = "";
            if (targets.Count > 0)
            {
                try
                {
                    var pd = targets[0].GetModelDoc2() as IModelDoc2;
                    string[] cfgs = pd?.GetConfigurationNames() as string[];
                    diag = "cfgs=[" + (cfgs != null ? string.Join(",", cfgs) : "none") + "]";
                }
                catch { diag = "cfgErr"; }
            }
            await emit("Ripple", null, "done", "found " + res.Found + " " + from.ToUpper() + " fasteners · " + diag);
            if (targets.Count == 0) { res.Error = "No " + from.ToUpper() + " fasteners found."; return res; }

            await emit("Ripple", "resizing to " + to.ToUpper(), "run", null);
            int noCfg = 0;
            foreach (var c in targets)
            {
                try
                {
                    var pd = c.GetModelDoc2() as IModelDoc2;
                    string[] cfgs = pd?.GetConfigurationNames() as string[];
                    string toCfg = null;
                    if (cfgs != null)
                        foreach (var cf in cfgs)
                            if (Regex.IsMatch(cf.ToLowerInvariant(), @"\b" + to + @"(?![0-9])")) { toCfg = cf; break; }
                    if (toCfg == null) { noCfg++; continue; }
                    try { ((dynamic)c).ReferencedConfiguration = toCfg; res.Switched++; } catch { }
                }
                catch { }
            }
            model.EditRebuild3();
            await emit("Ripple", null, "done",
                res.Switched + " switched to " + to.ToUpper() + (noCfg > 0 ? ", " + noCfg + " had no " + to.ToUpper() + " config" : ""));

            res.Info = res.Switched == 0
                ? "Found " + res.Found + " " + from.ToUpper() + " fasteners, but none have a " + to.ToUpper() + " configuration to switch to. " + diag
                : "Resized " + res.Switched + " fasteners " + from.ToUpper() + " → " + to.ToUpper() + ".";
            return res;
        }
    }
}
