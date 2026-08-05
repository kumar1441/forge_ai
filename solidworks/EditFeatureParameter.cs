using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class EditFeatureParameterResult
    {
        public string FeatureName;
        public string Parameter = "depth";
        public double RequestedMm = -1;
        public double BeforeMm = double.NaN, AfterMm = double.NaN;
        public bool AlreadyAtValue;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 73 — edit_feature_parameter (WRITE). Changes an extrude/boss feature's DEPTH by plain English. "change the
    /// depth of Boss-Extrude1 to 40mm", "set the extrude depth to 40". Resolves the feature (named, or the sole boss
    /// extrude — ONE question on ambiguity, Rule #2), edits its depth through the PROVEN feature-data recipe
    /// (GetDefinition → AccessSelections → IExtrudeFeatureData2.SetDepth → ModifyDefinition, the same route the green
    /// EditPatternCount/Spacing use), and verifies by an INDEPENDENT read-back of the applied depth (fail closed).
    /// Idempotent (already at the value → nothing to do); undoable; Forge never saves.
    /// </summary>
    public static class EditFeatureParameter
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // (edit|change|set|make|adjust|modify) an EXTRUDE'S DEPTH to a NUMBER. Requires "depth" AND an explicit
            // extrude reference (extrude/extrusion/boss) so a BARE "set the depth to 30" still goes to set_dimension
            // (a named dimension), while "set the extrude depth to 40" / "change the depth of Boss-Extrude1 to 40" comes
            // here. Excludes mate/pattern/config vocab. Checked BEFORE SetDimension.
            return Regex.IsMatch(c, @"\b(edit|change|set|make|adjust|modify)\b") &&
                   Regex.IsMatch(c, @"\bdepth\b") &&
                   Regex.IsMatch(c, @"\b(extrude|extrusion|boss)\b") &&
                   Regex.IsMatch(c, @"(?<![a-z0-9])\d+(\.\d+)?") &&
                   !Regex.IsMatch(c, @"\b(mate|pattern|spacing|instances|copies|config|configuration|variant|add|suppress|delete)\b");
        }

        public static async Task<EditFeatureParameterResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new EditFeatureParameterResult();
            if (model == null) { res.Error = "Open a document first."; return res; }

            double? mm = ParseDepthMm(intent);
            if (mm == null) { res.Error = "What depth? e.g. \"change the depth of Boss-Extrude1 to 40mm\"."; await emit("Gauge", null, "fail", "no value"); return res; }
            res.RequestedMm = mm.Value;

            // resolve the target extrude: by name if given, else the sole boss extrude
            string nameTok = null;
            var nm = Regex.Match(intent ?? "", @"\b(boss[- ]?extrude\d*|extrude\d+|extrusion\d*)\b", RegexOptions.IgnoreCase);
            if (nm.Success) nameTok = nm.Value.Replace(" ", "");

            await emit("Gauge", "finding the extrude to edit", "run", null);
            var extrudes = new List<Feature>();
            Feature named = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "Extrusion")
                {
                    extrudes.Add(f);
                    if (nameTok != null) { string fn = null; try { fn = f.Name; } catch { } if (fn != null && fn.Replace(" ", "").IndexOf(nameTok, StringComparison.OrdinalIgnoreCase) >= 0) named = f; }
                }
                f = f.GetNextFeature() as Feature;
            }

            Feature target = named;
            if (target == null)
            {
                if (extrudes.Count == 0) { res.Error = "No boss-extrude feature to edit."; await emit("Gauge", null, "fail", "none"); return res; }
                if (extrudes.Count > 1) { var ns = new List<string>(); foreach (var e in extrudes) { try { ns.Add(e.Name); } catch { } if (ns.Count >= 5) break; } res.Error = "Which feature? " + extrudes.Count + " extrudes (" + string.Join(", ", ns.ToArray()) + "…)."; await emit("Gauge", null, "fail", "ambiguous"); return res; }
                target = extrudes[0];
            }
            try { res.FeatureName = target.Name; } catch { }

            var d0 = target.GetDefinition() as IExtrudeFeatureData2;
            if (d0 != null) { try { res.BeforeMm = Math.Round(d0.GetDepth(true) * 1000.0, 4); } catch { } }
            if (!double.IsNaN(res.BeforeMm) && Math.Abs(res.BeforeMm - res.RequestedMm) <= 0.001)
            {
                res.AlreadyAtValue = true; res.Verified = true; res.AfterMm = res.BeforeMm;
                res.Info = "'" + res.FeatureName + "' depth is already " + res.RequestedMm + "mm — nothing to change.";
                await emit("Sentinel", null, "done", "already " + res.RequestedMm + "mm");
                return res;
            }

            await emit("Scribe", "setting '" + res.FeatureName + "' depth to " + res.RequestedMm + "mm", "run", null);
            string diag = "";
            var def = target.GetDefinition() as IExtrudeFeatureData2;
            if (def == null) { res.Error = "Couldn't read the extrude definition — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { def.AccessSelections(model, null); } catch (Exception ex) { diag += "access:EX(" + ex.GetType().Name + ") "; }
            try { def.SetDepth(true, res.RequestedMm / 1000.0); } catch (Exception ex) { diag += "set:EX(" + ex.GetType().Name + ") "; }
            bool applied = false;
            try { applied = target.ModifyDefinition(def, model, null); } catch (Exception ex) { diag += "modify:EX(" + ex.GetType().Name + ") "; }
            try { model.ForceRebuild3(false); } catch { }

            var d2 = FindByName(model, res.FeatureName)?.GetDefinition() as IExtrudeFeatureData2;
            if (d2 != null) { try { res.AfterMm = Math.Round(d2.GetDepth(true) * 1000.0, 4); } catch { } }
            res.Verified = !double.IsNaN(res.AfterMm) && Math.Abs(res.AfterMm - res.RequestedMm) <= 0.01;
            if (!res.Verified)
            {
                res.Error = "Depth didn't apply (before=" + res.BeforeMm + "mm, after=" + res.AfterMm + "mm, requested=" + res.RequestedMm + "mm, modify=" + applied + ") " + diag;
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }
            await emit("Sentinel", null, "done", res.FeatureName + ": " + res.BeforeMm + "mm → " + res.AfterMm + "mm");
            res.Info = "Set '" + res.FeatureName + "' depth to " + res.AfterMm + "mm (was " + res.BeforeMm + "mm). One Ctrl+Z undoes it; Forge didn't save.";
            return res;
        }

        private static double? ParseDepthMm(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var m = Regex.Match(intent, @"(\d+(?:\.\d+)?)\s*(mm|cm|m\b|in\b|inch\w*|"")", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                string u = m.Groups[2].Value.ToLowerInvariant();
                if (u == "cm") return v * 10.0;
                if (u == "m") return v * 1000.0;
                if (u.StartsWith("in") || u == "\"") return v * 25.4;
                return v;
            }
            var m2 = Regex.Match(intent, @"(?<![a-z0-9])(\d+(?:\.\d+)?)");
            if (!m2.Success) return null;
            return double.Parse(m2.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        private static Feature FindByName(IModelDoc2 model, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var f = model.FirstFeature() as Feature;
            while (f != null) { string n = null; try { n = f.Name; } catch { } if (n == name) return f; f = f.GetNextFeature() as Feature; }
            return null;
        }
    }
}
