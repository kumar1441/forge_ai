using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CountThroughHolesResult
    {
        public int ThroughCount = -1;
        public int BlindCount;
        public int TotalCylindricalHoles;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CountThroughHoles (READ-ONLY, PART): "how many holes go all the way through the part", "count of
    /// through-holes", "how many through holes".
    ///
    /// test-loop wrong-answer finding grinder-count-through-holes (real "Coffee Grinder by Tommy" part, "how many
    /// holes go all the way through the part"): no action in the cloud's vocabulary, fell to list_features (a
    /// generic "48 features · 16 types" dump, never counting anything hole-specific). A genuinely NEW geometric-
    /// analysis capability, not a routing fix.
    ///
    /// Method (honest, topology-derived — Character #2/#4): find every cylindrical HOLE face (same
    /// Surface.CylinderParams primitive MeasureBoltCircle/HoleSpacing already use). For each, inspect its two rim
    /// LOOPS (the circular boundary at each end of the bore). For each rim, walk its edges and — via
    /// IEdge2.GetTwoAdjacentFaces2, the same primitive FilletChamfer already uses for edge classification — find
    /// the OTHER face sharing that edge (not the cylindrical wall itself). If that other face's own area is close
    /// to the hole's circular cross-section (π·r², within a small multiple) it's a purpose-built CAP — a flat
    /// bottom or a conical drill-point closing that end (BLIND). If the other face is much larger, that end opens
    /// into the part's general surface (OPEN). A hole is a THROUGH hole only when BOTH rims are OPEN.
    ///
    /// Robustness: PART only (an assembly needs per-component resolution this v1 doesn't attempt — refused
    /// honestly). A cylindrical face whose rim topology can't be read cleanly (irregular loop count, no adjacent
    /// face) is excluded from the count rather than guessed either way. No cylindrical holes at all → honest
    /// refusal (Rule #4), never a fabricated zero disguised as a real answer. Below 1.5mm diameter is excluded as
    /// thread-root/knurl noise, not a real hole; above 60% of the part's own smallest overall dimension is excluded
    /// as the part's main body cavity, not a hole drilled through it.
    ///
    /// KNOWN SCOPE LIMIT (found live on the real "Coffee Grinder by Tommy" model): a PartDoc can contain MULTIPLE
    /// solid BODIES (this one has 31 — components merged into one document without a boolean union, e.g. via a
    /// STEP import), and this method counts hole faces across ALL of them. On a merged multi-body doc like that,
    /// stacked/nested sub-parts (washers, retainers, etc.) each contribute their OWN hole faces, so the count can
    /// include the same physical hole several times over (once per body it passes through) — "how many holes go
    /// through THE PART" is genuinely ambiguous when "the part" is actually dozens of co-located bodies. Sound and
    /// correct on the much more common single/few-body part; flagged here so a future investigation into
    /// per-body-aware counting starts from this known gap instead of re-discovering it.
    /// </summary>
    public static class CountThroughHoles
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\bthrough[- ]?holes?\b")
                || Regex.IsMatch(c, @"\bholes?\b.{0,35}\b(go|goes|going|pass(?:es)?)\s+(all\s+the\s+way\s+)?through\b")
                || Regex.IsMatch(c, @"\bhow\s+many\s+holes?\b.{0,25}\b(through|all\s+the\s+way)\b");
        }

        private class HoleCyl { public Face2 Face; public double R; }

        public static async Task<CountThroughHolesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CountThroughHolesResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Counting through-holes works on a single part — open the .SLDPRT you want checked, not an assembly."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to check."; return res; }

            await emit("Scribe", "reading the solid and finding hole faces", "run", null);
            object[] bodies = null;
            try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to check — this part has no solid geometry."; return res; }

            var holes = new List<HoleCyl>();
            double[] unionBox = null;
            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                try { unionBox = Union(unionBox, body.GetBodyBox() as double[]); } catch { }
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface surf = null; try { surf = face.GetSurface() as Surface; } catch { }
                    bool isCyl = false; try { isCyl = surf != null && surf.IsCylinder(); } catch { }
                    if (!isCyl) continue;
                    double[] cp = null; try { cp = surf.CylinderParams as double[]; } catch { }
                    if (cp == null || cp.Length < 7) continue;
                    // CONCAVE only (test-loop false-positive found live: a grinder burr's knurled/toothed surface has
                    // hundreds of small CONVEX cylindrical faces — round ribs/teeth/pins where material fills the
                    // cylinder, not a bore. A real hole's face normal points INWARD toward its own axis (empty
                    // space is the cylinder interior); a boss/rib/tooth's normal points OUTWARD away from its axis
                    // (solid material fills it).
                    if (!IsConcave(face, surf, cp)) continue;
                    holes.Add(new HoleCyl { Face = face, R = cp[6] });
                }
            }

            // SIZE-BOUNDED (a second real false-positive class found live on the same part): sub-mm concave
            // cylindrical faces are thread roots / burr-grinder-tooth valleys, not functional holes; and a
            // concave cylinder nearly as wide as the part's own smallest overall dimension is the part's MAIN BODY
            // CAVITY (this grinder's ~77mm bean chamber), not a "hole" in the everyday sense of something drilled
            // through solid material. Both ends are excluded rather than guessed which one the user meant.
            if (unionBox != null && unionBox.Length >= 6)
            {
                double spanX = unionBox[3] - unionBox[0], spanY = unionBox[4] - unionBox[1], spanZ = unionBox[5] - unionBox[2];
                double minSpan = Math.Min(spanX, Math.Min(spanY, spanZ));
                const double MinDiaM = 0.0015;              // 1.5mm — below this is thread/tooth noise, not a real hole
                double maxDiaM = 0.6 * minSpan;              // a "hole" shouldn't be most of the part's own smallest dimension
                holes.RemoveAll(h => (h.R * 2.0) < MinDiaM || (h.R * 2.0) > maxDiaM);
            }
            if (holes.Count == 0)
            { res.Error = "No cylindrical hole faces found on this part."; return res; }
            res.TotalCylindricalHoles = holes.Count;

            int through = 0, blind = 0, unreadable = 0;
            foreach (var h in holes)
            {
                double circleArea = Math.PI * h.R * h.R;
                bool? end1Open = RimIsOpen(h.Face, circleArea, true);
                bool? end2Open = RimIsOpen(h.Face, circleArea, false);
                if (end1Open == null || end2Open == null) { unreadable++; continue; }
                if (end1Open.Value && end2Open.Value) through++;
                else blind++;
            }

            res.ThroughCount = through;
            res.BlindCount = blind;
            res.Verified = (through + blind) > 0;
            if (!res.Verified)
            {
                res.Error = "Found " + holes.Count + " cylindrical hole face(s) but couldn't read enough rim topology on any " +
                            "of them to tell through from blind (" + unreadable + " unreadable).";
                return res;
            }

            res.Info = through + " through-hole(s) go all the way through the part" +
                       (blind > 0 ? " (" + blind + " more are blind — don't fully pass through)" : "") +
                       (unreadable > 0 ? "; " + unreadable + " hole(s) had unreadable rim topology and were excluded from the count" : "") + ".";
            await emit("Scribe", null, "done", through + " through / " + blind + " blind / " + holes.Count + " total cylindrical holes");
            return res;
        }

        // is ONE rim of this cylindrical hole face OPEN (exits into the part's general surface) or CAPPED (a small
        // bottom face closing a blind hole)? firstLoop picks which of the face's (typically 2) boundary loops to
        // check. Returns null when the topology can't be read cleanly (excluded from the count, never guessed).
        private static bool? RimIsOpen(Face2 face, double circleArea, bool firstLoop)
        {
            try
            {
                object[] loops = face.GetLoops() as object[];
                if (loops == null || loops.Length == 0) return null;
                // sort loops for a stable, deterministic pick (order isn't guaranteed by the API) — by the average
                // axial position of their edges isn't available cheaply, so just take index 0 / last as the two ends;
                // a face with exactly 2 loops (the common clean-hole case) makes this unambiguous either way.
                Loop2 loop = (loops.Length >= 2)
                    ? (firstLoop ? loops[0] as Loop2 : loops[loops.Length - 1] as Loop2)
                    : loops[0] as Loop2;
                if (loop == null) return null;
                object[] edges = loop.GetEdges() as object[];
                if (edges == null || edges.Length == 0) return null;

                bool sawOpen = false, sawCapped = false;
                foreach (var eo in edges)
                {
                    var e = eo as Edge; if (e == null) continue;
                    object[] adj = null; try { adj = e.GetTwoAdjacentFaces2() as object[]; } catch { }
                    if (adj == null || adj.Length < 2) continue;
                    var f1 = adj[0] as Face2; var f2 = adj[1] as Face2;
                    Face2 other = (f1 != null && !ReferenceEquals(f1, face)) ? f1 : f2;
                    if (other == null || ReferenceEquals(other, face)) continue;
                    double otherArea = 0; try { otherArea = (double)other.GetArea(); } catch { }
                    if (otherArea <= 0) continue;
                    if (otherArea <= 2.5 * circleArea) sawCapped = true; else sawOpen = true;
                }
                if (!sawOpen && !sawCapped) return null;
                // one edge disagreeing with another on the SAME rim is rare (a chamfered rim can split the loop into
                // multiple edges) — treat the rim as capped if ANY edge on it looks capped, open only if ALL do.
                return sawOpen && !sawCapped;
            }
            catch { return null; }
        }

        // a real HOLE's face normal points INWARD toward its own axis (the empty bore is the cylinder's interior,
        // solid material is outside it); a boss/rib/tooth/knurl bump's normal points OUTWARD away from its axis
        // (solid material fills the cylinder). Sample at the face's own bbox centre (same closest-point-projection
        // technique WallThickness/AddHole already rely on) rather than trusting the raw CylinderParams origin,
        // which is just a point ON the infinite axis, not necessarily near this specific face.
        private static double[] Union(double[] acc, double[] b)
        {
            if (b == null || b.Length < 6) return acc;
            if (acc == null) return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
            return new[]
            {
                Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
            };
        }

        private static bool IsConcave(Face2 face, Surface surf, double[] cylParams)
        {
            try
            {
                double[] box = null; try { box = face.GetBox() as double[]; } catch { }
                if (box == null || box.Length < 6) return false;
                double[] center = { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };
                double[] p = null; try { p = face.GetClosestPointOn(center[0], center[1], center[2]) as double[]; } catch { }
                if (p == null || p.Length < 3) return false;

                double[] axisO = { cylParams[0], cylParams[1], cylParams[2] };
                double[] axisD = { cylParams[3], cylParams[4], cylParams[5] };
                double dl = Math.Sqrt(axisD[0] * axisD[0] + axisD[1] * axisD[1] + axisD[2] * axisD[2]);
                if (dl < 1e-9) return false;
                axisD = new[] { axisD[0] / dl, axisD[1] / dl, axisD[2] / dl };

                double[] v = { p[0] - axisO[0], p[1] - axisO[1], p[2] - axisO[2] };
                double along = v[0] * axisD[0] + v[1] * axisD[1] + v[2] * axisD[2];
                double[] radial = { v[0] - along * axisD[0], v[1] - along * axisD[1], v[2] - along * axisD[2] };
                double rl = Math.Sqrt(radial[0] * radial[0] + radial[1] * radial[1] + radial[2] * radial[2]);
                if (rl < 1e-9) return false;
                radial = new[] { radial[0] / rl, radial[1] / rl, radial[2] / rl };   // unit, points OUTWARD from axis toward p

                double[] n = null; try { n = surf.EvaluateAtPoint(p[0], p[1], p[2]) as double[]; } catch { }
                if (n == null || n.Length < 3) return false;
                double nl = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                if (nl < 1e-9) return false;
                double[] nu = { n[0] / nl, n[1] / nl, n[2] / nl };
                bool reversed = false; try { reversed = face.FaceInSurfaceSense(); } catch { }
                if (reversed) nu = new[] { -nu[0], -nu[1], -nu[2] };   // make it point OUT of the solid

                double dot = nu[0] * radial[0] + nu[1] * radial[1] + nu[2] * radial[2];
                return dot < 0;   // outward normal pointing back TOWARD the axis => concave bore, not a convex boss
            }
            catch { return false; }
        }
    }
}
