using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AssemblyFeatureRow
    {
        public string Name;
        public string FeatureType;
        public string Family;   // "hole" | "fillet" | "chamfer" | "draft" | "shell"
    }

    public class HandleAssemblyFeaturesResult
    {
        public bool Success;
        public int FeaturesScanned;
        public int AssemblyFeatureCount;
        public List<AssemblyFeatureRow> Rows = new List<AssemblyFeatureRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// HandleAssemblyFeatures (tool 250, READ) — "cuts/holes made at ASSEMBLY level (not in parts): detect before
    /// mirror/pattern/export — they don't travel with components and vanish silently." A cut, hole, fillet,
    /// chamfer, or draft can be applied directly in an assembly document, spanning multiple components at once
    /// (Insert > Assembly Feature). Unlike a feature built inside a part, an assembly-level feature lives ONLY in
    /// the assembly's own FeatureManager — it has no representation in any component's own file. Mirror a
    /// component, pattern it, or export it standalone and the assembly-level cut/hole silently isn't there; the
    /// part looks solid where the assembly showed it machined. This tool exists to catch that BEFORE the
    /// mirror/pattern/export runs, not after the user notices the geometry is wrong.
    ///
    /// Detection: walk the ASSEMBLY document's own top-level features (FirstFeature/GetNextFeature — components
    /// themselves are not features, so this walk only ever sees assembly-owned features: reference geometry,
    /// mates, and any assembly-level solid-modifying feature) and classify by `GetTypeName2()` using the same
    /// family map `FindFeaturesByType`/`DeleteFeature` already use — cut-extrudes report `"ICE"` not `"Cut"` on
    /// this R2026x build (measured, tool 169's landmine), holes `"HoleWzd"`/`"SimpleHole"`/contain `"Hole"`,
    /// fillets/chamfers/drafts by their own type name. Mates, reference planes/axes, sketches, folders and
    /// components are explicitly NOT solid-modifying and excluded.
    ///
    /// Assembly-only: opening this on a part is a category error (a part's own features are never "assembly
    /// features" by this tool's definition) — fail closed with a clear redirect rather than silently reporting 0.
    ///
    /// READ-ONLY: nothing is opened, changed, rebuilt or saved. Detect + report, never auto-fix.
    /// </summary>
    public static class HandleAssemblyFeatures
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool noun = Regex.IsMatch(c, @"\bassembly[\s-]?(level|wide)?\s*(feature|cut|hole|fillet|chamfer|draft)s?\b")
                        || Regex.IsMatch(c, @"\b(feature|cut|hole)s?\s+(made|created|done|applied)\s+(at|on)\s+the\s+assembly\b");
            bool alsoAskish = Regex.IsMatch(c, @"\b(check|detect|find|list|show|report|are there|is there|any|scan|before\s+(i\s+)?(mirror|pattern|export))\b");
            return noun && (alsoAskish || Regex.IsMatch(c, @"\bassembly[\s-]?feature"));
        }

        public static bool IsSolidModifyingType(string tn, out string family)
        {
            family = null;
            if (string.IsNullOrEmpty(tn)) return false;
            if (tn.IndexOf("HoleWzd", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tn.IndexOf("Hole", StringComparison.OrdinalIgnoreCase) >= 0) { family = "hole"; return true; }
            if (tn.Equals("ICE", StringComparison.OrdinalIgnoreCase) ||
                tn.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) >= 0) { family = "hole"; return true; }
            if (tn.IndexOf("Fillet", StringComparison.OrdinalIgnoreCase) >= 0) { family = "fillet"; return true; }
            if (tn.IndexOf("Chamfer", StringComparison.OrdinalIgnoreCase) >= 0) { family = "chamfer"; return true; }
            if (tn.IndexOf("Draft", StringComparison.OrdinalIgnoreCase) >= 0) { family = "draft"; return true; }
            if (tn.IndexOf("Shell", StringComparison.OrdinalIgnoreCase) >= 0) { family = "shell"; return true; }
            return false;
        }

        public static async Task<HandleAssemblyFeaturesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new HandleAssemblyFeaturesResult();
            if (model == null) { res.Error = "Open an assembly to check for assembly-level features."; return res; }
            int docType = 0; try { docType = model.GetType(); } catch { }
            if (docType != (int)swDocumentTypes_e.swDocASSEMBLY)
            { res.Error = "Assembly-level features only apply to assemblies — open the .SLDASM, not a single part."; return res; }

            await emit("Sentinel", "walking assembly-owned features for solid-modifying (cut/hole/fillet/chamfer/draft) types", "run", null);

            var rows = new List<AssemblyFeatureRow>();
            int scanned = 0;

            Feature feat = null;
            try { feat = model.FirstFeature() as Feature; } catch { }
            while (feat != null)
            {
                scanned++;
                string tn = null, name = null;
                try { tn = feat.GetTypeName2(); } catch { }
                try { name = feat.Name; } catch { }
                if (IsSolidModifyingType(tn, out string family))
                    rows.Add(new AssemblyFeatureRow { Name = name, FeatureType = tn, Family = family });
                Feature next = null; try { next = feat.GetNextFeature() as Feature; } catch { }
                feat = next;
            }

            res.FeaturesScanned = scanned;
            res.AssemblyFeatureCount = rows.Count;
            res.Rows = rows;
            res.Success = true;

            res.Info = rows.Count == 0
                ? "No assembly-level solid-modifying features found — every cut/hole/fillet in this assembly lives inside its own component part, so mirror/pattern/export will carry them along fine."
                : rows.Count + " assembly-level feature(s) found (" +
                  string.Join(", ", rows.Select(r => (r.Name ?? "?") + " [" + r.Family + "]")) +
                  ") — these exist ONLY in this assembly's own tree, not in any component file. Mirroring, patterning, or exporting a component standalone will silently drop them; resolve or account for each before that operation.";
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }
    }
}
