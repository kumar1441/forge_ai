using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class UnknownFeatureRow
    {
        public string Name;
        public string ProgId;
        public string Provider;
        public string BaseName;
    }

    public class HandleUnknownFeaturesResult
    {
        public bool Success;
        public int FeaturesScanned;
        public int UnknownFeatureCount;
        public List<UnknownFeatureRow> Rows = new List<UnknownFeatureRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// HandleUnknownFeatures (tool 243, READ) — "third-party plugin features in the tree (CAMWorks, DriveWorks,
    /// sim add-ins): detect, NEVER modify, route operations around them; unknown feature type = hard stop, not
    /// a guess." SolidWorks' own mechanism for a third-party add-in injecting a custom feature into the tree is
    /// the MACRO FEATURE (`IFeatureManager.InsertMacroFeature3` at authoring time) — every native SolidWorks
    /// feature (Extrusion, Fillet, Sketch, etc.) reports its own specific type name from `GetTypeName2()`; a
    /// macro feature always reports the literal string `"MacroFeature"` regardless of which add-in created it,
    /// and stays in the tree unable to regenerate/edit if that add-in isn't installed on the seat that opens it
    /// — the exact "unknown feature type" state this tool exists to catch. `IFeature.GetDefinition()` on such a
    /// feature returns an `IMacroFeatureData`, which carries the responsible add-in's `GetProgId()`/`Provider`/
    /// `GetBaseName()` for the report — never used to attempt an edit.
    ///
    /// Top-level walk only (FirstFeature/GetNextFeature); GT re-derives via FeatureManager.GetFeatures(false), a
    /// different traversal primitive, same scope split as DetectInContextWrites (tool 242).
    ///
    /// READ-ONLY: nothing is opened, changed, rebuilt or saved. Detect + report, NEVER touch the feature itself.
    /// </summary>
    public static class HandleUnknownFeatures
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool noun = Regex.IsMatch(c, @"\b(unknown|unrecognized|unrecognised|third[\s-]?party|plug[\s-]?in|camworks|driveworks|macro)\s+feature");
            bool alsoAskish = Regex.IsMatch(c, @"\b(check|detect|find|list|show|report|are there|is there|any|scan)\b");
            return noun && alsoAskish || Regex.IsMatch(c, @"\bmacro\s+feature");
        }

        public static async Task<HandleUnknownFeaturesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new HandleUnknownFeaturesResult();
            if (model == null) { res.Error = "Open a document to check for unknown/third-party features."; return res; }

            await emit("Sentinel", "walking features for non-native (third-party plugin) types", "run", null);

            var rows = new List<UnknownFeatureRow>();
            int scanned = 0;

            Feature feat = null;
            try { feat = model.FirstFeature() as Feature; } catch { }
            while (feat != null)
            {
                scanned++;
                string tn = null;
                try { tn = feat.GetTypeName2(); } catch { }
                if (string.Equals(tn, "MacroFeature", StringComparison.OrdinalIgnoreCase))
                {
                    var row = new UnknownFeatureRow();
                    try { row.Name = feat.Name; } catch { }
                    try
                    {
                        var def = feat.GetDefinition() as IMacroFeatureData;
                        if (def != null)
                        {
                            try { row.ProgId = def.GetProgId(); } catch { }
                            try { row.Provider = def.Provider; } catch { }
                            try { row.BaseName = def.GetBaseName(); } catch { }
                        }
                    }
                    catch { }
                    rows.Add(row);
                }
                Feature next = null; try { next = feat.GetNextFeature() as Feature; } catch { }
                feat = next;
            }

            res.FeaturesScanned = scanned;
            res.UnknownFeatureCount = rows.Count;
            res.Rows = rows;
            res.Success = true;

            res.Info = rows.Count == 0
                ? "No unknown / third-party plugin feature types found — every feature in this tree is native SolidWorks."
                : rows.Count + " unknown/third-party feature(s) found (" +
                  string.Join(", ", rows.ConvertAll(r => (r.Name ?? "?") + (string.IsNullOrEmpty(r.Provider) ? "" : " [" + r.Provider + "]"))) +
                  ") — never modify these directly; route any requested operation around them and flag the gap honestly.";
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }
    }
}
