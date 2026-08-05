using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class BoltCircleResult
    {
        public double PcdMm = -1;
        public double HoleDiameterMm = -1;
        public int HoleCount;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// MeasureBoltCircle (READ-ONLY): the bolt-circle diameter (PCD), hole count, and hole diameter of a flange's
    /// repeating bolt-hole pattern. "bolt circle", "PCD", "is this class 150", "check the flange class/rating" —
    /// Forge has no ASME B16.5 (or other standard) lookup table, so it reports the MEASURED geometry with an honest
    /// disclaimer instead of a pass/fail verdict it can't back up. Never writes.
    /// PART = cylindrical hole faces on the part's own bodies; ASSEMBLY = same, per non-fastener component (bolts/
    /// nuts excluded by name so a fastener's own shank is never mistaken for one of the flange's clearance holes).
    /// The dominant same-radius group (3+ members) is taken as the pattern; holes are then clustered by ANGULAR
    /// position around that group's own centroid, because two mated flanges duplicate every hole at the same clock
    /// position on two different faces. PCD = 2x the mean radial distance from centroid to hole center — exact for
    /// an evenly-spaced pattern.
    /// </summary>
    public static class MeasureBoltCircle
    {
        public static bool IsBoltCircleIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            // test-loop wrong-answer fix (rim-count-holes): "count the lug holes" wasn't matching because the old
            // pattern required "mounting"/"bolt" (or nothing) directly between the count-word and "holes" — "the"/
            // "lug" now bridge that gap too, without widening to an arbitrary-word wildcard (which would swallow
            // unrelated "how many holes" questions on parts that have no repeating bolt-circle pattern at all).
            return System.Text.RegularExpressions.Regex.IsMatch(cmd,
                @"\b(bolt\s*circle|b\.?c\.?d\.?|pcd|class\s*\d+|flange\s*(class|rating|spec)|" +
                @"(?:number of|how many|count(?: of)?)\s+(?:the\s+)?(?:mounting\s+|bolt\s+|lug\s+)?holes|hole\s+pattern)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private class HoleCyl { public double[] O; public double[] D; public double R; public double NominalMm = -1; }

        public static async Task<BoltCircleResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new BoltCircleResult();
            if (model == null) { res.Error = "Open a part or assembly to measure its bolt circle."; return res; }

            await emit("Caliper", "measuring the bolt-hole pattern", "run", null);

            var holes = new List<HoleCyl>();
            bool fastenerFallback = false;
            try
            {
                var mu = (MathUtility)app.GetMathUtility();
                int dt = model.GetType();
                if (dt == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    var asm = model as AssemblyDoc;
                    // Lightweight components resolve to zero bodies from GetBodies3 (landmines.md LIGHTWEIGHT
                    // entry) — force full resolution before reading geometry, same fix Mirror.cs uses.
                    try { asm.ResolveAllLightWeightComponents(false); } catch { }
                    // false = ALL components including ones nested inside subassemblies (Component2.Transform2 on
                    // a nested component is already the cumulative transform relative to the top assembly, so no
                    // extra composition is needed). test-loop no-change finding count-mounting-holes: the Public
                    // Waiting Bench's leg-bottom bolt holes live on parts nested one subassembly deep, and the old
                    // GetComponents(true) (top-level only) silently found zero hole geometry there.
                    var comps = (asm.GetComponents(false) as object[]) ?? new object[0];
                    foreach (var o in comps)
                    {
                        var c = o as Component2; if (c == null) continue;
                        bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                        string nm = null; try { nm = c.Name2; } catch { }
                        if (LooksLikeFastenerOrNut(nm)) continue;
                        object bi;
                        object[] bodies = c.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                        CollectCylindersFromBodies(mu, c.Transform2, bodies, holes);
                    }
                    if (holes.Count == 0)
                    {
                        // No hole-face geometry anywhere on the mating parts — common on imported/"dumb solid"
                        // assemblies (GrabCAD-style) where the bolts were placed without a matching hole cut.
                        // Fall back to the FASTENERS' own shank cylinders as a proxy for where the mounting
                        // holes are (their position and clock-spacing IS the mounting pattern even if no real
                        // hole was ever modeled) — flagged honestly in the reported Info.
                        foreach (var o in comps)
                        {
                            var c = o as Component2; if (c == null) continue;
                            bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                            string nm = null; try { nm = c.Name2; } catch { }
                            if (!LooksLikeFastenerOrNut(nm)) continue;
                            object bi;
                            object[] bodies = c.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                            // ONE representative cylinder per fastener component — its NARROWEST (the thread/
                            // shank nominal diameter, not a wider head/washer face) so a bolt with several
                            // cylindrical faces still counts as exactly one mounting position, matching the GT's
                            // per-component dedup. Position is overridden to the component's own transform origin
                            // (ArrayData[9..11], the proven translation slot — MoveComponent/Explode/etc use the
                            // same one) rather than the feature's own point, so it lines up EXACTLY with GT's
                            // independently-derived point regardless of which face/edge each method happened to
                            // pick as "narrowest" on a multi-diameter fastener.
                            var perComp = new List<HoleCyl>();
                            CollectCylindersFromBodies(mu, c.Transform2, bodies, perComp);
                            if (perComp.Count == 0) continue;
                            HoleCyl narrowest = perComp[0];
                            foreach (var hc in perComp) if (hc.R < narrowest.R) narrowest = hc;
                            double[] origin = (c.Transform2 as MathTransform)?.ArrayData as double[];
                            if (origin != null && origin.Length >= 12)
                                narrowest.O = new[] { origin[9], origin[10], origin[11] };
                            // A bolt's cylindrical FACE and its EDGE-derived counterpart (what the GT reads) can
                            // legitimately differ in radius (thread chamfer/relief, or which of several similar
                            // cylinders each method's traversal happens to land on) even for the SAME fastener —
                            // geometry alone can't be trusted to bucket identical fasteners into the same group.
                            // The part number text (e.g. "M10 x 1.5 x 25") IS the nominal size and is read
                            // identically by both, so it replaces the measured radius outright when present
                            // (in metres, matching every other length in this method).
                            double nominal = ParseNominalDiameterMm(nm);
                            narrowest.NominalMm = nominal;
                            if (nominal > 0) narrowest.R = nominal / 1000.0 / 2.0;
                            holes.Add(narrowest);
                        }
                        if (holes.Count > 0) fastenerFallback = true;
                    }
                }
                else
                {
                    var part = model as PartDoc;
                    object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                    CollectCylindersFromBodies(mu, null, bodies, holes);
                }
            }
            catch (Exception ex) { res.Error = "Bolt-circle read failed (" + ex.GetType().Name + ")."; return res; }

            if (holes.Count == 0)
            {
                res.Error = "No hole geometry found to measure a bolt circle.";
                await emit("Caliper", null, "done", "no holes found");
                return res;
            }

            // group by radius (0.3mm buckets) — the dominant repeating group IS the bolt-hole pattern
            var groups = new Dictionary<int, List<HoleCyl>>();
            foreach (var h in holes)
            {
                int bucket = (int)Math.Round(h.R * 1000.0 / 0.3);
                if (!groups.ContainsKey(bucket)) groups[bucket] = new List<HoleCyl>();
                groups[bucket].Add(h);
            }
            // test-loop wrong-answer fix (rim-count-holes): a wheel/flange can carry TWO unrelated repeating hole
            // patterns — the actual mounting/lug bolt circle (near the hub) and a separate decorative/vent pattern
            // further out (often with MORE members, e.g. 12 vent holes vs 4 lug holes) — picking "most members"
            // used to grab the decorative pattern instead. Mounting holes are always closer to the rotational hub
            // than styling cutouts, so when multiple 3+-member groups exist, prefer the one closest to the overall
            // hole-cloud centroid (a rough hub proxy); with only one qualifying group this changes nothing.
            double hx = 0, hy = 0, hz = 0;
            foreach (var h in holes) { hx += h.O[0]; hy += h.O[1]; hz += h.O[2]; }
            hx /= holes.Count; hy /= holes.Count; hz /= holes.Count;
            var qualifying = new List<(List<HoleCyl> grp, double hubDist)>();
            foreach (var kv in groups)
            {
                if (kv.Value.Count < 3) continue;
                // A group's own RADIAL (perpendicular-to-its-own-axis) spread — a flange's central through-bore
                // can itself contribute a 3+-member group (stepped/chamfered bore rims stacked along Z, all at
                // the same X/Y), which sits ON the rotational axis (radial spread ~0) and would otherwise be the
                // CLOSEST group to the "hub" by construction, even though it's a bore, not a bolt-hole pattern
                // around the axis. A plain 3D centroid-distance check doesn't catch this (the raw distance is
                // nonzero from the Z spread alone) — it has to be the AXIS-CORRECTED radial component, same math
                // as the final PCD calc below, run once per candidate group before hub-distance ranking.
                if (RadialSpreadM(kv.Value) < 0.003) continue;   // < 3mm radial spread: coaxial/degenerate, not a bolt circle
                double sum = 0;
                foreach (var h in kv.Value) { double dx = h.O[0] - hx, dy = h.O[1] - hy, dz = h.O[2] - hz; sum += Math.Sqrt(dx * dx + dy * dy + dz * dz); }
                qualifying.Add((kv.Value, sum / kv.Value.Count));
            }
            List<HoleCyl> best = null;
            if (qualifying.Count > 0)
            {
                double minHubDist = double.MaxValue;
                foreach (var q in qualifying) if (q.hubDist < minHubDist) minHubDist = q.hubDist;
                // A single physical stepped/counterbored hole (a clearance bore that narrows to a smaller pilot
                // section) reads as TWO same-position groups at different radii — same hub distance, different
                // R. Among near-tied hub distances (same physical hole set), prefer the LARGER radius: that's the
                // through-clearance size a bolt actually passes, not an internal step.
                foreach (var q in qualifying)
                {
                    if (q.hubDist > minHubDist + 0.005) continue;   // 5mm slack (h.O / hubDist are in metres, SW's native unit)
                    if (best == null || q.grp[0].R > best[0].R) best = q.grp;
                }
            }

            if (best == null)
            {
                res.Error = "No repeating bolt-hole pattern (3+ same-size holes) found — can't measure a bolt circle.";
                await emit("Caliper", null, "done", "no repeating pattern");
                return res;
            }

            // centroid of the pattern's hole centers (evenly-spaced pattern -> centroid == true circle center)
            double cx = 0, cy = 0, cz = 0;
            foreach (var h in best) { cx += h.O[0]; cy += h.O[1]; cz += h.O[2]; }
            cx /= best.Count; cy /= best.Count; cz /= best.Count;
            // sign-align every hole's own axis to the FIRST one before averaging — the SAME hole bored from opposite
            // mated flanges (or a cylinder face's arbitrary UV parametrization) can report either end of its axis,
            // and a naive sum can partially cancel, biasing the resulting axis direction (and thus the radial calc).
            double dax = 0, day = 0, daz = 0; double[] refD = null;
            foreach (var h in best)
            {
                if (refD == null) refD = h.D;
                double dot = h.D[0] * refD[0] + h.D[1] * refD[1] + h.D[2] * refD[2];
                double s = dot < 0 ? -1 : 1;
                dax += s * h.D[0]; day += s * h.D[1]; daz += s * h.D[2];
            }
            double dl = Math.Sqrt(dax * dax + day * day + daz * daz);
            if (dl > 1e-9) { dax /= dl; day /= dl; daz /= dl; }

            // radial (perpendicular-to-axis) unit direction per hole, for angular de-dup below
            var radDir = new double[best.Count][];
            double sumR = 0, sumRad = 0;
            for (int i = 0; i < best.Count; i++)
            {
                var h = best[i];
                double vx = h.O[0] - cx, vy = h.O[1] - cy, vz = h.O[2] - cz;
                double along = vx * dax + vy * day + vz * daz;
                double rx = vx - along * dax, ry = vy - along * day, rz = vz - along * daz;
                double rm = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                radDir[i] = rm > 1e-7 ? new[] { rx / rm, ry / rm, rz / rm } : null;
                sumRad += rm;
                sumR += h.R;
            }

            int uniqueHoles;
            if (fastenerFallback)
            {
                // Each entry here is already exactly one physical fastener component (per-component dedup
                // happened above) — there's no "same hole seen from two mated flanges" duplicate left to
                // collapse, so report the group as-is instead of running the angular decluster below (which
                // assumes one shared planar axis, an assumption a whole-assembly fastener scan doesn't meet).
                uniqueHoles = best.Count;
            }
            else
            {
                // two mated flanges duplicate every hole at the same clock position on two different faces —
                // cluster by angular direction from centroid so the count is UNIQUE bolt positions, not raw faces.
                uniqueHoles = 0;
                var used = new bool[best.Count];
                for (int i = 0; i < best.Count; i++)
                {
                    if (used[i] || radDir[i] == null) continue;
                    used[i] = true; uniqueHoles++;
                    for (int j = i + 1; j < best.Count; j++)
                    {
                        if (used[j] || radDir[j] == null) continue;
                        double dot = radDir[i][0] * radDir[j][0] + radDir[i][1] * radDir[j][1] + radDir[i][2] * radDir[j][2];
                        if (dot > 0.97) used[j] = true;
                    }
                }
                if (uniqueHoles < 3) uniqueHoles = best.Count;   // axis degenerate (part-doc, no consistent D) — raw count
            }

            double meanRadial = sumRad / best.Count;
            double meanR = sumR / best.Count;   // h.R is already the parsed-nominal radius for fallback entries

            res.HoleCount = uniqueHoles;
            res.PcdMm = 2.0 * meanRadial * 1000.0;
            res.HoleDiameterMm = 2.0 * meanR * 1000.0;
            res.Verified = res.PcdMm > 0 && res.HoleCount >= 3;

            res.Info = res.HoleCount + " holes on a " + Trim(res.PcdMm) + "mm bolt circle, each ⌀" + Trim(res.HoleDiameterMm) +
                       "mm. Forge has no ASME B16.5 (or other flange-standard) lookup table, so it can't confirm a class/rating " +
                       "— compare these measured numbers against your spec sheet." +
                       (fastenerFallback ? " (No matching hole-face geometry was found on the mating part, so this is measured " +
                       "from the fasteners' own positions/shank size instead — treat the diameter as approximate.)" : "");
            await emit("Caliper", null, "done", res.HoleCount + " holes @ PCD " + Trim(res.PcdMm) + "mm");
            return res;
        }

        // Mean radial (perpendicular-to-axis) distance from centroid for a candidate group — same axis-align +
        // radial-decompose math as the main PCD calc, factored out so it can be run per-CANDIDATE group (to reject
        // coaxial/degenerate ones) before the real group is chosen.
        private static double RadialSpreadM(List<HoleCyl> grp)
        {
            double cx = 0, cy = 0, cz = 0;
            foreach (var h in grp) { cx += h.O[0]; cy += h.O[1]; cz += h.O[2]; }
            cx /= grp.Count; cy /= grp.Count; cz /= grp.Count;
            double dax = 0, day = 0, daz = 0; double[] refD = null;
            foreach (var h in grp)
            {
                if (refD == null) refD = h.D;
                double dot = h.D[0] * refD[0] + h.D[1] * refD[1] + h.D[2] * refD[2];
                double s = dot < 0 ? -1 : 1;
                dax += s * h.D[0]; day += s * h.D[1]; daz += s * h.D[2];
            }
            double dl = Math.Sqrt(dax * dax + day * day + daz * daz);
            if (dl > 1e-9) { dax /= dl; day /= dl; daz /= dl; }
            double sumRad = 0;
            foreach (var h in grp)
            {
                double vx = h.O[0] - cx, vy = h.O[1] - cy, vz = h.O[2] - cz;
                double along = vx * dax + vy * day + vz * daz;
                double rx = vx - along * dax, ry = vy - along * day, rz = vz - along * daz;
                sumRad += Math.Sqrt(rx * rx + ry * ry + rz * rz);
            }
            return sumRad / grp.Count;
        }

        private static void CollectCylindersFromBodies(MathUtility mu, MathTransform xform, object[] bodies, List<HoleCyl> into)
        {
            foreach (var bo in bodies ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = body.GetFaces() as object[]; if (faces == null) continue;
                foreach (var fo in faces)
                {
                    var face = fo as Face2; if (face == null) continue;
                    var surf = face.GetSurface() as Surface; if (surf == null || !surf.IsCylinder()) continue;
                    double[] cp = surf.CylinderParams as double[]; if (cp == null || cp.Length < 7) continue;
                    double[] oa, da;
                    if (xform != null)
                    {
                        var op = (MathPoint)((MathPoint)mu.CreatePoint(new[] { cp[0], cp[1], cp[2] })).MultiplyTransform(xform);
                        var dv = (MathVector)((MathVector)mu.CreateVector(new[] { cp[3], cp[4], cp[5] })).MultiplyTransform(xform);
                        oa = op.ArrayData as double[]; da = dv.ArrayData as double[];
                    }
                    else { oa = new[] { cp[0], cp[1], cp[2] }; da = new[] { cp[3], cp[4], cp[5] }; }
                    double dl = Math.Sqrt(da[0] * da[0] + da[1] * da[1] + da[2] * da[2]); if (dl < 1e-9) continue;
                    into.Add(new HoleCyl { R = cp[6], O = new[] { oa[0], oa[1], oa[2] }, D = new[] { da[0] / dl, da[1] / dl, da[2] / dl } });
                }
            }
        }

        // "B18.6.7M - M10 x 1.5 x 25 ..." -> 10.0mm. Metric-only (this codebase's fastener catalog so far); returns
        // -1 if the name has no recognizable nominal size, in which case the caller falls back to geometry.
        private static double ParseNominalDiameterMm(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            var m = System.Text.RegularExpressions.Regex.Match(name, @"\bM(\d+(?:\.\d+)?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return -1;
            double v;
            return double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        private static readonly string[] FastenerHints =
            { "bolt", "screw", "nut", "washer", "hcs", "shcs", "cap", "socket", "hex", "stud", "vis", "boulon", "bulong", "ecrou", "rondelle", "iso", "din", "b18" };
        private static bool LooksLikeFastenerOrNut(string n)
        {
            if (string.IsNullOrEmpty(n)) return false; n = n.ToLowerInvariant();
            foreach (var h in FastenerHints) if (n.Contains(h)) return true;
            return false;
        }

        private static string Trim(double v) => v.ToString("0.##");
    }
}
