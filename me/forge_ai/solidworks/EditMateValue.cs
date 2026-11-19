using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class EditMateValueResult
    {
        public string MateName;
        public string Kind;              // "distance" or "angle"
        public double RequestedMm = -1;  // for a distance mate
        public double RequestedDeg = -1; // for an angle mate
        public double BeforeVal = double.NaN, AfterVal = double.NaN;  // in mm (distance) or deg (angle)
        public bool AlreadyAtValue;      // idempotency
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 59 — edit_mate_value (WRITE). Changes the value of ONE existing distance or angle mate. "change the
    /// distance mate to 25mm", "set the angle mate to 45 degrees". Finds the mate (by name, or the sole distance/angle
    /// mate — ONE question on ambiguity, Rule #2), edits its value through the PROVEN feature-data recipe
    /// (GetDefinition → AccessSelections → set .Distance/.Angle → ModifyDefinition, the same route EditPatternCount/
    /// Spacing use), and verifies by an INDEPENDENT read-back of the applied value (fail closed). Idempotent (already at
    /// the value → nothing to do); undoable; Forge never saves.
    /// </summary>
    public static class EditMateValue
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // (edit|change|set|update|adjust|modify) a MATE to a NUMBER. Excludes the ADD verbs (add_*_mate) and the
            // other mate verbs. Requires "mate" so it never touches set_dimension / component / config edits.
            return Regex.IsMatch(c, @"\b(edit|change|set|update|adjust|modify)\b") &&
                   Regex.IsMatch(c, @"\bmate\b") &&
                   Regex.IsMatch(c, @"(?<![a-z0-9])\d+(\.\d+)?") &&
                   !Regex.IsMatch(c, @"\b(add|create|insert|place|put|suppress|unsuppress|delete|remove|drop|info|list)\b");
        }

        public static async Task<EditMateValueResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new EditMateValueResult();
            if (model as AssemblyDoc == null) { res.Error = "Open the assembly (.SLDASM) to edit a mate."; return res; }
            string c = (intent ?? "").ToLowerInvariant();

            bool wantAngle = Regex.IsMatch(c, @"\bangle\b") || Regex.IsMatch(c, @"\bdegree|°\b");
            bool wantDistance = Regex.IsMatch(c, @"\bdistance\b") || Regex.IsMatch(c, @"\b(mm|cm|apart)\b");

            // parse an explicit mate name if present (e.g. "Distance1", "Angle2")
            string nameTok = null;
            var nm = Regex.Match(intent ?? "", @"\b(distance|angle)\s*\d+\b", RegexOptions.IgnoreCase);
            if (nm.Success) nameTok = nm.Value.Replace(" ", "");

            await emit("Gauge", "finding the mate to edit", "run", null);
            var mates = CollectMates(model);

            // choose the target mate
            Feature target = null; swMateType_e targetType = swMateType_e.swMateUNKNOWN;
            var distMates = new List<Feature>(); var angMates = new List<Feature>();
            foreach (var f in mates)
            {
                Mate2 m = null; try { m = f.GetSpecificFeature2() as Mate2; } catch { }
                if (m == null) continue;
                var t = (swMateType_e)m.Type;
                if (t == swMateType_e.swMateDISTANCE) distMates.Add(f);
                else if (t == swMateType_e.swMateANGLE) angMates.Add(f);
                if (nameTok != null) { string fn = null; try { fn = f.Name; } catch { } if (fn != null && fn.IndexOf(nameTok, StringComparison.OrdinalIgnoreCase) >= 0) { target = f; targetType = t; } }
            }

            if (target == null)
            {
                List<Feature> pool = wantAngle ? angMates : (wantDistance ? distMates : (distMates.Count > 0 ? distMates : angMates));
                if (pool.Count == 0) { res.Error = "No " + (wantAngle ? "angle" : "distance") + " mate to edit."; await emit("Gauge", null, "fail", "none found"); return res; }
                if (pool.Count > 1) { var ns = new List<string>(); foreach (var f in pool) { try { ns.Add(f.Name); } catch { } if (ns.Count >= 5) break; } res.Error = "Which mate? " + pool.Count + " match (" + string.Join(", ", ns.ToArray()) + "…)."; await emit("Gauge", null, "fail", "ambiguous"); return res; }
                target = pool[0];
                Mate2 tm = null; try { tm = target.GetSpecificFeature2() as Mate2; } catch { }
                targetType = tm != null ? (swMateType_e)tm.Type : swMateType_e.swMateUNKNOWN;
            }
            try { res.MateName = target.Name; } catch { }

            if (targetType == swMateType_e.swMateDISTANCE) return await EditDistance(model, target, intent, res, emit);
            if (targetType == swMateType_e.swMateANGLE) return await EditAngle(model, target, intent, res, emit);
            res.Error = "That mate has no editable value (only distance/angle mates do)."; await emit("Gauge", null, "fail", "not a value mate"); return res;
        }

        private static async Task<EditMateValueResult> EditDistance(IModelDoc2 model, Feature target, string intent, EditMateValueResult res, Func<string, string, string, string, Task> emit)
        {
            res.Kind = "distance";
            double? mm = ParseDistanceMm(intent);
            if (mm == null) { res.Error = "What distance? e.g. \"change the distance mate to 25mm\"."; await emit("Gauge", null, "fail", "no value"); return res; }
            res.RequestedMm = mm.Value;

            var d0 = target.GetDefinition() as IDistanceMateFeatureData;
            if (d0 != null) { try { res.BeforeVal = Math.Round(d0.Distance * 1000.0, 4); } catch { } }
            if (!double.IsNaN(res.BeforeVal) && Math.Abs(res.BeforeVal - res.RequestedMm) <= 0.01)
            {
                res.AlreadyAtValue = true; res.Verified = true; res.AfterVal = res.BeforeVal;
                res.Info = "'" + res.MateName + "' is already " + res.RequestedMm + "mm — nothing to change.";
                await emit("Sentinel", null, "done", "already " + res.RequestedMm + "mm");
                return res;
            }

            await emit("Scribe", "setting '" + res.MateName + "' to " + res.RequestedMm + "mm", "run", null);
            string diag = "";
            var def = target.GetDefinition() as IDistanceMateFeatureData;
            if (def == null) { res.Error = "Couldn't read the distance-mate definition — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { def.Distance = res.RequestedMm / 1000.0; } catch (Exception ex) { diag += "set:EX(" + ex.GetType().Name + ") "; }
            bool applied = false;
            try { applied = target.ModifyDefinition(def, model, null); } catch (Exception ex) { diag += "modify:EX(" + ex.GetType().Name + ") "; }
            try { model.ForceRebuild3(false); } catch { }

            var d2 = FindByName(model, res.MateName)?.GetDefinition() as IDistanceMateFeatureData;
            if (d2 != null) { try { res.AfterVal = Math.Round(d2.Distance * 1000.0, 4); } catch { } }
            res.Verified = !double.IsNaN(res.AfterVal) && Math.Abs(res.AfterVal - res.RequestedMm) <= 0.01;
            if (!res.Verified)
            {
                res.Error = "Distance didn't apply (before=" + res.BeforeVal + "mm, after=" + res.AfterVal + "mm, requested=" + res.RequestedMm + "mm, modify=" + applied + ") " + diag;
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }
            await emit("Sentinel", null, "done", res.MateName + ": " + res.BeforeVal + "mm → " + res.AfterVal + "mm");
            res.Info = "Set '" + res.MateName + "' to " + res.AfterVal + "mm (was " + res.BeforeVal + "mm). One Ctrl+Z undoes it; Forge didn't save.";
            return res;
        }

        private static async Task<EditMateValueResult> EditAngle(IModelDoc2 model, Feature target, string intent, EditMateValueResult res, Func<string, string, string, string, Task> emit)
        {
            res.Kind = "angle";
            double? deg = ParseAngleDeg(intent);
            if (deg == null) { res.Error = "What angle? e.g. \"set the angle mate to 45 degrees\"."; await emit("Gauge", null, "fail", "no value"); return res; }
            res.RequestedDeg = deg.Value;

            var a0 = target.GetDefinition() as IAngleMateFeatureData;
            if (a0 != null) { try { res.BeforeVal = Math.Round(a0.Angle * 180.0 / Math.PI, 4); } catch { } }
            if (!double.IsNaN(res.BeforeVal) && (Math.Abs(res.BeforeVal - res.RequestedDeg) <= 0.05 || Math.Abs(res.BeforeVal - (180.0 - res.RequestedDeg)) <= 0.05))
            {
                res.AlreadyAtValue = true; res.Verified = true; res.AfterVal = res.BeforeVal;
                res.Info = "'" + res.MateName + "' is already " + res.RequestedDeg + "° — nothing to change.";
                await emit("Sentinel", null, "done", "already " + res.RequestedDeg + "°");
                return res;
            }

            await emit("Scribe", "setting '" + res.MateName + "' to " + res.RequestedDeg + "°", "run", null);
            string diag = "";
            var def = target.GetDefinition() as IAngleMateFeatureData;
            if (def == null) { res.Error = "Couldn't read the angle-mate definition — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { def.Angle = res.RequestedDeg * Math.PI / 180.0; } catch (Exception ex) { diag += "set:EX(" + ex.GetType().Name + ") "; }
            bool applied = false;
            try { applied = target.ModifyDefinition(def, model, null); } catch (Exception ex) { diag += "modify:EX(" + ex.GetType().Name + ") "; }
            try { model.ForceRebuild3(false); } catch { }

            var a2 = FindByName(model, res.MateName)?.GetDefinition() as IAngleMateFeatureData;
            if (a2 != null) { try { res.AfterVal = Math.Round(a2.Angle * 180.0 / Math.PI, 4); } catch { } }
            res.Verified = !double.IsNaN(res.AfterVal) && (Math.Abs(res.AfterVal - res.RequestedDeg) <= 0.05 || Math.Abs(res.AfterVal - (180.0 - res.RequestedDeg)) <= 0.05);
            if (!res.Verified)
            {
                res.Error = "Angle didn't apply (before=" + res.BeforeVal + "°, after=" + res.AfterVal + "°, requested=" + res.RequestedDeg + "°, modify=" + applied + ") " + diag;
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }
            await emit("Sentinel", null, "done", res.MateName + ": " + res.BeforeVal + "° → " + res.AfterVal + "°");
            res.Info = "Set '" + res.MateName + "' to " + res.AfterVal + "° (was " + res.BeforeVal + "°). One Ctrl+Z undoes it; Forge didn't save.";
            return res;
        }

        private static double? ParseDistanceMm(string intent)
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

        private static double? ParseAngleDeg(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var m = Regex.Match(intent, @"(\d+(?:\.\d+)?)\s*(deg\w*|°|degrees?)", RegexOptions.IgnoreCase);
            if (m.Success) return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var m2 = Regex.Match(intent, @"(?<![a-z0-9])(\d+(?:\.\d+)?)");
            if (!m2.Success) return null;
            return double.Parse(m2.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        private static Feature FindByName(IModelDoc2 model, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var f in CollectMates(model)) { string n = null; try { n = f.Name; } catch { } if (n == name) return f; }
            return null;
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
