using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddDomeResult
    {
        public bool Success;
        public bool AlreadyDone;
        public string FeatureName;
        public string FeatureType;
        public double HeightMm;
        public double VolumeDeltaMm3;   // dome bulges outward -> positive
        public double FaceAreaMm2;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 121 — create_dome. ⛔ PARKED 2026-07-25: IModelDoc2.InsertDome is a SILENT NO-OP headless on this R2026x
    /// build — same dead in-model geometry-WRITE class as InsertMoveFace and FullyDefineSketch. PROVEN by a FAIR
    /// throw-5 sweep (face re-resolved fresh each attempt so no stale-ref false-negatives): simple(1600mm²) and
    /// largest(3150mm²) planar faces × (outward / inward / elliptic) — ALL FIVE reported sel=True (the face WAS
    /// selected and mateable) yet dVol=0 every time, no Dome feature, rebuild clean. Selection is not the problem;
    /// the API commits nothing. Handler kept DORMANT + fail-CLOSED (the sweep evidence rides in Diag/Error). Revive
    /// only if InsertDome is confirmed to work INTERACTIVELY in this SW — do NOT re-attempt blind.
    ///
    /// Design (for the revive): bulges a planar face outward into a rounded cap via IModelDoc2.InsertDome (VOID; it
    /// operates on the pre-SELECTED face). A dome ADDS material — body count unchanged, total volume RISES by a
    /// positive amount bounded above by faceArea*height. Judges success by GEOMETRY (volume delta), never a guessed
    /// feature-type string. Names the feature "Forge-Dome" for idempotency; never saves.
    /// </summary>
    public static class AddDome
    {
        private const string DomeName = "Forge-Dome";
        private const double HeightM = 0.005;   // 5mm cap

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(dome|domed|bulge|rounded cap)\b");
        }

        public static async Task<AddDomeResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddDomeResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a dome."; return res; }

            var existing = FindFeature(model, DomeName);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.FeatureName = SafeName(existing);
                res.FeatureType = SafeType(existing);
                res.Info = "A dome (" + res.FeatureName + ") is already here — nothing to do.";
                return res;
            }

            res.HeightMm = HeightM * 1000.0;
            await emit("Builder", "doming a face outward (5mm cap)", "run", null);

            // THROW-N sweep: InsertDome is a geometry WRITE and several such APIs are dead headless on this build
            // (InsertMoveFace, FullyDefineSketch). Instrument the real volume delta across face-choice x (reverse,
            // elliptic) rather than theorise. Any attempt with a positive bounded volume rise wins.
            // Re-resolve the face FRESH each attempt: a prior InsertDome+ForceRebuild3 invalidates cached Face2 refs
            // (Select4 then returns false), which would make every attempt after the first a false negative.
            var attempts = new (string kind, bool rev, bool ell)[]
            { ("simple", false, false), ("simple", true, false), ("simple", false, true),
              ("largest", false, false), ("largest", true, false) };
            var sweep = new List<string>();
            bool domed = false; Feature domeFeat = null; double dVol = 0; double bound = 0;
            foreach (var a in attempts)
            {
                var pf = a.kind == "simple" ? ResolveSimpleFace(SolidBodies(part)) : ResolveLargestFace(SolidBodies(part));
                if (pf == null) { sweep.Add(a.kind + "/rev" + a.rev + "/ell" + a.ell + "=noface"); continue; }
                double volBefore = TotalSolidVolumeMm3(part);
                bool sel = false;
                try
                {
                    model.ClearSelection2(true);
                    try { sel = ((Entity)pf.Face).Select4(false, null); } catch { }
                    if (sel) { model.InsertDome(HeightM, a.rev, a.ell); model.ClearSelection2(true); model.ForceRebuild3(false); }
                }
                catch (Exception ex) { sweep.Add(a.kind + "/rev" + a.rev + "/ell" + a.ell + "=ex:" + ex.Message); continue; }
                double d = Math.Round(TotalSolidVolumeMm3(part) - volBefore, 2);
                sweep.Add(a.kind + Math.Round(pf.AreaMm2) + "/rev" + a.rev + "/ell" + a.ell + "=sel" + sel + " dVol" + d);
                double ub = pf.AreaMm2 * res.HeightMm;
                if (Math.Abs(d) > 0.5 && Math.Abs(d) < ub)   // outward (+) or inward (-) both prove the API fired
                {
                    domed = true; dVol = d; bound = ub; res.FaceAreaMm2 = Math.Round(pf.AreaMm2, 1);
                    domeFeat = model.FeatureByPositionReverse(0) as Feature;
                    break;
                }
            }

            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }
            res.VolumeDeltaMm3 = dVol;
            res.FeatureType = domeFeat == null ? null : SafeType(domeFeat);
            if (domed && domeFeat != null) { try { domeFeat.Name = DomeName; } catch { } res.FeatureName = SafeName(domeFeat); }
            res.Success = domed && rw == 0 && FindFeature(model, DomeName) != null;
            res.Diag = "dome name=" + res.FeatureName + " type=" + res.FeatureType + " dVolMm3=" + res.VolumeDeltaMm3 + " boundMm3=" + Math.Round(bound, 1) + " rebuildErr=" + rw + " | sweep: " + string.Join("; ", sweep);

            if (!res.Success && string.IsNullOrEmpty(res.Error))
                res.Error = "SolidWorks refused the dome (no attempt raised volume) — InsertDome may be dead headless on this build. sweep: " + string.Join("; ", sweep);

            await emit("Builder", null, "done", res.Success ? "dome added" : ("dVol=" + res.VolumeDeltaMm3 + "mm3"));

            res.Info = res.Success
                ? "Domed a face into a " + res.HeightMm + "mm cap (" + res.FeatureName + "): +" + res.VolumeDeltaMm3 + " mm3. Undo removes it; nothing was saved."
                : "Dome did not verify (dVol=" + res.VolumeDeltaMm3 + "mm3, rebuildErr=" + rw + ").";
            return res;
        }

        private class PlanarFace { public Face2 Face; public double AreaMm2; }

        private static PlanarFace ResolveSimpleFace(object[] bodies)
        {
            PlanarFace best = null; double bestArea = -1;
            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    bool plane = false; try { plane = s != null && s.IsPlane(); } catch { }
                    if (!plane) continue;
                    int edges = 0; try { edges = face.GetEdgeCount(); } catch { }
                    if (edges != 4) continue;   // simple rectangle only — no inner hole loop
                    double area = 0; try { area = face.GetArea(); } catch { }
                    if (area <= 0) continue;
                    if (area > bestArea) { bestArea = area; best = new PlanarFace { Face = face, AreaMm2 = area * 1e6 }; }
                }
            }
            return best;
        }

        private static PlanarFace ResolveLargestFace(object[] bodies)
        {
            PlanarFace best = null; double bestArea = -1;
            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    bool plane = false; try { plane = s != null && s.IsPlane(); } catch { }
                    if (!plane) continue;
                    double area = 0; try { area = face.GetArea(); } catch { }
                    if (area <= 0) continue;
                    if (area > bestArea) { bestArea = area; best = new PlanarFace { Face = face, AreaMm2 = area * 1e6 }; }
                }
            }
            return best;
        }

        private static object[] SolidBodies(PartDoc part)
        {
            try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? new object[0]; }
            catch { return new object[0]; }
        }

        private static double TotalSolidVolumeMm3(PartDoc part)
        {
            double v = 0;
            foreach (var o in SolidBodies(part))
            {
                var b = o as Body2; if (b == null) continue;
                var mp = b.GetMassProperties(0) as double[];
                if (mp != null && mp.Length >= 4) v += mp[3] * 1e9;
            }
            return v;
        }

        private static Feature FindFeature(IModelDoc2 model, string prefix)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
        private static string SafeType(Feature f) { try { return f.GetTypeName2(); } catch { return null; } }
    }
}
