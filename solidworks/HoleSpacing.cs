using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class HoleSpacingResult
    {
        public double SpacingMm = -1;      // nearest-neighbor center-to-center distance (the pitch, for a uniform row/pair)
        public double MaxSpacingMm = -1;   // farthest same-group hole pair — flags non-uniform spacing honestly
        public int HoleCount;
        public double HoleDiameterMm = -1;
        public bool Uniform;               // every hole's nearest-neighbor distance matches within tolerance
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// HoleSpacing (READ-ONLY): center-to-center distance between a part's repeating same-size holes — "how far
    /// apart are the bolt holes?", "hole spacing", "distance between the mounting holes", "center to center?".
    ///
    /// test-loop wrong-answer finding measure-mounting-hole-distance (real HIWIN HGH20CA linear-guide block, "how far
    /// apart are the bolt holes on that guide block?"): no action in the cloud's vocabulary, fell to
    /// get_bounding_box (an irrelevant overall footprint). Distinct from MeasureBoltCircle: that tool assumes a
    /// CIRCULAR pattern (reports PCD = 2x mean radial distance from a centroid) — correct for a flange's bolt
    /// circle, wrong for a LINEAR row of mounting holes (the common case on a guide rail/block), where "how far
    /// apart" means the straight-line pitch between adjacent holes, not a circle diameter.
    ///
    /// Method (honest — Character #2/#4): collect every cylindrical hole face on the part's solid bodies (same
    /// CylinderParams primitive MeasureBoltCircle uses), group by radius (0.3mm buckets — same-size holes are the
    /// mounting pattern; a single stray hole of a different size doesn't dilute it), take the LARGEST such group
    /// (2+ members — a pair counts, unlike MeasureBoltCircle's 3+ circular-pattern threshold), then compute the
    /// NEAREST-neighbor center-to-center distance for every hole in that group. The MINIMUM such distance across
    /// the group is reported as the spacing/pitch (exact for 2 holes; the adjacent pitch for an evenly-spaced row).
    /// If every hole's nearest-neighbor distance agrees within 5%, it's reported as "evenly spaced"; otherwise both
    /// the min and max are reported so an irregular layout is never flattened into a misleading single number.
    /// </summary>
    public static class HoleSpacing
    {
        public static bool IsHoleSpacingIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\bhow\s+far\s+apart\b.{0,30}\bholes?\b")
                || Regex.IsMatch(c, @"\bholes?\b.{0,30}\bhow\s+far\s+apart\b")
                || Regex.IsMatch(c, @"\b(distance|spacing)\s+between\b.{0,25}\bholes?\b")
                || Regex.IsMatch(c, @"\bholes?\b.{0,25}\b(spacing|apart)\b")
                || Regex.IsMatch(c, @"\bhole\s*(spacing|pitch)\b")
                || Regex.IsMatch(c, @"\bcenter[- ]to[- ]center\b");
        }

        private class HoleCyl { public double[] O; public double R; }

        public static async Task<HoleSpacingResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new HoleSpacingResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Hole spacing works on a single part — open the .SLDPRT you want measured, not an assembly."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to measure."; return res; }

            await emit("Caliper", "reading the solid and finding hole faces", "run", null);
            object[] bodies = null;
            try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to measure — this part has no solid geometry."; return res; }

            var holes = new List<HoleCyl>();
            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface surf = null; try { surf = face.GetSurface() as Surface; } catch { }
                    bool isCyl = false; try { isCyl = surf != null && surf.IsCylinder(); } catch { }
                    if (!isCyl) continue;
                    double[] cp = null; try { cp = surf.CylinderParams as double[]; } catch { }
                    if (cp == null || cp.Length < 7) continue;
                    holes.Add(new HoleCyl { O = new[] { cp[0], cp[1], cp[2] }, R = cp[6] });
                }
            }
            if (holes.Count == 0)
            { res.Error = "No cylindrical hole faces found on this part to measure spacing between."; return res; }

            // group by radius (0.1mm buckets — finer than MeasureBoltCircle's 0.3mm, which live on the real HIWIN
            // HGH20CA block MERGED two genuinely distinct hole sizes 0.07mm apart (radius 2.03mm/diameter 4.06mm
            // ball-recirculation-channel holes vs radius 2.10mm/diameter 4.20mm real corner mounting-bolt holes)
            // into one 8-member group, masking the real 4-hole bolt pattern entirely) — same-size holes are the
            // mounting pattern; take the LARGEST group (2+ members — a pair counts, unlike a circular-pattern's 3+)
            var groups = new Dictionary<int, List<HoleCyl>>();
            foreach (var h in holes)
            {
                int bucket = (int)Math.Round(h.R * 1000.0 / 0.1);
                if (!groups.ContainsKey(bucket)) groups[bucket] = new List<HoleCyl>();
                groups[bucket].Add(h);
            }
            // dedupe near-identical positions first (a through-hole's cylindrical face can be split into two
            // coincident faces at a rebuild seam) — collapse points within 0.5mm before counting group size.
            // Prefer the LARGEST-DIAMETER qualifying (2+) group, not the most POPULOUS one: a real part can carry
            // small functional holes (lubrication ports, ball-return passages on a linear-guide block) that
            // outnumber the actual bolt/mounting holes several-to-one — "the bolt holes" means the fastener-
            // clearance holes, which are almost always the biggest holes on the part, not the most common ones.
            List<HoleCyl> best = null;
            foreach (var kv in groups)
            {
                var deduped = DedupePositions(kv.Value);
                if (deduped.Count < 2) continue;
                if (best == null || deduped[0].R > best[0].R) best = deduped;
            }
            if (best == null)
            {
                res.Error = "No repeating same-size hole pair found — can't measure a hole-to-hole spacing (each " +
                            "hole on this part is a different size, or there's only one).";
                return res;
            }

            // nearest-neighbor distance for every hole in the group
            var nn = new List<double>();
            for (int i = 0; i < best.Count; i++)
            {
                double bestD = double.MaxValue;
                for (int j = 0; j < best.Count; j++)
                {
                    if (i == j) continue;
                    double d = Dist(best[i].O, best[j].O);
                    if (d < bestD) bestD = d;
                }
                if (bestD < double.MaxValue) nn.Add(bestD);
            }
            if (nn.Count == 0) { res.Error = "Couldn't compute a spacing between the found holes."; return res; }

            double minNn = double.MaxValue, maxNn = double.MinValue;
            foreach (var d in nn) { if (d < minNn) minNn = d; if (d > maxNn) maxNn = d; }

            res.HoleCount = best.Count;
            double sumR = 0; foreach (var h in best) sumR += h.R;
            res.HoleDiameterMm = 2.0 * (sumR / best.Count) * 1000.0;
            res.SpacingMm = minNn * 1000.0;
            res.MaxSpacingMm = maxNn * 1000.0;
            res.Uniform = (maxNn - minNn) <= 0.05 * minNn;
            res.Verified = res.SpacingMm > 0;

            res.Info = BuildInfo(res);
            await emit("Caliper", null, "done", res.HoleCount + " holes, spacing " + Trim(res.SpacingMm) + " mm" + (res.Uniform ? " (even)" : " (varies)"));
            return res;
        }

        // collapse points within 0.5mm of each other (same physical hole read twice, e.g. a split cylindrical face)
        private static List<HoleCyl> DedupePositions(List<HoleCyl> group)
        {
            var outp = new List<HoleCyl>();
            foreach (var h in group)
            {
                bool dup = false;
                foreach (var o in outp) { if (Dist(h.O, o.O) < 0.0005) { dup = true; break; } }
                if (!dup) outp.Add(h);
            }
            return outp;
        }

        private static double Dist(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // verdict first (Character #3), the NUMBER not an adjective (Character #2)
        private static string BuildInfo(HoleSpacingResult r)
        {
            if (r.Uniform)
                return r.HoleCount + " holes, ⌀" + Trim(r.HoleDiameterMm) + "mm, evenly spaced " + Trim(r.SpacingMm) +
                       "mm apart (center to center).";
            return r.HoleCount + " holes, ⌀" + Trim(r.HoleDiameterMm) + "mm — spacing varies, " + Trim(r.SpacingMm) +
                   "mm between the closest pair up to " + Trim(r.MaxSpacingMm) + "mm between the farthest (center to center), not evenly spaced.";
        }

        private static string Trim(double v) => v.ToString("0.##");
    }
}
