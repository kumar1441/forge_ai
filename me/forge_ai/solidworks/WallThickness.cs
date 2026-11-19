using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    // one sampled thin region — a face whose local (sampled) wall thickness fell below the threshold
    public class ThinRegion { public string Face; public double ThicknessMm; }

    public class WallThicknessResult
    {
        public double MinThicknessMm = -1;   // global minimum sampled wall thickness (mm); -1 => nothing measurable
        public int BelowCount;               // how many sampled faces measured below the threshold
        public double ThresholdMm = 1.0;     // threshold used (default 1.0mm, overridable from the intent)
        public int SampledFaces;             // planar/cylindrical faces we actually sampled
        public int MeasuredFaces;            // of those, how many produced a valid opposite-wall measurement
        public int BodyCount;
        public double BboxDiagMm;            // part bounding-box diagonal — the sane upper bound on any thickness
        public string ThinnestFace;          // feature name owning the thinnest sampled face (best-effort)
        public List<ThinRegion> Top = new List<ThinRegion>();  // thinnest-first, capped
        public bool CenterRequested;         // intent asked about "the center/middle" specifically, not the global min
        public double CenterThicknessMm = -1;  // thickness of the sample nearest the part's own geometric center (long axis)
        public double CenterOffsetMm = -1;     // how far that sample sat from the exact center (honesty about approximation)
        public string CenterFace;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// WallThickness (tool #182) — a READ-ONLY minimum-wall-thickness scan on a PART, for FEA prep / injection
    /// molding / thin-wall machining. It never edits: no feature, no config, no geometry — it only measures.
    ///
    /// Method (honest, SAMPLED estimate — labeled as such, per Character #2 + Rule #4):
    ///   For each PLANAR or CYLINDRICAL face of every solid body, take one sample point at the face centroid
    ///   (box-center snapped onto the face via GetClosestPointOn), read the OUTWARD normal there
    ///   (Surface.EvaluateAtPoint + FaceInSurfaceSense), then cast inward (-normal) to the nearest opposite-facing
    ///   wall: for every other face we take GetClosestPointOn(sample), keep only hits that lie straight inward
    ///   (direction aligns with -normal) and whose far wall faces back. The nearest such hit is the local wall
    ///   thickness at that sample. The global minimum across all samples is the reported minimum wall; faces whose
    ///   local thickness is below the threshold are the flagged thin regions.
    ///
    /// This is APPROXIMATE — one sample per face, closest-point (not a true swept ray) — so it is reported as a
    /// "sampled estimate," never as a certified minimum. On an imported dumb solid (no feature tree) it still works
    /// because it reads BODIES/FACES, not features. If there is no solid body or no measurable face, it says so
    /// plainly rather than inventing a number (Rule #4).
    ///
    /// All coordinates are PART-LOCAL. This runs on a single PART doc, so every face already shares the part
    /// coordinate system and no component transform is needed (the assembly-space landmine does not apply here).
    /// </summary>
    public static class WallThickness
    {
        private class FaceRec
        {
            public Face2 Face;
            public double[] P;      // sample point on the face (part coords)
            public double[] Nout;   // unit OUTWARD normal at P (points out of the solid)
            public bool Source;     // planar/cylindrical => we cast a ray from it
            public string Name;     // owning feature name (best-effort), for the thin-region report
        }

        public static bool IsWallThicknessIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return Regex.IsMatch(cmd,
                @"\b(wall[- ]?thickness|thin[- ]?wall|thin[- ]?region|min(imum)?[- ]?(wall[- ]?)?thickness|too[- ]?thin|thickness (check|scan|analysis))\b",
                RegexOptions.IgnoreCase);
        }

        // Broader than IsWallThicknessIntent above: a plain "how thick is..."/"thickness of/at..." READ question with
        // no dedicated vocabulary word (test-loop wrong-answer measure-thickness-center: "how thick is the metal in the
        // middle of that curve?" has no action in the cloud parser's vocabulary at all, so it declined rather than
        // routing here). Excludes WRITE phrasing (shell/hollow/thin-out/"make ... thick") and sheet-metal gauge props
        // (a different tool) so it only catches genuine thickness READ questions.
        public static bool IsGenericThicknessQuestion(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(shell|hollow(?:ed)?|thin(?:ner|\s*(?:it|this)?\s*out|ning)?|gauge|make\s+\w+\s+\w*\bthick|set\s+\w+\s+\w*\bthick)\b")) return false;
            return Regex.IsMatch(c, @"\bhow\s+thick\b") || Regex.IsMatch(c, @"\bthick(?:ness)?\s+(is|of|at|in|on|near|around)\b");
        }

        // Guards the TOP-LEVEL pre-cloud short-circuit (which answers the WHOLE intent as a single wall_thickness
        // read and returns immediately): only fire it when the thickness question really IS the whole ask, not one
        // clause of a longer multi-step chain (test-loop wrong-answer chain-thickness-arc-check, real LEAF SPRING:
        // "add 1mm to thickness, lower arc by 5mm, then measure thickness at the center" — the WHOLE sentence
        // matches IsGenericThicknessQuestion via its tail clause, so the top-level override was answering the
        // thickness reading correctly but SILENTLY SKIPPING the two requested writes entirely, worse than the
        // original bug it was fixing). A write verb + a number appearing BEFORE the thickness clause means this is
        // a chain — let it fall through to the normal chain pipeline instead, where the SAME override now also
        // runs per-LEG (see ForgePanel.Pipeline.cs RunChainRest / Harness.cs's chain loop) so the thickness leg
        // still gets corrected without the writes being dropped.
        public static bool IsStandaloneThicknessQuestion(string cmd)
        {
            if (!IsGenericThicknessQuestion(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            var m = Regex.Match(c, @"\bthick(?:ness)?\s+(is|of|at|in|on|near|around)\b");
            if (!m.Success) m = Regex.Match(c, @"\bhow\s+thick\b");
            if (!m.Success) return true;
            string before = c.Substring(0, m.Index);
            bool earlierWriteClause = Regex.IsMatch(before,
                @"\b(add|increase|decrease|raise|lower|set|change|make|reduce|grow|shrink|widen|thicken)\b.*\d");
            return !earlierWriteClause;
        }

        // "... in the middle of that curve", "at the center", "midspan" — report the sampled thickness NEAREST the
        // part's own geometric center along its longest axis, instead of the global minimum (a different question:
        // "how thick is it here" vs "what's the thinnest point anywhere").
        private static bool WantsCenter(string cmd)
            => Regex.IsMatch(cmd ?? "", @"\b(center|centre|middle|midspan|mid[- ]?point)\b", RegexOptions.IgnoreCase);

        private const int TopCap = 5;
        private const double AlignDot = 0.9;   // sample->hit must run within ~26deg of the inward normal (through the wall, not sideways)
        private const double BackDot = -0.35;  // the far wall must face back toward the source (opposite-ish outward normals)
        private const double Eps = 1e-6;       // ignore zero-distance self/adjacent-edge hits

        public static async Task<WallThicknessResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new WallThicknessResult();
            res.ThresholdMm = ParseThreshold(intent);

            if ((int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Wall-thickness runs on a single part — open the .SLDPRT you want measured."; return res; }

            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to measure."; return res; }

            await emit("Caliper", "reading the solid bodies", "run", null);

            object[] bodies = null;
            try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            if (bodies == null || bodies.Length == 0)
            {
                // Rule #4 — know what you don't know: no solid to measure (surface/sheet-body part or empty doc).
                res.Error = "No solid body to measure. Wall-thickness needs solid geometry — this part has no solid bodies " +
                            "(surface bodies, an empty part, or a reference-only doc can't be sampled).";
                return res;
            }
            res.BodyCount = bodies.Length;

            // ---- collect every face once (sample point + outward normal); mark planar/cylindrical as ray SOURCES ----
            var recs = new List<FaceRec>();
            double[] bbox = null;
            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                bbox = UnionBox(bbox, SafeBodyBox(body));
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                if (faces == null) continue;
                foreach (var fo in faces)
                {
                    var face = fo as Face2; if (face == null) continue;
                    var rec = BuildRec(face);
                    if (rec != null) recs.Add(rec);
                }
            }
            res.BboxDiagMm = BoxDiagMm(bbox);
            int sources = 0; foreach (var r in recs) if (r.Source) sources++;

            if (sources == 0)
            {
                res.Error = "No planar or cylindrical faces to sample. This part's walls are all freeform/complex surfaces — " +
                            "the sampled wall-thickness method (planar/cylindrical faces) can't measure it. A native Thickness " +
                            "Analysis run in SolidWorks would be needed here.";
                return res;
            }

            await emit("Caliper", null, "done", recs.Count + " faces read (" + sources + " planar/cylindrical to sample)");
            await emit("Caliper", "measuring wall thickness (sampled estimate)", "run", null);

            // ---- longest bbox axis + its center coordinate, for a "center of the curve" location-scoped answer ----
            res.CenterRequested = WantsCenter(intent);
            int longAxis = 0; double centerCoord = 0;
            if (bbox != null)
            {
                double dx = bbox[3] - bbox[0], dy = bbox[4] - bbox[1], dz = bbox[5] - bbox[2];
                if (dy >= dx && dy >= dz) longAxis = 1; else if (dz >= dx && dz >= dy) longAxis = 2; else longAxis = 0;
                centerCoord = (bbox[longAxis] + bbox[longAxis + 3]) / 2.0;
            }

            // ---- for each source face, find the nearest opposite wall along the inward normal ----
            double globalMin = double.MaxValue; string globalMinName = null;
            double centerBestOffset = double.MaxValue, centerThick = -1; string centerName = null;
            var below = new List<ThinRegion>();
            int measured = 0, sampled = 0, lastPct = -1;
            foreach (var src in recs)
            {
                if (!src.Source) continue;
                sampled++;
                double local = LocalThickness(src, recs);
                if (local > 0)
                {
                    measured++;
                    if (local < globalMin) { globalMin = local; globalMinName = src.Name; }
                    if (local < res.ThresholdMm) below.Add(new ThinRegion { Face = src.Name, ThicknessMm = local });
                    if (bbox != null)
                    {
                        double offsetMm = Math.Abs(src.P[longAxis] - centerCoord) * 1000.0;
                        if (offsetMm < centerBestOffset) { centerBestOffset = offsetMm; centerThick = local; centerName = src.Name; }
                    }
                }
                int pct = (int)(100.0 * sampled / Math.Max(1, sources));
                if (pct >= lastPct + 20) { lastPct = pct; await emit(null, null, "done", "measuring… " + sampled + "/" + sources); }
            }

            res.SampledFaces = sampled;
            res.MeasuredFaces = measured;
            res.MinThicknessMm = globalMin == double.MaxValue ? -1 : globalMin;
            res.ThinnestFace = globalMinName;
            res.BelowCount = below.Count;
            if (centerThick > 0) { res.CenterThicknessMm = centerThick; res.CenterOffsetMm = centerBestOffset; res.CenterFace = centerName; }
            below.Sort((a, b) => a.ThicknessMm.CompareTo(b.ThicknessMm));   // thinnest-first
            for (int i = 0; i < below.Count && i < TopCap; i++) res.Top.Add(below[i]);

            await emit("Caliper", null, "done",
                res.CenterRequested
                    ? (res.CenterThicknessMm < 0 ? "no center thickness measurable" : "center " + res.CenterThicknessMm.ToString("0.00") + " mm")
                    : (res.MinThicknessMm < 0 ? "no wall measurable" : "min " + res.MinThicknessMm.ToString("0.00") + " mm") +
                      " · " + res.BelowCount + " below " + res.ThresholdMm.ToString("0.##") + " mm");

            res.Info = BuildInfo(res);
            return res;
        }

        // ---- one face's sampled local wall thickness: nearest opposite-facing wall straight inward (or -1) ----
        private static double LocalThickness(FaceRec src, List<FaceRec> all)
        {
            double[] P = src.P, d = Neg(src.Nout);   // cast INWARD
            double best = double.MaxValue;
            foreach (var g in all)
            {
                if (g == src) continue;
                double[] q = null;
                try { q = g.Face.GetClosestPointOn(P[0], P[1], P[2]) as double[]; } catch { }
                if (q == null || q.Length < 3) continue;
                double[] v = { q[0] - P[0], q[1] - P[1], q[2] - P[2] };
                double dist = Len(v);
                if (dist < Eps) continue;                                  // self / shared-edge touch
                double[] vu = { v[0] / dist, v[1] / dist, v[2] / dist };
                if (Dot(vu, d) < AlignDot) continue;                       // not straight through the wall
                if (g.Nout != null && Dot(g.Nout, src.Nout) > BackDot) continue;  // far wall must face BACK
                if (dist < best) best = dist;
            }
            return best == double.MaxValue ? -1 : best * 1000.0;           // m -> mm
        }

        // ---- build a face record: sample point (centroid snapped) + outward normal; flag planar/cylindrical ----
        private static FaceRec BuildRec(Face2 face)
        {
            Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
            if (s == null) return null;
            bool source = false; try { source = s.IsPlane() || s.IsCylinder(); } catch { }

            double[] box = null; try { box = face.GetBox() as double[]; } catch { }
            double[] center = box != null && box.Length >= 6
                ? new[] { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 }
                : null;
            double[] P = null;
            try { if (center != null) P = face.GetClosestPointOn(center[0], center[1], center[2]) as double[]; } catch { }
            if (P == null || P.Length < 3) return null;
            P = new[] { P[0], P[1], P[2] };

            double[] n = null; try { n = s.EvaluateAtPoint(P[0], P[1], P[2]) as double[]; } catch { }
            if (n == null || n.Length < 3) return null;
            double nl = Len(n); if (nl < 1e-9) return null;
            double[] nu = { n[0] / nl, n[1] / nl, n[2] / nl };
            bool reversed = false; try { reversed = face.FaceInSurfaceSense(); } catch { }
            if (reversed) nu = Neg(nu);   // make the normal point OUT of the solid

            string name = null;
            try { var feat = face.GetFeature() as Feature; if (feat != null) name = feat.Name; } catch { }

            return new FaceRec { Face = face, P = P, Nout = nu, Source = source, Name = name ?? "face" };
        }

        // threshold in mm from the intent ("below 0.8mm", "under 2 mm"); default 1.0mm.
        private static double ParseThreshold(string intent)
        {
            string cmd = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(cmd, @"(\d+(\.\d+)?)\s*mm");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v) && v > 0) return v;
            return 1.0;
        }

        // verdict first (Character #3), the NUMBER not an adjective (Character #2), honest that it's sampled (Rule #4).
        private static string BuildInfo(WallThicknessResult r)
        {
            if (r.CenterRequested)
            {
                if (r.CenterThicknessMm < 0)
                    return "Couldn't measure a thickness near the center — " + r.SampledFaces + " faces sampled but none had a clear " +
                           "opposite wall along their normal. Likely an open/thin-shell topology; a native Thickness Analysis would confirm.";
                return "Thickness near the center: " + r.CenterThicknessMm.ToString("0.00") + " mm (nearest sampled point, " +
                       r.CenterOffsetMm.ToString("0.0") + " mm off the exact geometric center, at " + r.CenterFace + "). " +
                       "Sampled estimate (one point per face, closest-to-center pick) — not a certified reading.";
            }
            if (r.MinThicknessMm < 0)
                return "Couldn't measure a wall thickness — " + r.SampledFaces + " faces sampled but none had a clear " +
                       "opposite wall along their normal. Likely an open/thin-shell topology; a native Thickness Analysis would confirm.";

            string head = "Min wall " + r.MinThicknessMm.ToString("0.00") + " mm; " + r.BelowCount + " region" +
                          (r.BelowCount == 1 ? "" : "s") + " below " + r.ThresholdMm.ToString("0.##") + " mm.";
            var sb = new System.Text.StringBuilder(head);
            if (!string.IsNullOrEmpty(r.ThinnestFace)) sb.Append(" Thinnest at " + r.ThinnestFace + ".");
            if (r.Top.Count > 0)
            {
                sb.Append(" Thinnest: ");
                int shown = Math.Min(3, r.Top.Count);
                for (int i = 0; i < shown; i++)
                {
                    sb.Append(r.Top[i].Face + " " + r.Top[i].ThicknessMm.ToString("0.00") + " mm");
                    sb.Append(i < shown - 1 ? "; " : ".");
                }
            }
            sb.Append(" Sampled estimate (" + r.MeasuredFaces + "/" + r.SampledFaces + " faces measured, one point each) — " +
                      "not a certified minimum.");
            return sb.ToString();
        }

        // ---- small vector + box helpers ----
        private static double[] Neg(double[] a) => new[] { -a[0], -a[1], -a[2] };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Len(double[] a) => Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);

        private static double[] SafeBodyBox(Body2 body)
        { try { return body.GetBodyBox() as double[]; } catch { return null; } }

        private static double[] UnionBox(double[] acc, double[] b)
        {
            if (b == null || b.Length < 6) return acc;
            if (acc == null) return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
            return new[]
            {
                Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
            };
        }

        private static double BoxDiagMm(double[] b)
        {
            if (b == null || b.Length < 6) return 0;
            double dx = b[3] - b[0], dy = b[4] - b[1], dz = b[5] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;
        }
    }
}
