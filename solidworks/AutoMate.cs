using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class MateResult
    {
        public int Detected;
        public int Mated;
        public int Seated;   // truly seated flush (Flush + Fixed) — the honest "done" number
        public int Flush;    // seated flush on the first pass
        public int Fixed;    // proud on first pass, re-seated by Mender's retry
        public int Proud;    // still not flush after retry — flagged for review
        public int Failed;
        public bool RebuildClean = true;
        public string Error;
        public int PatternInstancesTotal;    // pattern-driven bolts found but never independently mated (SKIP by design)
        public int PatternInstancesFollowed; // of those, how many are ALSO flush post-mate (the pattern re-derived from the seed's new position)
    }

    // a cylindrical face reduced to an assembly-space axis + radius (for coaxial matching)
    internal class Cyl
    {
        public Face2 Face;
        public double[] O; // a point on the axis, assembly coords (m)
        public double[] D; // unit axis direction, assembly coords
        public double R;   // radius (m)
        public Component2 Comp;
    }

    // a planar face reduced to an assembly-space point + normal (for seating)
    internal class Plane
    {
        public Face2 Face;
        public double[] P; // a point on the plane, assembly coords
        public double[] N; // unit normal, assembly coords
        public double Area; // face area (m²) — used to pick the bolt HEAD face (the largest flat), not the tip
        public Component2 Comp; // owning component (for logging which part the flange face belongs to)
    }

    internal class Pair { public Cyl Fastener; public Cyl Hole; }

    // one bolt's mating attempt — the pair, BOTH mates we added, the component (for the over-define guard), and the
    // seat fit we actually mated (head + flange faces), so Sentinel can measure THAT pair, not some other one
    internal class Attempt { public Pair P; public Mate2 Concentric; public Mate2 Seat; public Component2 Comp; public SeatFit Fit; }

    // the head→flange seating interface the shared finder picked: which faces, the pre-solve axial distance, and the
    // flange face's axial position + the bolt head's axial position along the hole axis (both relative to the hole origin)
    internal class SeatFit { public Plane Head; public Plane Flange; public double GapMm; public double FaceOffMm; public double HeadOffMm; }

    // the bolt's HEAD end identified by RADIAL EXTENT (head is wider than shank). Bearing = the annular ⊥ face around
    // the shank at the head end (the underside bearing surface that seats on the flange). HeadT/TipT = axial positions.
    // BearingRout = the bearing's outer radius (across-flats/corners) — a fastener whose bearing is NOT wider than the
    // hole would pass through it, so "gap=0 inside the hole" is nonsense and must be rejected.
    internal class BoltGeom { public Plane Bearing; public double HeadT; public double TipT; public double ShankR; public double BearingRout; public string Log; }

    /// <summary>
    /// Auto-mate — Forge's first "doer". A named crew runs in sequence:
    ///   Gauge    → finds every fastener sitting coaxially inside a hole (no selection needed)
    ///   Torque   → concentric-mates each (aligns the axis), then coincident-mates it (seats it down)
    ///   Sentinel → rebuilds and confirms nothing broke
    /// The coincident (seating) mate is what makes a lifted bolt slide down into place.
    ///
    /// NOTE (honest): the geometry is best-effort and iterated on real assemblies — assembly-space
    /// faces, coaxial tolerances, and seating-face selection are the things to tune.
    /// </summary>
    public static class AutoMate
    {
        private static readonly string[] FastenerHints =
            { "bolt", "screw", "hcs", "shcs", "capscrew", "cap screw", "fastener",
              "hex bolt", "hex ", "socket", "socket head", "machine screw", "stud", "sems",
              "allen", "grub", "cheese", "din", "iso", "b18",   // standards
              "bulong", "vit",                                   // Vietnamese: bolt, screw
              "vis", "boulon", "goujon" };                       // French: screw, bolt, stud

        public static bool IsMateIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                cmd, @"\b(mate|mates|fasten|fastens|automate|auto-mate)\b");
        }

        // Not a bolt/screw even if it matches a hint — nuts, washers, keys, pins go on/around, not INTO a hole.
        private static readonly string[] NotFastener =
            { "nut", "ecrou", "washer", "rondelle", "clavette", "key", "pin", "goupille",
              "4035", "4032", "4033", "4034", "7089", "7090", "7091", "8738" }; // ISO nut/washer/pin standards

        private static bool LooksLikeFastener(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            foreach (var x in NotFastener) if (n.Contains(x)) return false;
            foreach (var h in FastenerHints) if (n.Contains(h)) return true;
            return false;
        }

        private static readonly string[] NutHints =
            { "nut", "ecrou", "hexnut", "iso 4032", "iso 4033", "iso 4034", "iso 4035", "iso 8673",
              "din 934", "din 985", "4032", "4034", "4035" };
        private static bool LooksLikeNut(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            foreach (var h in NutHints) if (n.Contains(h)) return true;
            return false;
        }

        // largest ⊥-to-axis face area on a component's face list — the bolt HEAD (heads are the widest flat).
        private static double MaxPerpArea(List<Plane> planes, double[] ax)
        {
            double mx = 0;
            foreach (var f in planes)
                if (Math.Abs(Math.Abs(f.N[0] * ax[0] + f.N[1] * ax[1] + f.N[2] * ax[2]) - 1.0) <= 5e-2 && f.Area > mx) mx = f.Area;
            return mx;
        }

        private static bool PerpAxis(double[] n, double[] ax) =>
            Math.Abs(Math.Abs(n[0] * ax[0] + n[1] * ax[1] + n[2] * ax[2]) - 1.0) <= 5e-2;
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        // largest ⊥-to-axis planar face on a face list (the bolt HEAD, or a big flat flange face).
        private static Plane LargestPerp(List<Plane> planes, double[] ax)
        {
            Plane best = null;
            foreach (var f in planes) { if (!PerpAxis(f.N, ax)) continue; if (best == null || f.Area > best.Area) best = f; }
            return best;
        }

        private const int MATE_OK = (int)swAddMateError_e.swAddMateError_NoError;              // 1 (NOT 0)
        private const int MATE_OVERDEF = (int)swAddMateError_e.swAddMateError_OverDefinedAssembly; // 5

        // Radial distance from the hole axis to a ⊥ face's NEAREST material point (its inner radius). The axis crosses
        // the face plane at the hole center; GetClosestPointOn (part-local) returns the inner rim. ~shankR for the
        // annular head-underside, ~0 for a solid disk (head top / tip). Distance is rigid-invariant.
        private static double RadialInner(MathUtility mu, Pair p, Plane f)
        {
            try
            {
                double[] ax = p.Hole.D, O = p.Hole.O;
                double t = Ax(f.P, O, ax);
                double[] cAsm = { O[0] + t * ax[0], O[1] + t * ax[1], O[2] + t * ax[2] };
                if (f.Comp == null) return 0;
                var inv = (MathTransform)f.Comp.Transform2.Inverse();
                var cpt = (MathPoint)((MathPoint)mu.CreatePoint(cAsm)).MultiplyTransform(inv);
                double[] cl = cpt.ArrayData as double[];
                double[] near = f.Face.GetClosestPointOn(cl[0], cl[1], cl[2]) as double[];
                if (near == null || near.Length < 3) return 0;
                double dx = near[0] - cl[0], dy = near[1] - cl[1], dz = near[2] - cl[2];
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            catch { return 0; }
        }

        // Identify the bolt HEAD by RADIAL EXTENT (not area/normals). For each ⊥ face: inner radius rin = RadialInner,
        // outer radius rout = sqrt(area/π + rin²). The bearing (head underside) is the ANNULAR face (rin ≈ shankR) with
        // the LARGEST rout — the hex head is wider than the shank; the head TOP is solid (rin≈0) and the tip is small.
        private static BoltGeom ComputeBoltGeom(MathUtility mu, Pair p)
        {
            var g = new BoltGeom { ShankR = p.Fastener.R };
            double[] ax = p.Hole.D, O = p.Hole.O;
            var planes = CollectPlanes(mu, p.Fastener.Comp);
            double tMin = double.MaxValue, tMax = -double.MaxValue;
            Plane bearing = null; double bestRout = 0, bIn = 0;
            int perpN = 0;
            foreach (var f in planes)
            {
                double t = Ax(f.P, O, ax);
                if (t < tMin) tMin = t; if (t > tMax) tMax = t;
                if (!PerpAxis(f.N, ax)) continue;
                perpN++;
                double rin = RadialInner(mu, p, f);
                double rout = Math.Sqrt(Math.Max(0, f.Area / Math.PI) + rin * rin);
                if (rin > 0.5 * g.ShankR && rin < 2.5 * g.ShankR && rout > 1.2 * g.ShankR && rout > bestRout)
                { bestRout = rout; bearing = f; bIn = rin; }
            }
            g.Bearing = bearing;
            g.BearingRout = bestRout;
            if (bearing != null)
            {
                g.HeadT = Ax(bearing.P, O, ax);
                g.TipT = (Math.Abs(tMax - g.HeadT) >= Math.Abs(tMin - g.HeadT)) ? tMax : tMin;   // far end from head
            }
            g.Log = "shankR=" + (g.ShankR * 1000).ToString("F1") + (bearing != null
                ? " bearing[rin=" + (bIn * 1000).ToString("F1") + " rout=" + (bestRout * 1000).ToString("F1") +
                  " headT=" + (g.HeadT * 1000).ToString("F0") + " tipT=" + (g.TipT * 1000).ToString("F0") + "]"
                : " bearing=NONE(perp=" + perpN + ")");
            return g;
        }

        // Ranked head→flange seating candidates, chosen by HOLE GEOMETRY alone (no pre-solve gap filter — the bolt is
        // still floating, so head-to-face distance is meaningless until the mate solves). Reference axis is the
        // stationary hole axis. Among opposing (head-underside ↔ flange) pairs, rank by the flange face nearest the
        // bolt head ALONG the axis — i.e. the hole opening on the head side — tie-broken by largest flange area. The
        // coincident mate then pulls the head to that face. GapMm here is only the pre-solve axial distance (for ranking
        // + logging); the REAL seat gap is measured by Sentinel post-rebuild.
        private static List<SeatFit> FindSeatCandidates(MathUtility mu, Pair p, List<Plane> flange, out string diag)
        {
            var geom = ComputeBoltGeom(mu, p);
            if (geom.Bearing == null) { diag = geom.Log + " → NO bearing face"; return new List<SeatFit>(); }
            double[] ax = p.Hole.D, O = p.Hole.O;
            Plane bear = geom.Bearing;
            double tHead = geom.HeadT;
            int flangePerp = 0;
            var cands = new List<SeatFit>();
            foreach (var g in flange)
            {
                if (!PerpAxis(g.N, ax)) continue;                       // the flange faces on the axis; AxisHitsFace filters to the annulus
                flangePerp++;
                double tG = (g.P[0] - O[0]) * ax[0] + (g.P[1] - O[1]) * ax[1] + (g.P[2] - O[2]) * ax[2];
                cands.Add(new SeatFit { Head = bear, Flange = g, GapMm = Math.Abs(tHead - tG) * 1000.0, FaceOffMm = tG * 1000.0, HeadOffMm = tHead * 1000.0 });
            }
            // seat the bearing on the flange face nearest the head along the axis (the hole opening on the head side);
            // tie (within 1mm) → larger flange area
            cands.Sort((x, y) =>
            {
                double dx = Math.Round(x.GapMm), dy = Math.Round(y.GapMm);
                if (dx != dy) return dx.CompareTo(dy);
                return y.Flange.Area.CompareTo(x.Flange.Area);
            });
            diag = geom.Log + " flangePerp=" + flangePerp + " cands=" + cands.Count +
                   (cands.Count > 0 ? " nearest=" + cands[0].GapMm.ToString("F1") + "mm" : "");
            return cands;
        }

        // Seating gap (mm) = nearest valid head→flange candidate. -1 = no interface (can't confirm).
        private static double SeatGap(MathUtility mu, Pair p, List<Plane> flange)
        {
            string d;
            var c = FindSeatCandidates(mu, p, flange, out d);
            return c.Count == 0 ? -1 : c[0].GapMm;
        }

        // axial position of point P along the hole axis, relative to the hole origin
        private static double Ax(double[] P, double[] O, double[] ax)
        { return (P[0] - O[0]) * ax[0] + (P[1] - O[1]) * ax[1] + (P[2] - O[2]) * ax[2]; }

        // POST-REBUILD orientation check, POSITIONS only (no normals — they flip with face sense). The bolt is seated
        // correctly iff its far end (tip) is on the flange-BODY side of the seat face (shank went through the flange),
        // not the head side. flange body direction = toward the flange's other large ⊥ face; tip = the bolt ⊥ face
        // farthest from its head (largest ⊥ face). If tip and body are on the same side of the seat → OK, else FLIPPED.
        // POST-REBUILD orientation check. The physical requirement: the shank travels from the bearing face INTO the
        // material — shank direction (head→tip) is OPPOSITE the seat face's outward normal → dot ≈ -1. Cross-checked
        // (sense-independently) by counting flange faces strictly BETWEEN head and tip (shank passes through the stack,
        // not dangling in air). Fail → the caller's flip-retry inverts the concentric alignment.
        private static bool OrientedCorrectly(MathUtility mu, Pair p, Plane seat, List<Plane> flangeFresh, out string dbg)
        {
            double[] ax = p.Hole.D, O = p.Hole.O;
            var geom = ComputeBoltGeom(mu, p);      // head/tip by RADIAL EXTENT, on the post-rebuild pose
            if (geom.Bearing == null) { dbg = "orient[no bearing]"; return true; }
            double headT = geom.HeadT, tipT = geom.TipT;
            double s = Math.Sign(tipT - headT);
            double[] shankDir = { s * ax[0], s * ax[1], s * ax[2] };   // head→tip along the axis
            double dotSN = Dot(shankDir, seat.N);

            double lo = Math.Min(headT, tipT), hi = Math.Max(headT, tipT);
            int through = 0;
            foreach (var g in flangeFresh)
            {
                if (!PerpAxis(g.N, ax)) continue;
                double tg = Ax(g.P, O, ax);
                if (tg > lo + 1e-3 && tg < hi - 1e-3) through++;
            }
            bool ok = dotSN < -0.9;
            dbg = "orient[headT=" + (headT * 1000).ToString("F0") + " tipT=" + (tipT * 1000).ToString("F0") +
                  " dot(shank,seatN)=" + dotSN.ToString("F2") + " dot(ax,seatN)=" + Dot(ax, seat.N).ToString("F2") +
                  " through=" + through + "→" + (ok ? "OK" : "FLIP") + "]";
            return ok;
        }

        // Distance (mm) from the fastener's ⊥ face NEAREST the seat face (i.e. the actually-mated face) to the seat,
        // along the hole axis — ~0 when truly seated. Using the nearest face (not the ambiguous "bearing") is correct
        // for a symmetric nut too: its far face is a nut-height away, so keying on the bearing wrongly read that height.
        private static double MatedGapMm(MathUtility mu, Pair p, Plane seat)
        {
            double[] ax = p.Hole.D, O = p.Hole.O;
            double tSeat = Ax(seat.P, O, ax);
            var fp = CollectPlanes(mu, p.Fastener.Comp);
            double best = double.MaxValue;
            foreach (var f in fp) { if (!PerpAxis(f.N, ax)) continue; double d = Math.Abs(Ax(f.P, O, ax) - tSeat); if (d < best) best = d; }
            return best == double.MaxValue ? -1 : best * 1000.0;
        }

        // Is the flange face the annulus around THIS bolt's hole (not a far-away flat)? The hole axis crosses the
        // flange plane at ~the hole center — inside the hole VOID — so GetClosestPointOn returns the inner rim, ~a hole
        // radius from the axis. Accept anything within ~2.5× hole radius (hole + head), admitting the seating annulus
        // and rejecting distant faces. CRITICAL: GetClosestPointOn works in the face's PART-LOCAL coords, so the
        // assembly-space crossing point must be transformed into the component frame first (distance is rigid-invariant).
        private static bool AxisHitsFace(MathUtility mu, Pair p, Plane flange, out double dist)
        {
            dist = -1;
            try
            {
                double[] ax = p.Hole.D, o = p.Hole.O, gp = flange.P, gn = flange.N;
                double denom = Dot(ax, gn);
                if (Math.Abs(denom) < 1e-6) return false;
                double t = ((gp[0] - o[0]) * gn[0] + (gp[1] - o[1]) * gn[1] + (gp[2] - o[2]) * gn[2]) / denom;
                double[] cAsm = { o[0] + t * ax[0], o[1] + t * ax[1], o[2] + t * ax[2] };   // crossing point, ASSEMBLY coords

                if (flange.Comp == null) return true;
                var inv = (MathTransform)flange.Comp.Transform2.Inverse();                  // assembly → part-local
                var cpt = (MathPoint)((MathPoint)mu.CreatePoint(cAsm)).MultiplyTransform(inv);
                double[] cl = cpt.ArrayData as double[];
                double[] near = flange.Face.GetClosestPointOn(cl[0], cl[1], cl[2]) as double[];
                if (near == null || near.Length < 3) return true;
                double dx = near[0] - cl[0], dy = near[1] - cl[1], dz = near[2] - cl[2];
                dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                return dist >= 0.7 * p.Hole.R && dist <= 2.0 * p.Hole.R;                     // the seating annulus, not a far/oversize face
            }
            catch { return true; }
        }

        // Planar faces from every non-fastener component, assembly coords (live handles until the next rebuild).
        private static List<Plane> CollectFlangePlanes(MathUtility mu, List<Component2> flangeComps)
        {
            var list = new List<Plane>();
            foreach (var c in flangeComps) list.AddRange(CollectPlanes(mu, c));
            return list;
        }

        private static string ShortName(Pair p)
        { string n = p.Fastener.Comp.Name2 ?? "?"; return n.Length > 12 ? n.Substring(0, 12) : n; }

        private static string FaceInfo(Plane f)
        {
            string cn = "?"; try { cn = f.Comp != null ? f.Comp.Name2 : "?"; } catch { }
            if (cn != null && cn.Length > 10) cn = cn.Substring(0, 10);
            return "flange[" + cn + " n=(" + f.N[0].ToString("F2") + "," + f.N[1].ToString("F2") + "," + f.N[2].ToString("F2") + ")]";
        }

        // Build a face-to-face mate. Returns AddMate5's code; the mate object is captured even on failure so an
        // over-defining mate can be deleted. Success = swAddMateError_NoError (= 1). Also reports if selection held.
        private static int AddMateFaces(IModelDoc2 model, AssemblyDoc asm, int mateType, int align, Face2 a, Face2 b, out Mate2 mate, out bool held)
        {
            mate = null; held = false;
            if (a == null || b == null) return -100;
            model.ClearSelection2(true);
            var sd = ((SelectionMgr)model.SelectionManager).CreateSelectData();
            sd.Mark = 1;
            bool s1 = ((Entity)a).Select4(false, sd);
            bool s2 = ((Entity)b).Select4(true, sd);
            held = s1 && s2;
            if (!held) { model.ClearSelection2(true); return -101; }
            int err;
            mate = asm.AddMate5(mateType, align, false, 0, 0, 0, 0, 0, 0, 0, 0, false, false, 0, out err);
            model.ClearSelection2(true);
            return err;
        }

        // Seat a bolt: coincident head→flange, trying the nearest VALIDATED candidates in turn. A candidate is used
        // only if the flange face is on the bolt axis and the selection holds. code=1 (NoError) => kept. code=5
        // (OverDefinedAssembly) => delete it and stop (this bolt would over-define). Logs faces + codes.
        private static Mate2 SeatV2(IModelDoc2 model, AssemblyDoc asm, MathUtility mu, Pair p, List<Plane> flange, int seatAlign, out string log, out SeatFit chosen)
        {
            chosen = null;
            string diag;
            var cands = FindSeatCandidates(mu, p, flange, out diag);
            if (cands.Count == 0) { log = "no candidate [" + diag + "]"; return null; }
            double bearingRout = ComputeBoltGeom(mu, p).BearingRout;   // fastener across-flats radius

            int tried = 0, axisFail = 0, selFail = 0, narrowFail = 0, lastCode = 0;
            var dLog = new List<string>();                     // post-transform axis→face distance per candidate (mm)
            foreach (var fit in cands)
            {
                if (tried >= 3) break;                         // only the 3 nearest are worth trying
                double dist;
                bool hit = AxisHitsFace(mu, p, fit.Flange, out dist);
                // the fastener must be WIDER than the hole at this face — else it passes through and "gap=0 inside the
                // hole" is nonsense (a nut narrower than the bore). dist ≈ the face's inner hole radius.
                bool wide = dist >= 0 && dist < bearingRout - 3e-4;
                if (dLog.Count < 5) dLog.Add("d=" + (dist < 0 ? "?" : (dist * 1000).ToString("F1")) + (hit ? "" : "x") + (wide ? "" : "N"));
                if (!hit) { axisFail++; continue; }            // flange face isn't on the hole axis
                if (!wide) { narrowFail++; continue; }         // fastener would pass through this hole — not a bearing face
                tried++;
                Mate2 m; bool held;
                int code = AddMateFaces(model, asm, (int)swMateType_e.swMateCOINCIDENT, seatAlign, fit.Head.Face, fit.Flange.Face, out m, out held);
                if (!held) { selFail++; if (m != null) DeleteMate(model, m); lastCode = code; continue; }
                if (code == MATE_OK && m != null)
                { chosen = fit; log = "seat align=" + AlignName(seatAlign) + " [" + diag + " " + string.Join(",", dLog) + "] " + FaceInfo(fit.Flange); return m; }
                if (code == MATE_OVERDEF)
                { if (m != null) DeleteMate(model, m); log = "OVERDEFINE(5) align=" + AlignName(seatAlign) + " tried " + FaceInfo(fit.Flange) + " [" + diag + "]"; return null; }
                if (m != null) DeleteMate(model, m);           // other reject → clean up, try the next candidate
                lastCode = code;
            }
            log = "no valid seat [" + diag + " rout=" + (bearingRout * 1000).ToString("F1") + " " + string.Join(",", dLog) + " tried=" + tried + " axisFail=" + axisFail + " narrowFail=" + narrowFail + " selFail=" + selFail + " lastCode=" + lastCode + "]";
            return null;
        }

        public static async Task<MateResult> Run(ISldWorks app, IModelDoc2 model, Func<string, string, string, string, Task> emit)
        {
            var res = new MateResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to mate fasteners."; return res; }
            var mu = (MathUtility)app.GetMathUtility();

            // ---- Gauge: find every fastener sitting coaxially in a hole ----
            await emit("Gauge", "reading the assembly", "run", null);
            var fasteners = new List<Cyl>();
            var holes = new List<Cyl>();
            object[] comps = asm.GetComponents(false) as object[];
            if (comps != null)
            {
                foreach (var o in comps)
                {
                    var comp = o as Component2;
                    if (comp == null || comp.IsSuppressed()) continue;
                    CollectCylinders(mu, comp, LooksLikeFastener(comp.Name2) ? fasteners : holes);
                }
            }
            var pairDiag = new List<string>();
            var allPairs = Pairing(fasteners, holes, pairDiag);
            res.Detected = allPairs.Count;
            if (pairDiag.Count > 0) await emit("Gauge", null, "done", "pairing · " + string.Join("  |  ", pairDiag));

            // Flange faces = planar faces from EVERY non-fastener part. The seat searches these (not just the part the
            // shank paired with), so a bolt head seats on the flange even when its shank paired with a nut. Collected
            // once here; re-collected after each rebuild (Face2 handles go stale, coords stay valid for stationary parts).
            var flangeComps = new List<Component2>();
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2;
                if (c == null || c.IsSuppressed()) continue;
                if (!LooksLikeFastener(c.Name2)) flangeComps.Add(c);
            }
            var flangePlanes = CollectFlangePlanes(mu, flangeComps);

            // Classify each coaxial fastener. PATTERN INSTANCES are never mated — the pattern drives them from the seed,
            // and mating an instance over-defines the assembly. A non-instance is "already seated" only on a real, small,
            // MEASURED head→flange gap (0..0.5mm); a large or unmeasurable gap → loose (mate it). Debug line per fastener
            // shows gap + pattern role so misclassification is visible.
            var forgeMated = ForgeMatedComps(model);   // idempotency: components Forge already mated on a prior run
            await emit("Gauge", null, "done", "forge-tags: " + (forgeMated.Count == 0 ? "NONE" : string.Join(",", forgeMated)));
            var seatedBoltNames = new HashSet<string>();
            var pairs = new List<Pair>();
            var skippedInstancePairs = new List<Pair>();   // pattern-driven bolts NOT mated this run — re-measured post-mate so the final report never overclaims
            int seated = 0, instances = 0, alreadyForge = 0;
            var dbg = new List<string>();
            foreach (var p in allPairs)
            {
                bool isInstance = false;
                try { isInstance = p.Fastener.Comp.IsPatternInstance(); } catch { }
                string cn = p.Fastener.Comp.Name2;
                bool forgeDone = cn != null && forgeMated.Contains(cn);
                double g = SeatGap(mu, p, flangePlanes);
                bool isSeated = (g >= 0 && g < 0.5);

                string role;
                if (isInstance) { instances++; role = "instance→pattern-driven, SKIP"; skippedInstancePairs.Add(p); }
                else if (forgeDone) { alreadyForge++; role = "already assembled by Forge, SKIP"; if (cn != null) seatedBoltNames.Add(cn); }
                else if (isSeated) { seated++; role = "seed/std SEATED"; if (cn != null) seatedBoltNames.Add(cn); }
                else { pairs.Add(p); role = "seed/std loose"; }

                if (dbg.Count < 6)
                {
                    string nm = cn ?? "?"; if (nm.Length > 14) nm = nm.Substring(0, 14);
                    dbg.Add(nm + " gap=" + (g < 0 ? "n/a" : g.ToString("F1")) + " pattern=" + role);
                }
            }

            await emit("Gauge", null, "done",
                "found " + allPairs.Count + " fastener" + (allPairs.Count == 1 ? "" : "s") +
                " · " + pairs.Count + " loose, " + (seated + alreadyForge) + " seated" +
                (instances > 0 ? ", " + instances + " pattern-driven (skipped)" : ""));
            if (dbg.Count > 0) await emit("Gauge", null, "done", string.Join("  |  ", dbg));
            if (pairs.Count == 0)
            {
                // no loose bolts — but nuts may still need seating (e.g. a re-run where bolts are already done)
                int[] nrx = await RunNutPass(model, asm, mu, comps, flangeComps, seatedBoltNames, emit);
                if (nrx[0] > 0) { res.Mated = nrx[0]; res.Seated = nrx[0]; res.RebuildClean = true; return res; }
                res.Error = instances > 0 && (seated + alreadyForge) == 0
                    ? (instances + " fasteners are pattern instances — mate/fix the pattern seed, not the copies.")
                    : ((seated + alreadyForge) > 0 ? ((seated + alreadyForge) + " bolts already assembled; no nuts to seat." ) : "No loose fasteners to mate.");
                return res;
            }

            // ---- Torque: mate ONLY seeds/standalones (instances were excluded in Gauge). Per bolt: concentric + seat,
            // rebuild, then a POSITION-based orientation check — if the bolt flipped (head buried, threads up), delete
            // BOTH mates, flip the concentric alignment, re-add, and rebuild once more. Sentinel only ever sees the final
            // state. code=5 (OverDefinedAssembly) mates are deleted on the spot. flip=yes/no is logged per bolt. ----
            await emit("Torque", "mating and seating the fasteners", "run", null);
            int before = SafeWrong(model);
            var attempts = new List<Attempt>();
            int seatAdded = 0, overDefAdd = 0, flips = 0;
            var seatLog = new List<string>();
            var seqLog = new List<string>();   // INSTRUMENTATION: exact add sequence per bolt (which add returns code 5)
            int dcount = 0;                     // how many bolts we've emitted detailed per-add lines for
            foreach (var p0 in pairs)
            {
                bool detail = dcount < 2; if (detail) dcount++;   // emit detailed per-add lines for the first 2 bolts
                var added = new List<Mate2>();                    // EVERY mate created for this bolt — rollback deletes them all
                Mate2 keptConc = null, keptSeat = null; SeatFit keptFit = null;
                bool ok = false, flipped = false;
                Pair p = p0;
                try
                {
                    p = ReFetch(mu, asm, p0);   // fresh geometry (previous bolts' rebuilds staled the Gauge-era faces)

                    if (detail)
                    { int st = 99; bool fx = false;
                      try { st = p.Fastener.Comp.GetConstrainedStatus(); } catch { }
                      try { fx = p.Fastener.Comp.IsFixed(); } catch { }
                      await emit(null, null, "done", "▸ " + ShortName(p) + " PRE[" + StatusName(st) + " fixed=" + fx +
                          " boltcyl@" + Shorten(p.Fastener.Comp != null ? p.Fastener.Comp.Name2 : "?") +
                          " hole=" + Shorten(p.Hole.Comp != null ? p.Hole.Comp.Name2 : "?") + ".cyl r=" + (p.Hole.R * 1000).ToString("F1") + "]"); }

                    // 1) Concentric — axis alignment ONLY. ALIGNED/ANTI is meaningless for cylinder↔cylinder, so use CLOSEST.
                    int cErr;
                    Mate2 mc = Concentric(model, asm, p, (int)swMateAlign_e.swMateAlignCLOSEST, out cErr);
                    if (detail) await emit(null, null, "done", "▸ conc CLOSEST code=" + cErr + (cErr == MATE_OVERDEF ? " OVERDEFINE" : ""));
                    if (mc == null || cErr != MATE_OK)
                    {
                        if (mc != null) DeleteMate(model, mc);
                        if (cErr == MATE_OVERDEF) overDefAdd++;
                        res.Failed++;
                        if (detail) await emit(null, null, "done", "▸ SKIPPED (conc code=" + cErr + ")");
                    }
                    else
                    {
                        added.Add(mc);
                        // 2) Seat — the COINCIDENT'S alignment controls the bolt's end-for-end orientation. Try ANTI, then
                        //    ALIGNED (max one flip). Fresh flange each attempt; rebuild before the orient check reads the pose.
                        int[] seatAligns = { (int)swMateAlign_e.swMateAlignANTI_ALIGNED, (int)swMateAlign_e.swMateAlignALIGNED };
                        for (int ai = 0; ai < seatAligns.Length; ai++)
                        {
                            int sAlign = seatAligns[ai];
                            var flangeSeat = CollectFlangePlanes(mu, flangeComps);
                            string slog; SeatFit fit;
                            Mate2 ms = SeatV2(model, asm, mu, p, flangeSeat, sAlign, out slog, out fit);
                            if (detail) await emit(null, null, "done", "▸ seat[" + AlignName(sAlign) + "] " + (ms != null ? "OK" : "fail") + ": " + slog);
                            if (ms == null) continue;                       // this alignment couldn't seat / over-defined → try the other
                            added.Add(ms);
                            model.ForceRebuild3(false);                     // rebuild so the orient check reads the SOLVED pose
                            var flangeChk = CollectFlangePlanes(mu, flangeComps);
                            string odbg = "";
                            ok = fit != null && OrientedCorrectly(mu, p, fit.Flange, flangeChk, out odbg);
                            if (detail) await emit(null, null, "done", "▸ orient[" + AlignName(sAlign) + "]: " + odbg + " → " + (ok ? "OK, keep" : "wrong side, flip seat"));
                            if (ok) { keptConc = mc; keptSeat = ms; keptFit = fit; break; }
                            DeleteMate(model, ms); added.Remove(ms); model.ForceRebuild3(false);   // wrong side → drop this seat, try other alignment
                            if (ai == 0) flipped = true;
                        }

                        if (ok && keptConc != null)
                        {
                            if (flipped) flips++;
                            if (keptSeat != null) seatAdded++;
                            res.Mated++;
                            try { ((Feature)keptConc).Name = "Forge-Conc-" + (p.Fastener.Comp.Name2 ?? "bolt"); } catch { }
                            if (keptSeat != null) { try { ((Feature)keptSeat).Name = "Forge-Seat-" + (p.Fastener.Comp.Name2 ?? "bolt"); } catch { } }
                            attempts.Add(new Attempt { P = p, Concentric = keptConc, Seat = keptSeat, Comp = p.Fastener.Comp, Fit = keptFit });
                            if (detail) await emit(null, null, "done", "▸ KEPT" + (flipped ? " (after seat flip)" : ""));
                        }
                        else
                        {
                            for (int i = added.Count - 1; i >= 0; i--) DeleteMate(model, added[i]);   // roll back EVERY add — no stray mate
                            added.Clear();
                            model.ForceRebuild3(false);
                            res.Failed++;
                            if (detail) await emit(null, null, "done", "▸ rollback: deleted all adds → SKIPPED");
                        }
                    }
                }
                catch (COMException ex)
                { res.Failed++; for (int i = added.Count - 1; i >= 0; i--) DeleteMate(model, added[i]); try { model.ForceRebuild3(false); } catch { } if (detail) { try { await emit(null, null, "done", "▸ EXCEPTION(COM 0x" + ((uint)ex.ErrorCode).ToString("X8") + ") → rolled back"); } catch { } } }
                catch (Exception ex)
                { res.Failed++; for (int i = added.Count - 1; i >= 0; i--) DeleteMate(model, added[i]); try { model.ForceRebuild3(false); } catch { } if (detail) { try { await emit(null, null, "done", "▸ EXCEPTION(" + ex.GetType().Name + ") → rolled back"); } catch { } } }
                try { if (p0.Fastener != null && p0.Fastener.Face != null) Marshal.ReleaseComObject(p0.Fastener.Face); } catch { }
                try { if (p0.Hole != null && p0.Hole.Face != null) Marshal.ReleaseComObject(p0.Hole.Face); } catch { }
            }

            await emit("Torque", null, "done",
                res.Mated + " concentric, " + seatAdded + " seated" +
                (flips > 0 ? ", " + flips + " flipped→corrected" : "") +
                (res.Failed > 0 ? ", " + res.Failed + " skipped" + (overDefAdd > 0 ? " (" + overDefAdd + " already constrained)" : "") : ""));
            foreach (var s in seqLog) await emit("Torque", null, "done", "seq · " + s);
            if (seatLog.Count > 0) await emit("Torque", null, "done", string.Join("  |  ", seatLog));
            if (res.Mated == 0)
            {
                // no bolts mated this run (e.g. all already assembled) — still seat the nuts
                foreach (var a in attempts) if (a.Comp != null && a.Comp.Name2 != null) seatedBoltNames.Add(a.Comp.Name2);
                int[] nr0 = await RunNutPass(model, asm, mu, comps, flangeComps, seatedBoltNames, emit);
                if (nr0[0] > 0) { res.Mated = nr0[0]; res.Seated = nr0[0]; res.RebuildClean = SafeWrong(model) <= before; return res; }
                res.Error = "Couldn't mate these fasteners without over-defining.";
                return res;
            }

            // ---- Guard: whole-assembly over-define check with rollback. AddMate flags most over-defines at add-time
            // (code 5); this catches any EMERGENT over-define via each mated component's GetConstrainedStatus, and rolls
            // back only the offending bolts' mates (partial), then rebuilds. Not GetWhatsWrongCount — the real DOF status. ----
            await emit("Sentinel", "checking the assembly isn't over-defined", "run", null);
            var rolledBack = new List<string>();
            foreach (var a in attempts.ToArray())
            {
                int st = (int)swConstrainedStatus_e.swUnderConstrained;   // default = safe (a mated-but-free bolt is fine)
                try { st = a.Comp.GetConstrainedStatus(); } catch { }
                bool over = st == (int)swConstrainedStatus_e.swOverConstrained
                         || st == (int)swConstrainedStatus_e.swNoSolution
                         || st == (int)swConstrainedStatus_e.swInvalidSolution;
                if (over)
                {
                    if (a.Seat != null) { DeleteMate(model, a.Seat); seatAdded--; }
                    if (a.Concentric != null) DeleteMate(model, a.Concentric);
                    attempts.Remove(a);
                    res.Mated--;
                    rolledBack.Add(ShortName(a.P));
                }
            }
            if (rolledBack.Count > 0)
            {
                model.ForceRebuild3(false);
                await emit("Sentinel", null, "done",
                    "rolled back " + rolledBack.Count + " mate-set" + (rolledBack.Count == 1 ? "" : "s") +
                    " that would over-define: " + string.Join(", ", rolledBack));
            }
            else await emit("Sentinel", null, "done", "no over-define — the assembly solves cleanly");

            if (res.Mated == 0)
            { res.RebuildClean = SafeWrong(model) <= before; res.Error = "Every candidate mate would over-define — rolled all back, nothing applied."; return res; }

            // ---- Sentinel: measure the ACTUAL mated pair per bolt — head→seat distance along the hole axis AND the
            // position-based orientation. Flush only if the mated gap is < 0.5mm AND the bolt isn't flipped. ----
            await emit("Sentinel", "checking each mate seated flush", "run", null);
            var flangeFresh = CollectFlangePlanes(mu, flangeComps);
            var proud = new List<Attempt>();
            var sentLog = new List<string>();
            foreach (var a in attempts)
            {
                double d = a.Fit != null ? MatedGapMm(mu, a.P, a.Fit.Flange) : -1;
                string odbg = "";
                bool oriented = a.Fit != null && OrientedCorrectly(mu, a.P, a.Fit.Flange, flangeFresh, out odbg);
                bool flush = a.Fit != null && d >= 0 && d < 0.5 && oriented;
                if (flush) res.Flush++; else proud.Add(a);
                if (sentLog.Count < 4)
                    sentLog.Add(ShortName(a.P) + " seatGap=" + (d < 0 ? "?" : d.ToString("F1")) + "mm " +
                        (flush ? "OK" : (!oriented ? "FLIP" : "PROUD")));
            }
            if (sentLog.Count > 0) await emit("Sentinel", null, "done", string.Join("  |  ", sentLog));

            // ---- Mender: re-seat the proud ones with fresh geometry, then ONE rebuild, then re-measure ----
            if (proud.Count > 0)
            {
                await emit("Mender", "re-seating " + proud.Count + " that didn't sit flush", "run", null);
                bool any = false;
                foreach (var a in proud)
                {
                    try
                    {
                        if (a.Seat != null) { DeleteMate(model, a.Seat); a.Seat = null; }
                        string slog; SeatFit rf;
                        Mate2 m = SeatV2(model, asm, mu, a.P, flangeFresh, (int)swMateAlign_e.swMateAlignANTI_ALIGNED, out slog, out rf);
                        if (m != null) { a.Seat = m; a.Fit = rf; any = true; }   // track the re-seat mate so rollback can reach it
                    }
                    catch { }
                }
                if (any) model.ForceRebuild3(false);
                var flangeFresh2 = CollectFlangePlanes(mu, flangeComps);
                foreach (var a in proud)
                {
                    double d = a.Fit != null ? MatedGapMm(mu, a.P, a.Fit.Flange) : -1;
                    string odbg;
                    bool oriented = a.Fit != null && OrientedCorrectly(mu, a.P, a.Fit.Flange, flangeFresh2, out odbg);
                    if (a.Fit != null && d >= 0 && d < 0.5 && oriented) res.Fixed++;
                }
                await emit("Mender", null, "done", res.Fixed + " of " + proud.Count + " re-seated");
            }

            // ===================== NUT PASS (always runs) =====================
            foreach (var a in attempts) if (a.Comp != null && a.Comp.Name2 != null) seatedBoltNames.Add(a.Comp.Name2);
            int[] nr = await RunNutPass(model, asm, mu, comps, flangeComps, seatedBoltNames, emit);
            int nutSeated = nr[0];
            int nutPatternTotal = nr[2], nutPatternFollowed = nr[3];

            // ---- Final Sentinel: verify EVERY Forge-mated fastener (bolts + nuts), not just the one touched this run —
            // a relative mate can move a previously-seated fastener, so re-measure each bearing→nearest-flange gap. ----
            int movedCount = await VerifyAll(model, mu, comps, flangeComps, emit);

            res.Proud = proud.Count - res.Fixed + movedCount;
            res.Seated = res.Flush + res.Fixed + nutSeated;
            res.RebuildClean = SafeWrong(model) <= before && movedCount == 0;

            // test-loop false-success fix (the regression corpus): "mate all the bolts" was
            // reporting "all N bolts seated" where N was only the bolts THIS RUN attempted — pattern-driven bolts
            // skipped by design (mating an instance over-defines the assembly) were silently excluded from that "all".
            // Re-measure every skipped instance's actual seat gap now that the seed has moved, fresh flange planes —
            // if the component pattern re-derived them into place they genuinely ARE done; if not, say so.
            res.PatternInstancesTotal = skippedInstancePairs.Count;
            if (skippedInstancePairs.Count > 0)
            {
                var flangeForInstances = CollectFlangePlanes(mu, flangeComps);
                foreach (var sp in skippedInstancePairs)
                {
                    double gi = SeatGap(mu, sp, flangeForInstances);
                    if (gi >= 0 && gi < 0.5) res.PatternInstancesFollowed++;
                }
            }
            int totalBolts = res.Detected;
            int flushBolts = res.Flush + res.Fixed + res.PatternInstancesFollowed;
            bool trulyAllBolts = flushBolts >= totalBolts && res.Proud == 0 && movedCount == 0;
            int totalNuts = nutSeated + nutPatternTotal;
            int flushNuts = nutSeated + nutPatternFollowed;
            bool trulyAllNuts = flushNuts >= totalNuts;
            string boltMsg = trulyAllBolts
                ? "all " + totalBolts + " bolts seated flush" + (res.PatternInstancesFollowed > 0 ? " (" + res.PatternInstancesFollowed + " pattern-driven followed the seed automatically)" : "")
                : flushBolts + " of " + totalBolts + " total bolts seated flush" +
                  (res.PatternInstancesTotal > 0 ? " (" + (res.PatternInstancesTotal - res.PatternInstancesFollowed) + " pattern-driven still NOT seated — mate/move the pattern's seed, not the copies)" : "") +
                  (res.Proud > 0 || movedCount > 0 ? "; " + movedCount + " fastener(s) moved/not flush" : "");
            string nutMsg = totalNuts == 0 ? "" :
                (trulyAllNuts
                    ? " + all " + totalNuts + " nuts seated" + (nutPatternFollowed > 0 ? " (" + nutPatternFollowed + " pattern-driven followed)" : "")
                    : " + " + flushNuts + " of " + totalNuts + " total nuts seated (" + (nutPatternTotal - nutPatternFollowed) + " pattern-driven still NOT seated)");
            await emit("Sentinel", null, "done", boltMsg + nutMsg + ((trulyAllBolts && trulyAllNuts) ? ", rebuild clean" : " — review"));
            return res;
        }

        // Seat every seed/standalone nut: pair its bore to the coaxial bolt shank, concentric (nuts are symmetric —
        // alignment doesn't matter), then seat its bearing face on the nearest flange face. Reuses the bolt seat/verify
        // infra; rollback on over-define; skips pattern instances. Tags its mates "Forge-*" for idempotent re-runs.
        // Returns {seated, skipped}.
        private static async Task<int[]> RunNutPass(IModelDoc2 model, AssemblyDoc asm, MathUtility mu, object[] comps,
            List<Component2> flangeComps, HashSet<string> seatedBolts, Func<string, string, string, string, Task> emit)
        {
            int nutSeated = 0, nutSkipped = 0, nutAlready = 0;
            var nutLog = new List<string>();
            var forgeMated = ForgeMatedComps(model);   // idempotency: nuts Forge already assembled (both mates present)
            // re-collect cylinders FRESH — the bolt pass rebuilt several times, so any earlier faces are stale
            var fFast = new List<Cyl>(); var fHoles = new List<Cyl>();
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2;
                if (c == null || c.IsSuppressed()) continue;
                CollectCylinders(mu, c, LooksLikeFastener(c.Name2) ? fFast : fHoles);
            }
            var nutPairs = new List<Pair>();
            var nutInstancePairs = new List<Pair>();   // pattern-driven nuts, never mated directly — re-measured post-mate for an honest report
            var nutSeen = new HashSet<string>();
            foreach (var nb in fHoles)
            {
                if (nb.Comp == null || !LooksLikeNut(nb.Comp.Name2)) continue;
                bool inst = false; try { inst = nb.Comp.IsPatternInstance(); } catch { }
                if (inst)
                {
                    Cyl instShank = null; double instDR = double.MaxValue;
                    foreach (var fs in fFast)
                    {
                        if (fs.Comp == null || !Coaxial(nb, fs)) continue;
                        double dr = Math.Abs(nb.R - fs.R);
                        if (dr <= 0.20 * nb.R && dr < instDR) { instDR = dr; instShank = fs; }
                    }
                    if (instShank != null) nutInstancePairs.Add(new Pair { Fastener = nb, Hole = instShank });
                    continue;                                          // pattern instances driven by the seed nut — never mated directly
                }
                string key = nb.Comp.Name2 ?? "?";
                if (nutSeen.Contains(key)) continue;
                if (forgeMated.Contains(key)) { nutSeen.Add(key); nutAlready++; continue; }   // already assembled by Forge — no re-mate
                Cyl bestShank = null; double bestDR = double.MaxValue;
                foreach (var fs in fFast)                             // ONLY bolt (fastener) cylinders — never flange holes
                {
                    if (fs.Comp == null) continue;
                    if (seatedBolts != null && !seatedBolts.Contains(fs.Comp.Name2)) continue;   // paired bolt must be SEATED (defined stack side)
                    if (!Coaxial(nb, fs)) continue;
                    double dr = Math.Abs(nb.R - fs.R);
                    if (dr <= 0.20 * nb.R && dr < bestDR) { bestDR = dr; bestShank = fs; }        // nut bore ≈ shank radius
                }
                if (bestShank != null) { nutSeen.Add(key); nutPairs.Add(new Pair { Fastener = nb, Hole = bestShank }); }
            }
            int MeasureNutPatternFollowed()
            {
                if (nutInstancePairs.Count == 0) return 0;
                var flangeForNuts = CollectFlangePlanes(mu, flangeComps);
                int followed = 0;
                foreach (var np2 in nutInstancePairs) { double g2 = SeatGap(mu, np2, flangeForNuts); if (g2 >= 0 && g2 < 0.5) followed++; }
                return followed;
            }

            if (nutPairs.Count == 0)
            {
                if (nutAlready > 0) await emit("Torque", null, "done", nutAlready + " nut" + (nutAlready == 1 ? "" : "s") + " already assembled by Forge — nothing to do");
                return new[] { 0, 0, nutInstancePairs.Count, MeasureNutPatternFollowed() };
            }

            await emit("Torque", "seating the nuts", "run", null);
            int ndetail = 0;
            foreach (var np in nutPairs)
            {
                bool ndet = ndetail < 1; if (ndet) ndetail++;
                var added = new List<Mate2>();
                string ent = Shorten(np.Fastener.Comp != null ? np.Fastener.Comp.Name2 : "?") + ".bore↔" + Shorten(np.Hole.Comp != null ? np.Hole.Comp.Name2 : "?") + ".shank";
                bool okNut = false; double gap = -1; bool over = false;
                try
                {
                    if (ndet) await emit(null, null, "done", "▸ nut " + ShortName(np) + " conc[" + ent + "]");
                    int cErr;
                    Mate2 mc = Concentric(model, asm, np, (int)swMateAlign_e.swMateAlignCLOSEST, out cErr);
                    if (ndet) await emit(null, null, "done", "▸ nut conc code=" + cErr);
                    if (mc == null || cErr != MATE_OK)
                    { if (mc != null) DeleteMate(model, mc); nutSkipped++; if (nutLog.Count < 4) nutLog.Add(ShortName(np) + " conc code=" + cErr + " skip"); continue; }
                    added.Add(mc);

                    // seat — try both coincident alignments; keep the one that sits flush (gap < 0.5) and doesn't over-define
                    int[] seatAligns = { (int)swMateAlign_e.swMateAlignANTI_ALIGNED, (int)swMateAlign_e.swMateAlignALIGNED };
                    SeatFit keptFit = null; Mate2 keptSeat = null;
                    for (int ai = 0; ai < seatAligns.Length; ai++)
                    {
                        var flangeN = CollectFlangePlanes(mu, flangeComps);
                        string slog; SeatFit fit;
                        Mate2 ms = SeatV2(model, asm, mu, np, flangeN, seatAligns[ai], out slog, out fit);
                        if (ndet) await emit(null, null, "done", "▸ nut seat[" + AlignName(seatAligns[ai]) + "] " + (ms != null ? "OK" : "fail") + ": " + slog);
                        if (ms == null) continue;
                        added.Add(ms);
                        model.ForceRebuild3(false);
                        gap = fit != null ? MatedGapMm(mu, np, fit.Flange) : -1;
                        int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = np.Fastener.Comp.GetConstrainedStatus(); } catch { }
                        over = st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution;
                        if (ndet) await emit(null, null, "done", "▸ nut check[" + AlignName(seatAligns[ai]) + "] gap=" + (gap < 0 ? "?" : gap.ToString("F1")) + "mm" + (over ? " OVER" : "") + (gap >= 0 && gap < 0.5 && !over ? " → flush" : " → no"));
                        if (gap >= 0 && gap < 0.5 && !over) { keptFit = fit; keptSeat = ms; okNut = true; break; }
                        DeleteMate(model, ms); added.Remove(ms); model.ForceRebuild3(false);
                    }

                    if (okNut)
                    {
                        try { ((Feature)mc).Name = "Forge-Conc-" + (np.Fastener.Comp.Name2 ?? "nut"); } catch { }
                        if (keptSeat != null) { try { ((Feature)keptSeat).Name = "Forge-Seat-" + (np.Fastener.Comp.Name2 ?? "nut"); } catch { } }
                        nutSeated++;
                        if (ndet) await emit(null, null, "done", "▸ nut KEPT gap=" + gap.ToString("F1") + "mm");
                    }
                    else
                    {
                        for (int i = added.Count - 1; i >= 0; i--) DeleteMate(model, added[i]);
                        model.ForceRebuild3(false);
                        nutSkipped++;
                        if (ndet) await emit(null, null, "done", "▸ nut rollback: deleted all adds → SKIPPED");
                    }
                    if (nutLog.Count < 4) nutLog.Add(ShortName(np) + " gap=" + (gap < 0 ? "?" : gap.ToString("F1")) + "mm" + (over ? " OVER" : "") + (okNut ? " → OK" : " → rolled back"));
                }
                catch { nutSkipped++; for (int i = added.Count - 1; i >= 0; i--) DeleteMate(model, added[i]); try { model.ForceRebuild3(false); } catch { } }
            }
            await emit("Torque", null, "done", nutSeated + " nut" + (nutSeated == 1 ? "" : "s") + " seated" + (nutSkipped > 0 ? ", " + nutSkipped + " skipped" : "") + (nutAlready > 0 ? ", " + nutAlready + " already assembled" : ""));
            if (nutLog.Count > 0) await emit("Torque", null, "done", string.Join("  |  ", nutLog));
            return new[] { nutSeated, nutSkipped, nutInstancePairs.Count, MeasureNutPatternFollowed() };
        }

        // Walk the feature tree for Forge-tagged mates ("Forge-Conc-<comp>" / "Forge-Seat-<comp>") and return the set
        // of component names Forge has already mated — so a re-run recognizes its own work and treats those as seated.
        // A fastener counts as Forge-assembled ONLY if BOTH its mates (Forge-Conc-* AND Forge-Seat-*) exist — a lone
        // concentric leaves the part free to slide, so a partial tag must NOT be treated as assembled (re-mate instead).
        private static HashSet<string> ForgeMatedComps(IModelDoc2 model)
        {
            var conc = new HashSet<string>(); var seat = new HashSet<string>();
            try
            {
                var feat = model.FirstFeature() as Feature;
                while (feat != null)
                {
                    ForgeName(feat, conc, seat);
                    var sub = feat.GetFirstSubFeature() as Feature;
                    while (sub != null) { ForgeName(sub, conc, seat); sub = sub.GetNextSubFeature() as Feature; }
                    feat = feat.GetNextFeature() as Feature;
                }
            }
            catch { }
            conc.IntersectWith(seat);   // require BOTH
            return conc;
        }
        private static void ForgeName(Feature f, HashSet<string> conc, HashSet<string> seat)
        {
            try
            {
                string nm = f.Name;
                if (nm == null || !nm.StartsWith("Forge-")) return;
                var parts = nm.Split(new[] { '-' }, 3);   // Forge - Conc/Seat - <comp (may contain dashes)>
                if (parts.Length != 3) return;
                if (parts[1] == "Conc") conc.Add(parts[2]);
                else if (parts[1] == "Seat") seat.Add(parts[2]);
            }
            catch { }
        }

        // Re-verify EVERY Forge-mated fastener's bearing→nearest-flange gap (bolts and nuts). Returns how many MOVED
        // (gap ≥ 0.5mm) — a relative mate can shift a previously-seated part, and Sentinel must catch that, not just
        // check the one fastener it touched this run.
        private static async Task<int> VerifyAll(IModelDoc2 model, MathUtility mu, object[] comps,
            List<Component2> flangeComps, Func<string, string, string, string, Task> emit)
        {
            var mated = ForgeMatedComps(model);
            if (mated.Count == 0) return 0;
            await emit("Sentinel", "verifying every Forge-mated fastener", "run", null);
            var fFast = new List<Cyl>(); var fHoles = new List<Cyl>();
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2;
                if (c == null || c.IsSuppressed()) continue;
                CollectCylinders(mu, c, LooksLikeFastener(c.Name2) ? fFast : fHoles);
            }
            var flangeV = CollectFlangePlanes(mu, flangeComps);
            int moved = 0; var vLog = new List<string>();
            foreach (var name in mated)
            {
                Pair vp = null;
                Cyl fc = null; foreach (var c in fFast) if (c.Comp != null && c.Comp.Name2 == name) { fc = c; break; }
                if (fc != null)   // a bolt — pair its shank to the coaxial hole
                {
                    Cyl h = null; double bg = double.MaxValue;
                    foreach (var hh in fHoles) { if (!Coaxial(fc, hh)) continue; if (hh.R < fc.R - 5e-4) continue; double g = hh.R - fc.R; if (g < bg) { bg = g; h = hh; } }
                    if (h != null) vp = new Pair { Fastener = fc, Hole = h };
                }
                else              // a nut — pair its bore to the coaxial bolt shank
                {
                    Cyl nb = null; foreach (var c in fHoles) if (c.Comp != null && c.Comp.Name2 == name) { nb = c; break; }
                    if (nb != null)
                    {
                        Cyl sh = null; double bd = double.MaxValue;
                        foreach (var fs in fFast) { if (!Coaxial(nb, fs)) continue; double dr = Math.Abs(nb.R - fs.R); if (dr <= 0.20 * nb.R && dr < bd) { bd = dr; sh = fs; } }
                        if (sh != null) vp = new Pair { Fastener = nb, Hole = sh };
                    }
                }
                if (vp == null) continue;
                var geom = ComputeBoltGeom(mu, vp);
                if (geom.Bearing == null) continue;
                double tHead = geom.HeadT, gap = double.MaxValue;
                foreach (var gf in flangeV) { if (!PerpAxis(gf.N, vp.Hole.D)) continue; double tg = Ax(gf.P, vp.Hole.O, vp.Hole.D); double d = Math.Abs(tHead - tg); if (d < gap) gap = d; }
                double gapMm = gap == double.MaxValue ? -1 : gap * 1000;
                bool ok = gapMm >= 0 && gapMm < 0.5;
                if (!ok) moved++;
                if (vLog.Count < 8) vLog.Add(Shorten(name) + " " + (gapMm < 0 ? "?" : gapMm.ToString("F1") + "mm") + (ok ? " OK" : " MOVED"));
            }
            await emit("Sentinel", null, "done",
                (moved == 0 ? "all " + mated.Count + " Forge-mated fasteners still flush" : moved + " MOVED — not flush") + " · " + string.Join("  ", vLog));
            return moved;
        }

        // Collect a component's cylindrical faces, expressed in ASSEMBLY coordinates.
        private static void CollectCylinders(MathUtility mu, Component2 comp, List<Cyl> into)
        {
            try
            {
                var xform = comp.Transform2;
                object bodyInfo;
                object[] bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bodyInfo) as object[];
                if (bodies == null) return;

                foreach (var bo in bodies)
                {
                    var body = bo as Body2;
                    if (body == null) continue;
                    object[] faces = body.GetFaces() as object[];
                    if (faces == null) continue;

                    foreach (var f in faces)
                    {
                        var face = f as Face2;
                        if (face == null) continue;
                        var surf = face.GetSurface() as Surface;
                        if (surf == null || !surf.IsCylinder()) continue;
                        double[] cp = surf.CylinderParams as double[]; // [ox,oy,oz, ax,ay,az, r]
                        if (cp == null || cp.Length < 7) continue;

                        var op = (MathPoint)mu.CreatePoint(new double[] { cp[0], cp[1], cp[2] });
                        op = (MathPoint)op.MultiplyTransform(xform);
                        var dv = (MathVector)mu.CreateVector(new double[] { cp[3], cp[4], cp[5] });
                        dv = (MathVector)dv.MultiplyTransform(xform);

                        double[] oa = op.ArrayData as double[];
                        double[] da = dv.ArrayData as double[];
                        double dl = Math.Sqrt(da[0] * da[0] + da[1] * da[1] + da[2] * da[2]);
                        if (dl < 1e-9) continue;

                        into.Add(new Cyl
                        {
                            Face = face, Comp = comp, R = cp[6],
                            O = new double[] { oa[0], oa[1], oa[2] },
                            D = new double[] { da[0] / dl, da[1] / dl, da[2] / dl }
                        });
                    }
                }
            }
            catch { }
        }

        // Collect a component's planar faces (perpendicular-ish to axis is filtered later), assembly coords.
        private static List<Plane> CollectPlanes(MathUtility mu, Component2 comp)
        {
            var list = new List<Plane>();
            try
            {
                var xform = comp.Transform2;
                object bodyInfo;
                object[] bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bodyInfo) as object[];
                if (bodies == null) return list;

                foreach (var bo in bodies)
                {
                    var body = bo as Body2;
                    if (body == null) continue;
                    object[] faces = body.GetFaces() as object[];
                    if (faces == null) continue;

                    foreach (var f in faces)
                    {
                        var face = f as Face2;
                        if (face == null) continue;
                        var surf = face.GetSurface() as Surface;
                        if (surf == null || !surf.IsPlane()) continue;
                        double[] pp = surf.PlaneParams as double[]; // [nx,ny,nz, px,py,pz]
                        if (pp == null || pp.Length < 6) continue;

                        var nv = (MathVector)mu.CreateVector(new double[] { pp[0], pp[1], pp[2] });
                        nv = (MathVector)nv.MultiplyTransform(xform);
                        var pt = (MathPoint)mu.CreatePoint(new double[] { pp[3], pp[4], pp[5] });
                        pt = (MathPoint)pt.MultiplyTransform(xform);

                        double[] na = nv.ArrayData as double[];
                        double[] pa = pt.ArrayData as double[];
                        double nl = Math.Sqrt(na[0] * na[0] + na[1] * na[1] + na[2] * na[2]);
                        if (nl < 1e-9) continue;

                        double area = 0; try { area = face.GetArea(); } catch { }
                        list.Add(new Plane
                        {
                            Face = face,
                            P = new double[] { pa[0], pa[1], pa[2] },
                            N = new double[] { na[0] / nl, na[1] / nl, na[2] / nl },
                            Area = area,
                            Comp = comp
                        });
                    }
                }
            }
            catch { }
            return list;
        }

        private static string Shorten(string n)
        { if (string.IsNullOrEmpty(n)) return "?"; return n.Length > 10 ? n.Substring(0, 10) : n; }
        private static string Vec(double[] v, double s, string f)
        { return (v[0] * s).ToString(f) + "," + (v[1] * s).ToString(f) + "," + (v[2] * s).ToString(f); }

        // Pair each fastener to its clearance HOLE. Key fixes: (1) ONE pair per fastener COMPONENT — a bolt has several
        // cylinders (shank, thread, chamfer), so we pick its single best shank↔hole match instead of emitting duplicates;
        // (2) radius match — the hole must be the bolt's clearance hole (bolt_r ≤ hole_r ≤ ~1.2× bolt_r), which rejects
        // the hub bore and oversize features. `diag` logs the candidate hole set + chosen hole geometry per bolt.
        private static List<Pair> Pairing(List<Cyl> fasteners, List<Cyl> holes, List<string> diag)
        {
            var byComp = new Dictionary<string, List<Cyl>>();
            foreach (var fc in fasteners)
            {
                string k = fc.Comp != null ? (fc.Comp.Name2 ?? "?") : ("#" + fc.GetHashCode());
                if (!byComp.ContainsKey(k)) byComp[k] = new List<Cyl>();
                byComp[k].Add(fc);
            }

            var pairs = new List<Pair>();
            var used = new HashSet<Face2>();
            foreach (var kv in byComp)
            {
                Cyl bestF = null, bestH = null; double bestGap = double.MaxValue;
                var cand = new List<string>();
                foreach (var fc in kv.Value)
                    foreach (var h in holes)
                    {
                        if (h.Comp != null && LooksLikeNut(h.Comp.Name2)) continue;   // a BOLT pairs to a FLANGE HOLE, never a nut bore
                        if (!Coaxial(fc, h)) continue;
                        double gap = h.R - fc.R;
                        if (cand.Count < 8) cand.Add((h.R * 1000).ToString("F1") + "@" + Shorten(h.Comp != null ? h.Comp.Name2 : "?"));
                        if (used.Contains(h.Face)) continue;
                        if (gap < -5e-4 || gap > 0.20 * fc.R) continue;   // clearance hole only — reject too-tight / hub bore
                        if (gap < bestGap) { bestGap = gap; bestF = fc; bestH = h; }
                    }
                if (bestH != null)
                {
                    used.Add(bestH.Face);
                    pairs.Add(new Pair { Fastener = bestF, Hole = bestH });
                    if (diag.Count < 4)
                        diag.Add(Shorten(kv.Key) + " boltR=" + (bestF.R * 1000).ToString("F1") +
                            " holeR=" + (bestH.R * 1000).ToString("F1") + "@" + Shorten(bestH.Comp != null ? bestH.Comp.Name2 : "?") +
                            " Omm=(" + Vec(bestH.O, 1000, "F0") + ") D=(" + Vec(bestH.D, 1, "F2") + ") cands[" + string.Join(" ", cand) + "]");
                }
            }
            return pairs;
        }

        // Component-pair key — identifies a bolt/hole pairing across a rebuild (Face2 handles go stale, names don't).
        private static string CompKey(Pair p) =>
            (p.Fastener.Comp.Name2 ?? "") + "|" + (p.Hole.Comp.Name2 ?? "");

        // Re-collect FRESH cylinder faces after the rebuild and re-pair, so Mender's retry selects live geometry
        // (the Face2 handles captured before ForceRebuild3 are stale). Returns only the pairs we still need to fix.
        private static Dictionary<string, Pair> FreshPairsFor(MathUtility mu, AssemblyDoc asm, List<Attempt> proud)
        {
            var want = new HashSet<string>();
            foreach (var a in proud) want.Add(CompKey(a.P));

            var fasteners = new List<Cyl>();
            var holes = new List<Cyl>();
            object[] comps = asm.GetComponents(false) as object[];
            foreach (var o in comps ?? new object[0])
            {
                var comp = o as Component2;
                if (comp == null || comp.IsSuppressed()) continue;
                CollectCylinders(mu, comp, LooksLikeFastener(comp.Name2) ? fasteners : holes);
            }

            var map = new Dictionary<string, Pair>();
            foreach (var p in Pairing(fasteners, holes, new List<string>()))
            {
                string k = CompKey(p);
                if (want.Contains(k) && !map.ContainsKey(k)) map[k] = p;
            }
            return map;
        }

        // Re-acquire ONE pair's live geometry (fresh Face2 handles) after a COM disconnect / stale-ref failure.
        private static Pair ReFetch(MathUtility mu, AssemblyDoc asm, Pair stale)
        {
            var fresh = FreshPairsFor(mu, asm, new List<Attempt> { new Attempt { P = stale } });
            Pair fp;
            return fresh.TryGetValue(CompKey(stale), out fp) ? fp : stale;
        }

        private static bool Coaxial(Cyl a, Cyl b)
        {
            double dot = a.D[0] * b.D[0] + a.D[1] * b.D[1] + a.D[2] * b.D[2];
            if (Math.Abs(Math.Abs(dot) - 1.0) > 1e-3) return false;
            double[] w = { b.O[0] - a.O[0], b.O[1] - a.O[1], b.O[2] - a.O[2] };
            double proj = w[0] * a.D[0] + w[1] * a.D[1] + w[2] * a.D[2];
            double[] perp = { w[0] - proj * a.D[0], w[1] - proj * a.D[1], w[2] - proj * a.D[2] };
            double dist = Math.Sqrt(perp[0] * perp[0] + perp[1] * perp[1] + perp[2] * perp[2]);
            return dist < 2e-4;
        }

        private static int SafeWrong(IModelDoc2 model)
        {
            try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; }
        }

        // Delete a mate we added (used to roll back a pair that over-defined the assembly).
        private static void DeleteMate(IModelDoc2 model, Mate2 mate)
        {
            if (mate == null) return;
            try
            {
                var feat = mate as Feature;
                if (feat == null) return;
                model.ClearSelection2(true);
                feat.Select2(false, 0);
                model.EditDelete();
                model.ClearSelection2(true);
            }
            catch { }
        }

        private static string AlignName(int a) => a == 0 ? "ALIGNED" : a == 1 ? "ANTI" : "CLOSEST";
        private static string StatusName(int s)
        {
            if (s == (int)swConstrainedStatus_e.swFullyConstrained) return "FULLY";
            if (s == (int)swConstrainedStatus_e.swUnderConstrained) return "UNDER";
            if (s == (int)swConstrainedStatus_e.swOverConstrained) return "OVER";
            if (s == (int)swConstrainedStatus_e.swNoSolution) return "NOSOLN";
            if (s == (int)swConstrainedStatus_e.swInvalidSolution) return "INVALID";
            return "st" + s;
        }

        // Choose the concentric alignment so the bolt's HEAD END faces the seat flange (head side of the hole) — robust
        // for ANY starting orientation, not just "heads already out". Uses the seat target's head-underside face and
        // flange face. sBolt = dot(shankAxis, headUndersideNormal) is bolt-attached (orientation-invariant); the flange
        // normal + hole axis are stationary. decision = -sign(sBolt)·dot(flangeN, holeD): >0 → ALIGNED, else ANTI.
        // Falls back to CLOSEST if no seat target is found. `dec` is returned for logging.
        private static int ChooseConcentricAlign(MathUtility mu, Pair p, List<Plane> flange, out double dec)
        {
            dec = 0;
            string diag;
            var cands = FindSeatCandidates(mu, p, flange, out diag);
            foreach (var fit in cands)
            {
                double dd;
                if (!AxisHitsFace(mu, p, fit.Flange, out dd)) continue;
                double sBolt = Dot(p.Fastener.D, fit.Head.N);
                dec = -Math.Sign(sBolt) * Dot(fit.Flange.N, p.Hole.D);
                return dec > 0 ? (int)swMateAlign_e.swMateAlignALIGNED : (int)swMateAlign_e.swMateAlignANTI_ALIGNED;
            }
            return (int)swMateAlign_e.swMateAlignCLOSEST;
        }

        // Concentric mate between the fastener face and the hole face — aligns the axis with the caller's explicit
        // alignment (from ChooseConcentricAlign), so SW can't flip the bolt end-for-end. Returns mate + AddMate5 code.
        private static Mate2 Concentric(IModelDoc2 model, AssemblyDoc asm, Pair p, int align, out int err)
        {
            err = -101;
            model.ClearSelection2(true);
            var sd = ((SelectionMgr)model.SelectionManager).CreateSelectData();
            sd.Mark = 1;
            bool s1 = ((Entity)p.Fastener.Face).Select4(false, sd);
            bool s2 = ((Entity)p.Hole.Face).Select4(true, sd);
            if (!s1 || !s2) return null;

            var mate = asm.AddMate5((int)swMateType_e.swMateCONCENTRIC, align,
                false, 0, 0, 0, 0, 0, 0, 0, 0, false, false, 0, out err);
            model.ClearSelection2(true);
            return mate;
        }

    }
}
