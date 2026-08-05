using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class PatternDrivenPatternComponentResult
    {
        public string SeedComponent;
        public string HostComponent;   // the OTHER component whose part carries the feature pattern being followed
        public string HostFeature;     // the LPattern/CirPattern feature name
        public string PatternKind;     // "linear" | "circular"
        public int Count;              // target total instances, read from the host feature (authoritative)
        public double SpacingMm;       // linear only
        public double AngleDeg;        // circular only
        public int InstancesAdded;
        public int ExpectedInstances;
        public int OverDefined;
        public int RebuildErrors;
        public bool RolledBack;
        public bool AlreadyPatterned;
        public string Info;
        public string Error;
        public string Question;
        public bool NeedsConfirm;
        public string Diag;
    }

    /// <summary>
    /// PatternDrivenPatternComponent (tool 44, "pattern_driven_pattern") — a WRITE handler that patterns ONE named
    /// assembly component to FOLLOW an existing feature pattern (an LPattern/CirPattern already sitting on some
    /// OTHER part in the assembly, e.g. a plate's own bolt-hole array) — never given a count/spacing/angle
    /// directly by the user (that's tools 41/42's job) and never derived from bare hole/cylinder geometry with no
    /// backing feature (that's PatternComponent.cs's bolt-circle job). This is the only one of the three that
    /// requires and reads an ACTUAL FeatureManager pattern feature: the host's ILinearPatternFeatureData
    /// (D1TotalInstances, D1Spacing) or ICircularPatternFeatureData (TotalInstances), the same proven-live
    /// definitions GetPatternInfo.cs/EditPatternSpacing.cs/EditPatternCount.cs already read on this build.
    ///
    /// LANDMINE (do not re-attempt blind — confirmed TWICE already for tools 41/42): a Component2-seeded
    /// IFeatureManager.FeatureLinearPattern4 / FeatureCircularPattern4 call returns a null feature on this R2026x
    /// build even with proven-good selection. Same WORKING ROUTE as 41/42 instead: AssemblyDoc.AddComponent5 to
    /// insert the missing instances of the seed's own file, positioned/rotated using the SAME direction/axis
    /// auto-detection those two handlers already proved live (longest straight edge elsewhere for linear, largest
    /// cylindrical face elsewhere for circular) — only the INSTANCE COUNT (and, for linear, the spacing) comes
    /// from the host feature's own data instead of the user's words.
    ///
    /// Host resolution: the first OTHER (non-seed) component whose part document has an LPattern or CirPattern
    /// feature. Component resolution reuses SelectComponent's normalized-name matcher. No single pattern feature
    /// exists on the ASSEMBLY side to Ctrl+Z as a unit — each insert is its own undo step; Forge never saves.
    /// Idempotent: if the seed's file already has >= the host pattern's own instance count, nothing is added.
    /// </summary>
    public static class PatternDrivenPatternComponent
    {
        private const double MM = 0.001;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bpattern\b")) return false;
            bool named = Regex.IsMatch(c, @"\bcomponent\b") || Regex.IsMatch(c, "[\"']([^\"']{2,})[\"']");
            if (!named) return false;
            bool driven = Regex.IsMatch(c, @"\bpattern[\s-]?driven\b") ||
                          (Regex.IsMatch(c, @"\b(existing|feature|hole)\s+pattern\b") &&
                           Regex.IsMatch(c, @"\b(follow(s|ing)?|match(es|ing)?|same\s+as|per|driven)\b"));
            return driven;
        }

        public static async Task<PatternDrivenPatternComponentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new PatternDrivenPatternComponentResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to pattern a component."; return res; }

            await emit("Gauge", "reading the assembly", "run", null);

            string query = ParseComponentName(intent);
            if (string.IsNullOrEmpty(query))
            { res.Error = "Which component? Name it (e.g. \"pattern the pin component to follow the existing hole pattern\")."; await emit("Gauge", null, "fail", res.Error); return res; }

            object[] comps = asm.GetComponents(false) as object[];
            Component2 exact = null;
            var candidates = new List<Component2>();
            string normQuery = SelectComponent.Normalize(query);
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                string nm = SafeName(c); if (string.IsNullOrEmpty(nm)) continue;
                string norm = SelectComponent.Normalize(nm);
                if (norm == normQuery) { if (exact == null) exact = c; candidates.Add(c); }
                else if (norm.Contains(normQuery) || normQuery.Contains(norm)) candidates.Add(c);
            }
            Component2 seed = exact;
            if (seed == null && candidates.Count == 1) seed = candidates[0];
            if (seed == null && candidates.Count > 1)
            {
                res.NeedsConfirm = true;
                res.Question = candidates.Count + " components match '" + query + "' — which one? (e.g. include the instance number.)";
                await emit("Gauge", null, "fail", res.Question);
                return res;
            }
            if (seed == null)
            { res.Error = "Couldn't find a component matching '" + query + "'."; await emit("Gauge", null, "fail", res.Error); return res; }
            res.SeedComponent = SafeName(seed);

            string seedPath = SafePath(seed);
            if (string.IsNullOrEmpty(seedPath))
            { res.Error = "Couldn't resolve '" + res.SeedComponent + "'s file path."; await emit("Gauge", null, "fail", res.Error); return res; }

            // ---- find the host: the first OTHER component whose part carries an LPattern/CirPattern feature ----
            Component2 host = null; Feature hostFeat = null; string kind = null;
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null || c == seed) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                IModelDoc2 pdoc = null; try { pdoc = c.GetModelDoc2() as IModelDoc2; } catch { }
                if (pdoc == null) continue;
                Feature f = null; try { f = pdoc.FirstFeature() as Feature; } catch { }
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (tn != null && tn.IndexOf("LPattern", StringComparison.OrdinalIgnoreCase) >= 0)
                    { host = c; hostFeat = f; kind = "linear"; break; }
                    if (tn != null && tn.IndexOf("CirPattern", StringComparison.OrdinalIgnoreCase) >= 0)
                    { host = c; hostFeat = f; kind = "circular"; break; }
                    f = f.GetNextFeature() as Feature;
                }
                if (host != null) break;
            }
            if (host == null)
            { res.Error = "No existing feature pattern (linear or circular) found on another component to follow — pattern that part's holes first, then run this."; await emit("Gauge", null, "fail", res.Error); return res; }
            res.HostComponent = SafeName(host);
            res.HostFeature = SafeName(hostFeat);
            res.PatternKind = kind;

            int count = 0; double spacingMm = 0;
            try
            {
                object def = hostFeat.GetDefinition();
                if (kind == "linear")
                {
                    var lin = def as ILinearPatternFeatureData;
                    if (lin != null) { try { count = lin.D1TotalInstances; } catch { } try { spacingMm = lin.D1Spacing * 1000.0; } catch { } }
                }
                else
                {
                    var cir = def as ICircularPatternFeatureData;
                    if (cir != null) { try { count = cir.TotalInstances; } catch { } }
                }
            }
            catch { }
            if (count < 2)
            { res.Error = "Couldn't read a usable instance count from '" + res.HostFeature + "' on " + res.HostComponent + "."; await emit("Gauge", null, "fail", res.Error); return res; }
            res.Count = count;
            res.AngleDeg = 360.0; // scope note: total sweep isn't re-derived from the feature — a full circle is assumed, same default tool 42 uses

            int existingCount = CountByPath(comps, seedPath);
            if (existingCount >= count)
            {
                res.AlreadyPatterned = true;
                res.Info = "Already " + existingCount + " instance(s) of " + res.SeedComponent + " present (>= the host pattern's " + count + ") — nothing to do.";
                await emit("Gauge", null, "done", res.Info);
                return res;
            }
            res.ExpectedInstances = count - existingCount;

            var mu = (MathUtility)app.GetMathUtility();
            double[] seedXf = null; try { seedXf = seed.Transform2.ArrayData as double[]; } catch { }
            if (seedXf == null || seedXf.Length < 12)
            { res.Error = "Couldn't read '" + res.SeedComponent + "'s placement transform."; await emit("Gauge", null, "fail", res.Error); return res; }
            double[] seedPos = { seedXf[9], seedXf[10], seedXf[11] };

            double[][] newPositions;
            if (kind == "linear")
            {
                res.SpacingMm = spacingMm > 0 ? spacingMm : 20.0;
                double[] dir;
                if (!FindLinearDirection(mu, seed, comps, out dir))
                { res.Error = "Couldn't find a straight edge to set the pattern direction — add one and re-run."; await emit("Gauge", null, "fail", res.Error); return res; }
                newPositions = new double[res.ExpectedInstances][];
                for (int i = 0; i < res.ExpectedInstances; i++)
                {
                    int step = existingCount + i;
                    newPositions[i] = new[] {
                        seedPos[0] + dir[0] * res.SpacingMm * MM * step,
                        seedPos[1] + dir[1] * res.SpacingMm * MM * step,
                        seedPos[2] + dir[2] * res.SpacingMm * MM * step
                    };
                }
                await emit("Gauge", null, "done",
                    "host '" + res.HostFeature + "' on " + res.HostComponent + " · linear ×" + count + " @ " + Trim(res.SpacingMm) + "mm → adding " + res.ExpectedInstances);
            }
            else
            {
                AxisCyl axis = FindAxisCylinder(mu, seed, comps);
                if (axis == null)
                { res.Error = "Couldn't find a cylindrical face elsewhere in the assembly to pattern '" + res.SeedComponent + "' around."; await emit("Gauge", null, "fail", res.Error); return res; }
                double[] w = Sub(seedPos, axis.O);
                double along = Dot(w, axis.D);
                double[] radial = Sub(w, Scale(axis.D, along));
                if (Norm(radial) < 1e-6)
                { res.Error = "'" + res.SeedComponent + "' sits ON the pattern axis — no radial offset to rotate around."; await emit("Gauge", null, "fail", res.Error); return res; }
                double stepRad = (res.AngleDeg * Math.PI / 180.0) / count;
                newPositions = new double[res.ExpectedInstances][];
                for (int i = 0; i < res.ExpectedInstances; i++)
                {
                    int step = existingCount + i;
                    double[] rotated = RotateAroundAxis(radial, axis.D, stepRad * step);
                    newPositions[i] = Add(Add(axis.O, Scale(axis.D, along)), rotated);
                }
                await emit("Gauge", null, "done",
                    "host '" + res.HostFeature + "' on " + res.HostComponent + " · circular ×" + count + " → adding " + res.ExpectedInstances);
            }

            // =================== Stamp: insert the new instances following the host pattern ===================
            await emit("Stamp", "inserting the pattern-driven copies", "run", null);
            int oe = 0, ow = 0; try { app.OpenDoc6(seedPath, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow); } catch { }
            try { model.ClearSelection2(true); } catch { }
            foreach (var pos in newPositions)
            {
                try { asm.AddComponent5(seedPath, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, "", false, "", pos[0], pos[1], pos[2]); }
                catch (Exception ex) { res.Error = "AddComponent5 threw: " + ex.Message; await emit("Stamp", null, "fail", res.Error); return res; }
            }
            try { model.ClearSelection2(true); } catch { }
            try { model.EditRebuild3(); } catch { try { model.ForceRebuild3(false); } catch { } }
            await emit("Stamp", null, "done", "copies inserted — verifying they landed");

            // =================== Sentinel: INDEPENDENT post-rebuild verification ===================
            await emit("Sentinel", "confirming the new instances landed", "run", null);
            object[] comps2 = asm.GetComponents(false) as object[];
            int afterCount = CountByPath(comps2, seedPath);
            res.InstancesAdded = afterCount - existingCount;
            res.RebuildErrors = SafeWrong(model);

            int over = 0;
            foreach (var o in comps2 ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) over++;
            }
            res.OverDefined = over;
            res.Diag = "hostCount=" + count + " existing=" + existingCount + " added=" + res.InstancesAdded + " overDefined=" + over + " rebuildErr=" + res.RebuildErrors;

            bool countOk = res.InstancesAdded == res.ExpectedInstances;
            bool clean = res.RebuildErrors == 0 && res.OverDefined == 0;
            if (!(countOk && clean))
            {
                RemoveExtraInstances(model, asm, seedPath, existingCount);
                try { model.ForceRebuild3(false); } catch { }
                res.RolledBack = true;
                res.Error = !clean
                    ? ("Insert " + (res.OverDefined > 0 ? "over-defined " + res.OverDefined + " component(s)" : "left " + res.RebuildErrors + " rebuild error(s)") + " — rolled back. " + res.Diag)
                    : ("Added " + res.InstancesAdded + " instance(s), expected " + res.ExpectedInstances + " — rolled back. " + res.Diag);
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Patterned " + res.SeedComponent + " to follow " + res.HostComponent + "'s '" + res.HostFeature + "' (" + count + " total instances, " +
                       res.InstancesAdded + " new) — no over-define, rebuild clean. Forge never saves.";
            await emit("Sentinel", null, "done", res.InstancesAdded + " instance(s) verified in place · rebuild clean");
            return res;
        }

        // ---------- parsing ----------
        private static string ParseComponentName(string intent)
        {
            string raw = intent ?? "";
            var qm = Regex.Match(raw, "[\"']([^\"']{2,})[\"']");
            if (qm.Success) return qm.Groups[1].Value.Trim();
            var m = Regex.Match(raw, @"pattern\s+(?:the\s+)?(.+?)\s+component\b", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return null;
        }

        // ---- linear direction: the longest straight edge on any OTHER (non-seed) component (same lesson tools
        //      41/42 already learned about not leaning on the seed's own geometry). ----
        private static bool FindLinearDirection(MathUtility mu, Component2 seed, object[] allComps, out double[] dir)
        {
            dir = null;
            LinEdge best = null; double bestLen = -1;
            foreach (var o in allComps ?? new object[0])
            {
                var c = o as Component2; if (c == null || c == seed) continue;
                foreach (var e in CollectEdges(mu, c)) if (e.Len > bestLen) { bestLen = e.Len; best = e; }
            }
            if (best == null) foreach (var e in CollectEdges(mu, seed)) if (e.Len > bestLen) { bestLen = e.Len; best = e; }
            if (best == null) return false;
            dir = best.D; return true;
        }

        private class LinEdge { public double[] D; public double Len; }

        private static List<LinEdge> CollectEdges(MathUtility mu, Component2 comp)
        {
            var list = new List<LinEdge>();
            try
            {
                var xf = comp.Transform2; object bi;
                object[] bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                if (bodies == null) return list;
                foreach (var bo in bodies)
                {
                    var body = bo as Body2; if (body == null) continue;
                    object[] edges = body.GetEdges() as object[]; if (edges == null) continue;
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
                        list.Add(new LinEdge { D = u, Len = EdgeLength(edge) });
                    }
                }
            }
            catch { }
            return list;
        }

        private static double EdgeLength(Edge edge)
        {
            try
            {
                var cp = edge.GetCurveParams2() as double[];
                if (cp == null || cp.Length < 6) return 0;
                double dx = cp[3] - cp[0], dy = cp[4] - cp[1], dz = cp[5] - cp[2];
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            catch { return 0; }
        }

        // ---- circular axis: the largest-radius cylindrical face on any OTHER (non-seed) component ----
        private class AxisCyl { public double R; public double[] O; public double[] D; }

        private static AxisCyl FindAxisCylinder(MathUtility mu, Component2 seed, object[] allComps)
        {
            AxisCyl best = null;
            foreach (var o in allComps ?? new object[0])
            {
                var c = o as Component2; if (c == null || c == seed) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                try
                {
                    var xf = c.Transform2; object bi;
                    object[] bodies = c.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                    if (bodies == null) continue;
                    foreach (var bo in bodies)
                    {
                        var body = bo as Body2; if (body == null) continue;
                        object[] faces = body.GetFaces() as object[]; if (faces == null) continue;
                        foreach (var fo in faces)
                        {
                            var face = fo as Face2; if (face == null) continue;
                            var surf = face.GetSurface() as Surface; if (surf == null) continue;
                            bool cyl = false; try { cyl = surf.IsCylinder(); } catch { }
                            if (!cyl) continue;
                            double[] cp = null; try { cp = surf.CylinderParams as double[]; } catch { }
                            if (cp == null || cp.Length < 7) continue;
                            if (best != null && cp[6] <= best.R) continue;
                            var op = (MathPoint)((MathPoint)mu.CreatePoint(new[] { cp[0], cp[1], cp[2] })).MultiplyTransform(xf);
                            var dv = (MathVector)((MathVector)mu.CreateVector(new[] { cp[3], cp[4], cp[5] })).MultiplyTransform(xf);
                            double[] oa = op.ArrayData as double[]; double[] da = dv.ArrayData as double[];
                            double dl = Norm(da); if (dl < 1e-9) continue;
                            best = new AxisCyl { R = cp[6], O = new[] { oa[0], oa[1], oa[2] }, D = new[] { da[0] / dl, da[1] / dl, da[2] / dl } };
                        }
                    }
                }
                catch { }
            }
            return best;
        }

        private static double[] RotateAroundAxis(double[] v, double[] d, double theta)
        {
            double ct = Math.Cos(theta), st = Math.Sin(theta);
            double[] cross = Cross(d, v);
            return new[] {
                v[0] * ct + cross[0] * st,
                v[1] * ct + cross[1] * st,
                v[2] * ct + cross[2] * st
            };
        }

        private static double[] Cross(double[] a, double[] b) => new[] {
            a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]
        };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Norm(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        private static double[] Scale(double[] v, double s) => new[] { v[0] * s, v[1] * s, v[2] * s };

        // ---------- misc ----------
        private static string SafeName(Component2 c) { try { return c.Name2; } catch { return null; } }
        private static string SafeName(Feature f) { try { return f?.Name; } catch { return null; } }
        private static string SafePath(Component2 c) { try { return c.GetPathName(); } catch { return null; } }
        private static string Trim(double v) => v.ToString("0.###");

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

        private static void RemoveExtraInstances(IModelDoc2 model, AssemblyDoc asm, string seedPath, int keepCount)
        {
            try
            {
                object[] comps = asm.GetComponents(false) as object[];
                var mine = new List<Component2>();
                foreach (var o in comps ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                    string p = null; try { p = c.GetPathName(); } catch { }
                    if (string.Equals(p, seedPath, StringComparison.OrdinalIgnoreCase)) mine.Add(c);
                }
                model.ClearSelection2(true);
                for (int i = keepCount; i < mine.Count; i++) { try { mine[i].Select4(false, null, false); } catch { } }
                try { model.EditDelete(); } catch { }
                model.ClearSelection2(true);
            }
            catch { }
        }

        private static int SafeWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
