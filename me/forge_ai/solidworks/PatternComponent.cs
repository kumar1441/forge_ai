using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class PatternResult
    {
        public string SeedComponent;    // the seed fastener we patterned
        public string PatternType;      // "circular" | "linear"
        public int InstancesAdded;      // measured: seed-file instances gained (independent recount)
        public int ExpectedInstances;   // empty holes we set out to fill (= added, when clean)
        public int OverDefined;         // over-defined components AFTER the pattern (must be 0)
        public int RebuildErrors;       // GetWhatsWrongCount AFTER the pattern (must be 0)
        public bool RolledBack;         // pattern created but verify failed -> feature deleted
        public bool AlreadyPatterned;   // idempotent skip (Forge-Pattern present OR all holes already filled)
        public string Info;
        public string Error;
    }

    // a cylindrical face reduced to an assembly-space axis + radius (own copy — AutoMate.Cyl is a different type)
    internal class PatCyl { public Face2 Face; public double[] O; public double[] D; public double R; public Component2 Comp; }

    /// <summary>
    /// PatternComponent (tool "pattern a component around a bolt circle / in a line"). A WRITE handler that takes ONE
    /// seed fastener already seated in a hole and reproduces it — via a REAL SolidWorks component pattern feature
    /// (IFeatureManager.FeatureCircularPattern4 / FeatureLinearPattern4) — into every other empty hole on the ring,
    /// killing the manual "insert + mate 6 more bolts" ritual.
    ///
    /// Named crew:
    ///   Gauge    → reads the assembly, picks the seed, derives the hole ring (bolt-circle centre + count), classifies
    ///              circular vs linear, counts filled vs empty holes, and short-circuits when already populated.
    ///   Stamp    → selects the pattern axis (the flange's central bore) + the seed, and creates the pattern feature,
    ///              tagged "Forge-Pattern" for idempotency.
    ///   Sentinel → INDEPENDENTLY confirms (post-rebuild, by its own recount + geometry): the seed-file instance count
    ///              rose by exactly the number of empty holes, every empty hole now holds a coaxial fastener, nothing
    ///              over-defines, and the rebuild is clean. Anything short of that ROLLS BACK the feature and reports why.
    ///
    /// Component patterns add NO mates (the copies are pattern-driven, IsPatternInstance), so this is one Ctrl+Z to undo
    /// and never saves the document. Idempotent: a second run sees the Forge-Pattern feature (or full holes) and no-ops.
    /// </summary>
    public static class PatternComponent
    {
        private static readonly string[] FastenerHints =
            { "bolt", "screw", "hcs", "shcs", "capscrew", "cap screw", "fastener", "hex", "socket",
              "machine screw", "stud", "sems", "allen", "grub", "cheese", "din", "iso", "b18",
              "bulong", "vit", "vis", "boulon", "goujon" };
        private static readonly string[] NotFastener =
            { "nut", "ecrou", "washer", "rondelle", "clavette", "key", "pin", "goupille",
              "4035", "4032", "4033", "4034", "7089", "7090", "7091", "8738" };

        public static bool IsPatternIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return Regex.IsMatch(cmd,
                @"\b(pattern|populate|circular[\s-]?pattern|put\s+(a\s+|the\s+)?(bolt|screw|fastener)s?\s+in\s+(all|every|each|the)|(fill|populate)\s+(the\s+)?(empty\s+)?holes?|in\s+every\s+hole|in\s+each\s+hole|around\s+the\s+(bolt\s+)?circle)\b",
                RegexOptions.IgnoreCase);
        }

        private static bool LooksLikeFastener(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            foreach (var x in NotFastener) if (n.Contains(x)) return false;
            foreach (var h in FastenerHints) if (n.Contains(h)) return true;
            return false;
        }

        public static async Task<PatternResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new PatternResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to pattern a component."; return res; }
            var mu = (MathUtility)app.GetMathUtility();

            await emit("Gauge", "reading the assembly", "run", null);

            // ---- idempotency #1: a prior Forge-Pattern feature means we're done ----
            if (FeatureExists(model, "Forge-Pattern"))
            {
                res.AlreadyPatterned = true;
                res.Info = "Already patterned — a Forge-Pattern feature is present; nothing to do.";
                await emit("Gauge", null, "done", "Forge-Pattern already present — skipping");
                return res;
            }

            // ---- collect components ----
            object[] comps = asm.GetComponents(false) as object[];
            var fastComps = new List<Component2>();
            var flangeComps = new List<Component2>();
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                if (LooksLikeFastener(SafeName(c))) fastComps.Add(c); else flangeComps.Add(c);
            }
            if (fastComps.Count == 0)
            { res.Error = "No fastener (bolt/screw) found to pattern — open an assembly with a bolt seated in a hole."; await emit("Gauge", null, "fail", res.Error); return res; }

            // ---- pick the seed: named-in-intent, else the first NON-pattern-instance fastener ----
            Component2 seed = PickSeed(fastComps, intent);
            if (seed == null)
            { res.Error = "Couldn't pick a seed fastener — name the bolt to pattern (e.g. \"pattern Bolt-1 around the holes\")."; await emit("Gauge", null, "fail", res.Error); return res; }
            res.SeedComponent = SafeName(seed);

            // ---- cylinders (assembly coords) ----
            var seedCyls = new List<PatCyl>(); CollectCylinders(mu, seed, seedCyls);
            var flangeCyls = new List<PatCyl>();
            foreach (var c in flangeComps) CollectCylinders(mu, c, flangeCyls);
            var fastCyls = new List<PatCyl>();
            foreach (var c in fastComps) CollectCylinders(mu, c, fastCyls);

            // ---- find the hole the seed is seated in (its shank coaxial with a flange clearance hole) ----
            PatCyl seedShank = null, seedHole = null; double bestGap = double.MaxValue;
            foreach (var s in seedCyls)
                foreach (var h in flangeCyls)
                {
                    if (!Coaxial(s, h)) continue;
                    double gap = h.R - s.R;
                    if (gap < -3e-4 || gap > 0.30 * s.R) continue;   // clearance hole only
                    if (gap < bestGap) { bestGap = gap; seedShank = s; seedHole = h; }
                }
            if (seedHole == null)
            { res.Error = "The seed bolt " + res.SeedComponent + " isn't seated in a hole yet — mate it into a hole first, then I can pattern it."; await emit("Gauge", null, "fail", res.Error); return res; }
            double[] D = Normalize(seedHole.D);

            // ---- the hole ring: same radius, parallel axis, deduped by coaxiality (a hole through two stacked plates
            //      is ONE bolt position, counted once) ----
            var ring = new List<PatCyl>();
            foreach (var h in flangeCyls)
            {
                if (Math.Abs(Math.Abs(Dot(Normalize(h.D), D)) - 1.0) > 1e-3) continue;
                if (Math.Abs(h.R - seedHole.R) > Math.Max(3e-4, 0.08 * seedHole.R)) continue;
                bool dup = false; foreach (var r in ring) if (Coaxial(r, h)) { dup = true; break; }
                if (!dup) ring.Add(h);
            }

            // ---- bolt-circle centre = seed-hole origin + mean of the perpendicular offsets to every ring hole ----
            double[] refO = seedHole.O;
            double[] sumPerp = { 0, 0, 0 };
            foreach (var h in ring) { double[] p = PerpTo(Sub(h.O, refO), D); sumPerp[0] += p[0]; sumPerp[1] += p[1]; sumPerp[2] += p[2]; }
            double invN = ring.Count > 0 ? 1.0 / ring.Count : 0;
            double[] center = { refO[0] + sumPerp[0] * invN, refO[1] + sumPerp[1] * invN, refO[2] + sumPerp[2] * invN };

            // ---- classify circular vs linear: are the holes collinear in the plane ⊥ the axis? ----
            double[] lineDir = null; bool collinear = true;
            foreach (var h in ring)
            {
                double[] p = PerpTo(Sub(h.O, refO), D); double m = Norm(p);
                if (m < 1e-5) continue;
                double[] u = Scale(p, 1.0 / m);
                if (lineDir == null) lineDir = u;
                else if (Math.Abs(Math.Abs(Dot(u, lineDir)) - 1.0) > 1e-2) { collinear = false; break; }
            }
            bool isLinear = collinear && lineDir != null && ring.Count >= 2;
            res.PatternType = isLinear ? "linear" : "circular";

            // ---- for a circle, keep only holes ~equidistant from centre (guards against a stray same-size hole) ----
            double meanR = 0;
            if (!isLinear)
            {
                var radii = new List<double>(); double rsum = 0;
                foreach (var h in ring) { double d = PerpDist(h.O, center, D); radii.Add(d); rsum += d; }
                meanR = ring.Count > 0 ? rsum / ring.Count : 0;
                var ring2 = new List<PatCyl>();
                for (int i = 0; i < ring.Count; i++)
                    if (meanR <= 1e-6 || Math.Abs(radii[i] - meanR) <= 0.15 * meanR + 2e-4) ring2.Add(ring[i]);
                ring = ring2;
            }
            int N = ring.Count;
            if (N < 2)
            { res.Error = "Only found " + N + " hole on the seed's bolt circle — need at least 2 matching holes to pattern."; await emit("Gauge", null, "fail", res.Error); return res; }

            // ---- filled vs empty holes ----
            int filled = 0; var emptyHoles = new List<PatCyl>();
            foreach (var h in ring)
            {
                bool has = false;
                foreach (var f in fastCyls) { if (!Coaxial(f, h)) continue; if (f.R <= h.R + 3e-4) { has = true; break; } }
                if (has) filled++; else emptyHoles.Add(h);
            }
            res.ExpectedInstances = emptyHoles.Count;

            // ---- idempotency #2: every hole already has a fastener ----
            if (emptyHoles.Count == 0)
            {
                res.AlreadyPatterned = true;
                res.Info = "All " + N + " holes on the bolt circle already have a fastener — nothing to pattern.";
                await emit("Gauge", null, "done", "all " + N + " holes already populated — skipping");
                return res;
            }
            // ---- partial population: an even pattern from one seed would land on filled holes — refuse honestly ----
            if (filled != 1)
            {
                res.Error = N + " holes on the circle, " + filled + " already have bolts and " + emptyHoles.Count +
                            " are empty — an even pattern from a single seed would land on the filled holes. Leave one seed bolt and clear the rest, or mate them individually.";
                await emit("Gauge", null, "fail", res.Error); return res;
            }

            string seedPath = SafePath(seed);
            int beforeCount = CountByPath(comps, seedPath);
            await emit("Gauge", null, "done",
                "seed " + res.SeedComponent + " · " + N + "-hole " + res.PatternType + " circle, " +
                emptyHoles.Count + " empty → patterning " + emptyHoles.Count + " cop" + (emptyHoles.Count == 1 ? "y" : "ies"));

            // =================== Stamp: create the real component pattern ===================
            await emit("Stamp", "creating the " + res.PatternType + " component pattern", "run", null);
            Feature pf = null;
            var selMgr = (SelectionMgr)model.SelectionManager;

            if (!isLinear)
            {
                // axis = the flange's central bore (cylinder ∥ the hole axis whose axis line runs nearest the circle centre)
                PatCyl axisCyl = FindCentralAxis(flangeCyls, center, D, meanR);
                if (axisCyl == null)
                { res.Error = "Found the " + N + "-hole bolt circle but no central axis face (a bore/hub concentric with the circle) to pattern around. Add a concentric bore or a reference axis at the circle centre, then re-run."; await emit("Stamp", null, "fail", res.Error); return res; }

                model.ClearSelection2(true);
                var sd1 = selMgr.CreateSelectData(); sd1.Mark = 1;
                ((Entity)axisCyl.Face).Select4(false, sd1);
                var sd4 = selMgr.CreateSelectData(); sd4.Mark = 4;
                seed.Select4(true, sd4, false);
                try { pf = model.FeatureManager.FeatureCircularPattern4(N, 2 * Math.PI, false, "", false, true, false); }
                catch (Exception ex) { model.ClearSelection2(true); res.Error = "Circular pattern threw: " + ex.Message; await emit("Stamp", null, "fail", res.Error); return res; }
                model.ClearSelection2(true);
            }
            else
            {
                double spacing = NearestSpacing(ring, lineDir);
                if (spacing <= 1e-6)
                { res.Error = "Holes lie on a line but their spacing couldn't be measured — pattern not attempted."; await emit("Stamp", null, "fail", res.Error); return res; }
                Entity dirEdge = FindLinearEdge(mu, flangeComps, lineDir);
                if (dirEdge == null)
                { res.Error = "Holes lie on a line but there's no straight edge parallel to it to set the pattern direction. Add a reference axis/edge along the hole line and re-run."; await emit("Stamp", null, "fail", res.Error); return res; }

                model.ClearSelection2(true);
                dirEdge.Select4(false, SetMark(selMgr, 1));
                seed.Select4(true, SetMark(selMgr, 4), false);
                try
                {
                    pf = model.FeatureManager.FeatureLinearPattern4(
                        N, spacing, 1, 0, false, false, "", "", false, false, false, false, false, false, false, false, false, false, 0, 0);
                }
                catch (Exception ex) { model.ClearSelection2(true); res.Error = "Linear pattern threw: " + ex.Message; await emit("Stamp", null, "fail", res.Error); return res; }
                model.ClearSelection2(true);
            }

            if (pf == null)
            { res.Error = "SolidWorks returned no pattern feature — the axis or seed selection didn't hold."; await emit("Stamp", null, "fail", res.Error); return res; }
            try { pf.Name = "Forge-Pattern"; } catch { }
            model.ForceRebuild3(false);
            await emit("Stamp", null, "done", "pattern feature created — verifying it landed");

            // =================== Sentinel: INDEPENDENT post-rebuild verification ===================
            await emit("Sentinel", "confirming the new instances landed in the empty holes", "run", null);
            object[] comps2 = asm.GetComponents(false) as object[];
            int afterCount = CountByPath(comps2, seedPath);
            res.InstancesAdded = afterCount - beforeCount;
            res.RebuildErrors = SafeWrong(model);

            int over = 0;
            var fastCyls2 = new List<PatCyl>();
            foreach (var o in comps2 ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) over++;
                if (LooksLikeFastener(SafeName(c))) CollectCylinders(mu, c, fastCyls2);
            }
            res.OverDefined = over;

            int populated = 0;
            foreach (var h in emptyHoles)
                foreach (var f in fastCyls2) { if (!Coaxial(f, h)) continue; if (f.R <= h.R + 3e-4) { populated++; break; } }

            bool countOk = res.InstancesAdded == emptyHoles.Count;
            bool geomOk = populated == emptyHoles.Count;
            bool clean = res.RebuildErrors == 0 && res.OverDefined == 0;

            if (!(countOk && geomOk && clean))
            {
                DeleteFeature(model, "Forge-Pattern");
                model.ForceRebuild3(false);
                res.RolledBack = true;
                res.Error = !clean
                    ? ("Pattern " + (res.OverDefined > 0 ? "over-defined " + res.OverDefined + " component(s)" : "left " + res.RebuildErrors + " rebuild error(s)") + " — rolled back, assembly restored.")
                    : (!geomOk
                        ? ("Pattern added " + res.InstancesAdded + " instance(s) but only " + populated + " of " + emptyHoles.Count + " landed in a hole — rolled back (holes may be unevenly spaced).")
                        : ("Pattern added " + res.InstancesAdded + " instance(s), expected " + emptyHoles.Count + " — rolled back."));
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Patterned " + res.SeedComponent + " into " + res.InstancesAdded + " empty hole" +
                       (res.InstancesAdded == 1 ? "" : "s") + " on the " + N + "-hole " + res.PatternType +
                       " circle — all " + N + " holes now filled, no over-define, rebuild clean. One Ctrl+Z undoes it.";
            await emit("Sentinel", null, "done",
                res.InstancesAdded + " instance(s) verified in place · " + N + "/" + N + " holes filled · rebuild clean");
            return res;
        }

        // ---- seed pick: named-in-intent (any fastener whose name appears in the prompt) wins; else the first
        //      NON-pattern-instance fastener (patterning an existing instance is nonsense). ----
        private static Component2 PickSeed(List<Component2> fasteners, string intent)
        {
            string lo = (intent ?? "").ToLowerInvariant();
            foreach (var c in fasteners)
            {
                string n = SafeName(c); if (string.IsNullOrEmpty(n)) continue;
                string nl = n.ToLowerInvariant();
                if (nl.Length >= 3 && lo.Contains(nl) && !IsInstance(c)) return c;
            }
            foreach (var c in fasteners) if (!IsInstance(c)) return c;
            return fasteners.Count > 0 ? fasteners[0] : null;
        }

        private static bool IsInstance(Component2 c) { try { return c.IsPatternInstance(); } catch { return false; } }

        // ---- the central bore: the flange cylinder ∥ the hole axis whose axis line runs nearest the circle centre
        //      (holes sit at ~meanR from centre; the bore sits at ~0), used as the circular-pattern axis. ----
        private static PatCyl FindCentralAxis(List<PatCyl> flangeCyls, double[] center, double[] D, double meanR)
        {
            PatCyl best = null; double bestDist = double.MaxValue;
            foreach (var c in flangeCyls)
            {
                if (Math.Abs(Math.Abs(Dot(Normalize(c.D), D)) - 1.0) > 1e-3) continue;
                double d = PerpDist(c.O, center, D);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            if (best == null) return null;
            return bestDist < 0.5 * meanR ? best : null;   // clearly concentric with the circle, not a ring hole
        }

        // ---- first straight edge on any flange body whose direction is parallel to the hole line (linear axis ref) ----
        private static Entity FindLinearEdge(MathUtility mu, List<Component2> flangeComps, double[] lineDir)
        {
            foreach (var comp in flangeComps)
            {
                MathTransform xf = null; try { xf = comp.Transform2; } catch { }
                if (xf == null) continue;
                object bi;
                object[] bodies = null; try { bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[]; } catch { }
                if (bodies == null) continue;
                foreach (var bo in bodies)
                {
                    var body = bo as Body2; if (body == null) continue;
                    object[] edges = null; try { edges = body.GetEdges() as object[]; } catch { }
                    if (edges == null) continue;
                    foreach (var eo in edges)
                    {
                        var edge = eo as Edge; if (edge == null) continue;
                        Curve cv = null; try { cv = edge.GetCurve() as Curve; } catch { }
                        if (cv == null) continue;
                        bool line = false; try { line = cv.IsLine(); } catch { }
                        if (!line) continue;
                        double[] lp = null; try { lp = cv.LineParams as double[]; } catch { }
                        if (lp == null || lp.Length < 6) continue;
                        var dv = (MathVector)((MathVector)mu.CreateVector(new[] { lp[3], lp[4], lp[5] })).MultiplyTransform(xf);
                        double[] da = dv.ArrayData as double[];
                        double dl = Norm(da); if (dl < 1e-9) continue;
                        double[] u = { da[0] / dl, da[1] / dl, da[2] / dl };
                        if (Math.Abs(Math.Abs(Dot(u, lineDir)) - 1.0) < 1e-2) return edge as Entity;
                    }
                }
            }
            return null;
        }

        // min positive gap between adjacent holes projected onto the line direction
        private static double NearestSpacing(List<PatCyl> ring, double[] lineDir)
        {
            var ts = new List<double>();
            foreach (var h in ring) ts.Add(Dot(h.O, lineDir));
            ts.Sort();
            double best = double.MaxValue;
            for (int i = 1; i < ts.Count; i++) { double g = ts[i] - ts[i - 1]; if (g > 1e-6 && g < best) best = g; }
            return best == double.MaxValue ? 0 : best;
        }

        private static SelectData SetMark(SelectionMgr sm, int mark) { var sd = sm.CreateSelectData(); sd.Mark = mark; return sd; }

        // ---------- geometry collection + vector helpers (self-contained) ----------
        private static void CollectCylinders(MathUtility mu, Component2 comp, List<PatCyl> into)
        {
            try
            {
                var xform = comp.Transform2; object bi;
                object[] bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                if (bodies == null) return;
                foreach (var bo in bodies)
                {
                    var body = bo as Body2; if (body == null) continue;
                    object[] faces = body.GetFaces() as object[]; if (faces == null) continue;
                    foreach (var f in faces)
                    {
                        var face = f as Face2; if (face == null) continue;
                        var surf = face.GetSurface() as Surface; if (surf == null || !surf.IsCylinder()) continue;
                        double[] cp = surf.CylinderParams as double[]; if (cp == null || cp.Length < 7) continue;
                        var op = (MathPoint)((MathPoint)mu.CreatePoint(new[] { cp[0], cp[1], cp[2] })).MultiplyTransform(xform);
                        var dv = (MathVector)((MathVector)mu.CreateVector(new[] { cp[3], cp[4], cp[5] })).MultiplyTransform(xform);
                        double[] oa = op.ArrayData as double[]; double[] da = dv.ArrayData as double[];
                        double dl = Norm(da); if (dl < 1e-9) continue;
                        into.Add(new PatCyl { Face = face, Comp = comp, R = cp[6], O = new[] { oa[0], oa[1], oa[2] }, D = new[] { da[0] / dl, da[1] / dl, da[2] / dl } });
                    }
                }
            }
            catch { }
        }

        private static bool Coaxial(PatCyl a, PatCyl b)
        {
            double dot = Dot(a.D, b.D);
            if (Math.Abs(Math.Abs(dot) - 1.0) > 1e-3) return false;
            double[] w = { b.O[0] - a.O[0], b.O[1] - a.O[1], b.O[2] - a.O[2] };
            double proj = Dot(w, a.D);
            double[] perp = { w[0] - proj * a.D[0], w[1] - proj * a.D[1], w[2] - proj * a.D[2] };
            return Norm(perp) < 2e-4;
        }

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Norm(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Scale(double[] v, double s) => new[] { v[0] * s, v[1] * s, v[2] * s };
        private static double[] Normalize(double[] v) { double n = Norm(v); return n < 1e-12 ? v : new[] { v[0] / n, v[1] / n, v[2] / n }; }
        private static double[] PerpTo(double[] w, double[] d) { double p = Dot(w, d); return new[] { w[0] - p * d[0], w[1] - p * d[1], w[2] - p * d[2] }; }
        private static double PerpDist(double[] pt, double[] center, double[] d) => Norm(PerpTo(Sub(pt, center), d));

        // ---------- misc ----------
        private static string SafeName(Component2 c) { try { return c.Name2; } catch { return null; } }
        private static string SafePath(Component2 c) { try { return c.GetPathName(); } catch { return null; } }

        private static int CountByPath(object[] comps, string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            int n = 0;
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string p = null; try { p = c.GetPathName(); } catch { }
                if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) n++;
            }
            return n;
        }

        private static bool FeatureExists(IModelDoc2 model, string name)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null) { string n = null; try { n = f.Name; } catch { } if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true; f = f.GetNextFeature() as Feature; }
            return false;
        }

        private static void DeleteFeature(IModelDoc2 model, string name)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string n = null; try { n = f.Name; } catch { }
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                {
                    try { model.ClearSelection2(true); f.Select2(false, 0); model.EditDelete(); model.ClearSelection2(true); } catch { }
                    return;
                }
                f = f.GetNextFeature() as Feature;
            }
        }

        private static int SafeWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
