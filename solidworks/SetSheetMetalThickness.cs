using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SetSheetMetalThicknessResult
    {
        public bool IsSheetMetal;
        public double BeforeMm = -1;    // physical wall thickness (measured from faces) before the edit
        public double AfterMm = -1;     // physical wall thickness after the edit + rebuild
        public double TargetMm = -1;
        public string Source;           // which feature the parameter edit went through
        public bool AlreadyDone;        // idempotent: already at the target thickness
        public bool Verified;           // fail closed: true ONLY when the SOLID actually measures the new thickness
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 172 — set_sheet_metal_thickness (WRITE). "set the sheet metal thickness to 3mm" / "change the gauge to
    /// 0.06in". Edits Thickness on BOTH sheet-metal feature definitions when present — the base-flange feature
    /// (IBaseFlangeFeatureData, which actually shapes the already-built solid) AND the part-level gauge default
    /// (ISheetMetalFeatureData) — via the standard GetDefinition -> AccessSelections -> set -> ModifyDefinition
    /// route (EditPatternCount/EditPatternSpacing/EditMateValue's proven idiom), one ForceRebuild3. Editing only the
    /// gauge default was tried first and is a confirmed silent no-op (ModifyDefinition returns true, the SOLID
    /// doesn't change) — the base-flange feature is the one that must move for the geometry to follow.
    ///
    /// Verified INDEPENDENTLY of the parameters it just wrote: the physical wall thickness is measured from the
    /// SOLID itself (largest planar face vs. the nearest parallel face — the same geometry check
    /// GroundTruth.MeasureGetSheetMetalProps uses) both BEFORE and AFTER, so a parameter that "sets" but doesn't
    /// actually change the geometry is caught rather than trusted. Idempotent: already-at-target is reported and
    /// skipped, never re-applied. Never saves.
    /// </summary>
    public static class SetSheetMetalThickness
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"sheet\s*metal|\bgauge\b|\bbend allowance\b")) return false;
            if (!Regex.IsMatch(c, @"\b(set|change|make|update|increase|decrease|thicken|thin)\b")) return false;
            return Regex.IsMatch(c, @"\bthick(ness)?\b|\bgauge\b");
        }

        public static async Task<SetSheetMetalThicknessResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SetSheetMetalThicknessResult();
            var part = model as PartDoc;
            if (model == null || part == null) { res.Error = "Open a sheet-metal part to change its thickness."; return res; }

            await emit("Reader", "finding the sheet-metal feature", "run", null);

            // Collect BOTH the part-level gauge default (ISheetMetalFeatureData, the "Sheet-Metal1" feature) AND the
            // base-flange feature's OWN thickness (IBaseFlangeFeatureData) if present — the base flange is what
            // actually shapes the built solid, so editing only the gauge default can leave existing geometry
            // unchanged; both are edited when both exist, same as SolidWorks' own Edit-Feature dialog keeps them
            // in sync.
            Feature smFeature = null; object smDef = null; string smName = null;
            Feature bfFeature = null; object bfDef = null; string bfName = null;
            Feature f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (!string.IsNullOrEmpty(tn) &&
                    (tn.IndexOf("SheetMetal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     tn.IndexOf("SMBaseFlange", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     tn.StartsWith("SM", StringComparison.Ordinal)))
                {
                    object def = null; try { def = f.GetDefinition(); } catch { }
                    if (smFeature == null && def is ISheetMetalFeatureData) { smFeature = f; smDef = def; smName = tn; }
                    if (bfFeature == null && def is IBaseFlangeFeatureData) { bfFeature = f; bfDef = def; bfName = tn; }
                }
                f = f.GetNextFeature() as Feature;
            }

            if (smFeature == null && bfFeature == null)
            {
                res.Error = "This part has no sheet-metal features — there's no thickness to change. Convert it to sheet metal first.";
                await emit("Reader", null, "fail", "not a sheet-metal part");
                return res;
            }
            res.IsSheetMetal = true;
            res.Source = bfName ?? smName;

            double targetMm = ParseThicknessMm(intent);
            if (targetMm <= 0)
            {
                res.Error = "Didn't catch a target thickness — say something like \"set the thickness to 3mm\".";
                await emit("Reader", null, "fail", "no thickness stated");
                return res;
            }
            res.TargetMm = targetMm;

            res.BeforeMm = WallThicknessMm(part);
            if (res.BeforeMm > 0 && Math.Abs(res.BeforeMm - targetMm) < 0.01)
            {
                res.AlreadyDone = true; res.Verified = true; res.AfterMm = res.BeforeMm;
                res.Info = "Already " + Trim(targetMm) + "mm thick — nothing to change.";
                await emit("Reader", null, "done", "already " + Trim(targetMm) + "mm");
                return res;
            }

            await emit("Scribe", "setting thickness to " + Trim(targetMm) + "mm", "run", null);
            string diag = "";
            // Base flange first — it shapes the built solid; the gauge default second, so both end up in sync.
            if (bfFeature != null)
            {
                bool bfApplied = false;
                try { ((IBaseFlangeFeatureData)bfDef).AccessSelections(model, null); } catch { }
                try { ((IBaseFlangeFeatureData)bfDef).Thickness = targetMm / 1000.0; } catch (Exception ex) { diag += "bf-set:EX(" + ex.GetType().Name + ") "; }
                try { bfApplied = bfFeature.ModifyDefinition(bfDef, model, null); } catch (Exception ex) { diag += "bf-modify:EX(" + ex.GetType().Name + ") "; }
                diag += "bf-applied=" + bfApplied + " ";
            }
            if (smFeature != null)
            {
                bool smApplied = false;
                try { ((ISheetMetalFeatureData)smDef).AccessSelections(model, null); } catch { }
                try { ((ISheetMetalFeatureData)smDef).Thickness = targetMm / 1000.0; } catch (Exception ex) { diag += "sm-set:EX(" + ex.GetType().Name + ") "; }
                try { smApplied = smFeature.ModifyDefinition(smDef, model, null); } catch (Exception ex) { diag += "sm-modify:EX(" + ex.GetType().Name + ") "; }
                diag += "sm-applied=" + smApplied + " ";
            }
            try { model.ForceRebuild3(false); } catch { }

            res.AfterMm = WallThicknessMm(part);
            res.Verified = res.AfterMm > 0 && Math.Abs(res.AfterMm - targetMm) < 0.05;

            if (!res.Verified)
            {
                res.Error = "Thickness edit didn't take — the solid still measures " + Trim(res.AfterMm) + "mm, not " + Trim(targetMm) + "mm" +
                            (string.IsNullOrEmpty(diag) ? "." : " (" + diag.Trim() + ").");
                await emit("Scribe", null, "fail", res.Error);
                return res;
            }

            res.Info = Trim(res.BeforeMm) + "mm -> " + Trim(res.AfterMm) + "mm (via \"" + res.Source + "\").";
            await emit("Scribe", null, "done", Trim(res.BeforeMm) + "mm -> " + Trim(res.AfterMm) + "mm");
            return res;
        }

        // INDEPENDENT physical wall measurement — touches no sheet-metal API. Largest planar face vs. the nearest
        // parallel face, same idiom GroundTruth.MeasureGetSheetMetalProps uses (kept as a separate implementation
        // here — the add-in and the harness are different assemblies and don't share code).
        private static double WallThicknessMm(PartDoc part)
        {
            double wall = -1;
            try
            {
                var planes = new List<double[]>();
                var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var s = face.GetSurface() as Surface; if (s == null) continue;
                        bool isPlane = false; try { isPlane = s.IsPlane(); } catch { }
                        if (!isPlane) continue;
                        double[] pp = null; try { pp = s.PlaneParams as double[]; } catch { }
                        if (pp == null || pp.Length < 6) continue;
                        double area = 0; try { area = face.GetArea(); } catch { }
                        planes.Add(new[] { pp[0], pp[1], pp[2], pp[3], pp[4], pp[5], area });
                    }
                }
                double[] big = null;
                foreach (var p in planes) if (big == null || p[6] > big[6]) big = p;
                if (big != null)
                {
                    foreach (var p in planes)
                    {
                        double dot = big[0] * p[0] + big[1] * p[1] + big[2] * p[2];
                        if (Math.Abs(dot) < 0.999) continue;
                        double dx = p[3] - big[3], dy = p[4] - big[4], dz = p[5] - big[5];
                        double sep = Math.Abs(dx * big[0] + dy * big[1] + dz * big[2]) * 1000.0;
                        if (sep > 1e-6 && (wall < 0 || sep < wall)) wall = sep;
                    }
                }
            }
            catch { }
            return wall;
        }

        private static double ParseThicknessMm(string intent)
        {
            string cmd = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(cmd, @"(\d+(\.\d+)?)\s*(inch(es)?|in\b|"")");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double vIn) && vIn > 0) return vIn * 25.4;
            m = Regex.Match(cmd, @"(\d+(\.\d+)?)\s*cm\b");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double vCm) && vCm > 0) return vCm * 10.0;
            m = Regex.Match(cmd, @"(\d+(\.\d+)?)\s*mm");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v) && v > 0) return v;
            m = Regex.Match(cmd, @"\b(\d+(\.\d+)?)\b");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v2) && v2 > 0) return v2;
            return -1;
        }

        private static string Trim(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
