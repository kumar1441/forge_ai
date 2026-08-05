using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class EditLastFeatureResult
    {
        public string FeatureName;
        public string Direction;        // "deeper" | "shallower"
        public double DeltaMm = -1;
        public double BeforeMm = double.NaN, AfterMm = double.NaN;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 236 — edit_last_feature (WRITE). The "iterate loop": relative edits to the MOST RECENT operation without
    /// naming it — "make it deeper by 5mm", "go shallower". Resolves the target itself (the last real feature in
    /// creation order, via the same scaffold-exclusion rule as find_features_by_type/get_feature_info) instead of
    /// requiring a name like edit_feature_parameter does, then edits DEPTH through the same proven feature-data recipe
    /// (GetDefinition -> AccessSelections -> IExtrudeFeatureData2.SetDepth -> ModifyDefinition). Scope is honest: only
    /// an Extrusion is currently editable this way, and a "shallower" request that would zero/invert the depth is
    /// refused rather than guessed. Verified by an INDEPENDENT read-back (fail closed). A relative edit is NOT
    /// idempotent by design (rerunning "deeper" again deepens again — correct), so this handler never claims
    /// already-done. Undoable; Forge never saves.
    /// </summary>
    public static class EditLastFeature
    {
        private const double DefaultDeltaMm = 5.0;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return Regex.IsMatch(cmd, @"\b(deeper|shallower)\b", RegexOptions.IgnoreCase);
        }

        public static async Task<EditLastFeatureResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new EditLastFeatureResult();
            if (model == null) { res.Error = "Open a document first."; return res; }

            string lo = (intent ?? "").ToLowerInvariant();
            res.Direction = Regex.IsMatch(lo, @"\bdeeper\b") ? "deeper" : "shallower";
            res.DeltaMm = ParseDeltaMm(lo) ?? DefaultDeltaMm;

            await emit("Gauge", "finding the last feature", "run", null);
            Feature target = FindLastRealFeature(model, out string lastType);
            if (target == null)
            { res.Error = "No feature in the tree to edit yet."; await emit("Gauge", null, "fail", res.Error); return res; }
            try { res.FeatureName = target.Name; } catch { }

            if (lastType != "Extrusion")
            { res.Error = "The last feature '" + res.FeatureName + "' is a " + lastType + " — depth editing only supports extrudes right now."; await emit("Gauge", null, "fail", res.Error); return res; }

            var d0 = target.GetDefinition() as IExtrudeFeatureData2;
            if (d0 == null) { res.Error = "Couldn't read '" + res.FeatureName + "'s extrude definition."; await emit("Gauge", null, "fail", res.Error); return res; }
            try { res.BeforeMm = Math.Round(d0.GetDepth(true) * 1000.0, 4); } catch { }
            if (double.IsNaN(res.BeforeMm)) { res.Error = "Couldn't read '" + res.FeatureName + "'s current depth."; await emit("Gauge", null, "fail", res.Error); return res; }

            double requestedMm = res.Direction == "deeper" ? res.BeforeMm + res.DeltaMm : res.BeforeMm - res.DeltaMm;
            if (res.Direction == "shallower" && requestedMm <= 0.5)
            { res.Error = "'" + res.FeatureName + "' is only " + res.BeforeMm + "mm deep — going " + res.DeltaMm + "mm shallower would zero/invert it. Refusing rather than guessing."; await emit("Gauge", null, "fail", res.Error); return res; }
            await emit("Gauge", null, "done", res.FeatureName + " (" + lastType + ") is " + res.BeforeMm + "mm — going " + res.Direction + " by " + res.DeltaMm + "mm");

            await emit("Scribe", "setting '" + res.FeatureName + "' depth to " + requestedMm + "mm", "run", null);
            string diag = "";
            var def = target.GetDefinition() as IExtrudeFeatureData2;
            if (def == null) { res.Error = "Couldn't re-read the extrude definition — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { def.AccessSelections(model, null); } catch (Exception ex) { diag += "access:EX(" + ex.GetType().Name + ") "; }
            try { def.SetDepth(true, requestedMm / 1000.0); } catch (Exception ex) { diag += "set:EX(" + ex.GetType().Name + ") "; }
            bool applied = false;
            try { applied = target.ModifyDefinition(def, model, null); } catch (Exception ex) { diag += "modify:EX(" + ex.GetType().Name + ") "; }
            try { model.ForceRebuild3(false); } catch { }

            Feature after = FindByName(model, res.FeatureName);
            var d2 = after?.GetDefinition() as IExtrudeFeatureData2;
            if (d2 != null) { try { res.AfterMm = Math.Round(d2.GetDepth(true) * 1000.0, 4); } catch { } }
            res.Verified = !double.IsNaN(res.AfterMm) && Math.Abs(res.AfterMm - requestedMm) <= 0.01;
            if (!res.Verified)
            {
                res.Error = "Depth didn't apply (before=" + res.BeforeMm + "mm, after=" + res.AfterMm + "mm, requested=" + requestedMm + "mm, modify=" + applied + ") " + diag;
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }
            await emit("Sentinel", null, "done", res.FeatureName + ": " + res.BeforeMm + "mm -> " + res.AfterMm + "mm");
            res.Info = "Made '" + res.FeatureName + "' " + res.Direction + " by " + res.DeltaMm + "mm (" + res.BeforeMm + "mm -> " + res.AfterMm + "mm). One Ctrl+Z undoes it; Forge didn't save.";
            return res;
        }

        private static double? ParseDeltaMm(string lo)
        {
            var m = Regex.Match(lo, @"\bby\s+(\d+(?:\.\d+)?)\s*(mm|cm|m\b|in\b|inch\w*|"")?", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string u = m.Groups[2].Success ? m.Groups[2].Value.ToLowerInvariant() : "mm";
            if (u == "cm") return v * 10.0;
            if (u == "m") return v * 1000.0;
            if (u.StartsWith("in") || u == "\"") return v * 25.4;
            return v;
        }

        // last REAL feature in creation order — same scaffold-exclusion rule as find_features_by_type/get_feature_info.
        private static Feature FindLastRealFeature(IModelDoc2 model, out string lastType)
        {
            Feature last = null; lastType = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (!string.IsNullOrEmpty(tn) && IsRealFeature(tn)) { last = f; lastType = tn; }
                f = f.GetNextFeature() as Feature;
            }
            return last;
        }

        private static bool IsRealFeature(string tn)
        {
            if (tn.EndsWith("Folder", StringComparison.OrdinalIgnoreCase)) return false;
            switch (tn)
            {
                case "RefPlane": case "RefAxis": case "OriginProfileFeature": case "CoordSys":
                case "DetailCabinet": case "HistoryFolder": case "SketchBlockDef": return false;
                default: return true;
            }
        }

        private static Feature FindByName(IModelDoc2 model, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string n = null; try { n = f.Name; } catch { }
                if (n == name) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }
    }
}
