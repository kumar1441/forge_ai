using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CircularPatternComponentResult
    {
        public string SeedComponent;
        public int Count;              // requested total instances (including any already present)
        public double AngleDeg;        // total span, degrees (default 360 = full circle)
        public int InstancesAdded;
        public int ExpectedInstances;  // Count - existing-instance-count
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
    /// CircularPatternComponent (tool 42, "circular_pattern_components") — a WRITE handler that patterns ONE
    /// named assembly component around an axis, with count + angle span given DIRECTLY by the user (never
    /// derived from an existing hole ring — that's PatternComponent.cs's fastener-specific job).
    ///
    /// LANDMINE (instrumented, not guessed — a second confirmation of tool 41's finding): calling
    /// `IFeatureManager.FeatureCircularPattern4` with a Component2 seed (the same 1=axis/4=seed selection-mark
    /// scheme this codebase's OTHER circular-pattern code uses) also returns a NULL feature on this build with
    /// confirmed-good selection (axisSel=True seedSel=True selCount=2, axis correctly resolved to the plate's own
    /// 60mm rim). A first attempt on `flange-suppressed.SLDASM` looked like a DIFFERENT bug (the plate's own
    /// `GetBodies3` returned null bodies — traced to a STALE pre-generated fixture file with a bodyless plate,
    /// fixed by re-running `gen-flange-suppressed.ps1`), but after that fix the pattern call still no-ops. Not
    /// chased further with a live-interactive check; treated as the same COMPONENT-mode dead class tool 41 hit.
    ///
    /// WORKING ROUTE INSTEAD (identical shape to tool 41): `AssemblyDoc.AddComponent5` to insert N-1 more
    /// instances of the seed's file, each ROTATED about the resolved axis by an even angular step (Rodrigues'
    /// rotation of the seed's own radial offset from the axis — translation only, the seed's own rotation matrix
    /// is preserved unchanged for every copy, correct for the common axially-symmetric case — bolts, pins,
    /// dowels — and an honest, documented scope limit for anything else, matching tool 41's translation-only
    /// note). Positions/axis are read via the proven `Component2.Transform2.ArrayData` primitive
    /// (`MoveComponent.cs`). No single pattern feature exists to Ctrl+Z as a unit — each insert is its own undo
    /// step; Forge never saves. Idempotent: if the seed's file already has >= the requested count, nothing added.
    ///
    /// Axis: the LARGEST-radius cylindrical face found on any OTHER (non-seed) component (a hub/boss/rim is
    /// almost always the biggest cylinder in a small assembly, and is never the thing being copied — the same
    /// "don't lean on the seed's own geometry" caution tool 41 learned). Component resolution reuses
    /// SelectComponent's normalized-name matcher (exact-first, substring fallback).
    /// </summary>
    public static class CircularPatternComponent
    {
        private const double MM = 0.001;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // must be a CIRCULAR component pattern: "circular(ly) pattern" (never bare "pattern"/"linear pattern"),
            // naming a component, with an explicit count — disjoint from PatternComponent's broad bolt-hole-ring
            // vocabulary (no "hole"/"fill"/"every hole" words here).
            if (!Regex.IsMatch(c, @"\bcircular(ly)?\s*pattern\b")) return false;
            if (Regex.IsMatch(c, @"\b(every\s+hole|each\s+hole|fill\s+the\s+holes?)\b")) return false;
            if (!Regex.IsMatch(c, @"\bcomponent\b") && !Regex.IsMatch(c, "[\"']([^\"']{2,})[\"']")) return false;
            return Regex.IsMatch(c, @"\b\d+\s*(times|instances|copies)\b");
        }

        public static async Task<CircularPatternComponentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CircularPatternComponentResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to pattern a component."; return res; }

            await emit("Gauge", "reading the assembly", "run", null);

            int count = ParseCount(intent);
            if (count < 2)
            { res.Error = "How many total instances? Say a count like \"4 times\" (need at least 2)."; await emit("Gauge", null, "fail", res.Error); return res; }
            double angleDeg = ParseAngleDeg(intent);
            res.Count = count; res.AngleDeg = angleDeg;

            string query = ParseComponentName(intent);
            if (string.IsNullOrEmpty(query))
            { res.Error = "Which component? Name it (e.g. \"circularly pattern the bolt component 4 times\")."; await emit("Gauge", null, "fail", res.Error); return res; }

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

            int existingCount = CountByPath(comps, seedPath);
            if (existingCount >= count)
            {
                res.AlreadyPatterned = true;
                res.Info = "Already " + existingCount + " instance(s) of " + res.SeedComponent + " present (>= the requested " + count + ") — nothing to do.";
                await emit("Gauge", null, "done", res.Info);
                return res;
            }

            var mu = (MathUtility)app.GetMathUtility();

            // ---- axis: the largest-radius cylindrical face on any OTHER component ----
            AxisCyl axis = FindAxisCylinder(mu, seed, comps);
            if (axis == null)
            {
                res.Error = "Couldn't find a cylindrical face (a hub/boss/bore) elsewhere in the assembly to pattern '" + res.SeedComponent + "' around — add one and re-run.";
                await emit("Gauge", null, "fail", res.Error); return res;
            }

            double[] seedXf = null; try { seedXf = seed.Transform2.ArrayData as double[]; } catch { }
            if (seedXf == null || seedXf.Length < 12)
            { res.Error = "Couldn't read '" + res.SeedComponent + "'s placement transform."; await emit("Gauge", null, "fail", res.Error); return res; }
            double[] seedPos = { seedXf[9], seedXf[10], seedXf[11] };

            // decompose seed position relative to the axis: along-axis component + radial (perpendicular) offset
            double[] w = Sub(seedPos, axis.O);
            double along = Dot(w, axis.D);
            double[] radial = Sub(w, Scale(axis.D, along));
            if (Norm(radial) < 1e-6)
            {
                res.Error = "'" + res.SeedComponent + "' sits ON the pattern axis — no radial offset to rotate around.";
                await emit("Gauge", null, "fail", res.Error); return res;
            }

            res.ExpectedInstances = count - existingCount;
            double stepRad = (angleDeg * Math.PI / 180.0) / count;
            await emit("Gauge", null, "done",
                "seed " + res.SeedComponent + " · " + existingCount + "→" + count + " instances over " + angleDeg + "° → adding " + res.ExpectedInstances + " new cop" + (res.ExpectedInstances == 1 ? "y" : "ies"));

            // =================== Stamp: insert the new instances around the axis ===================
            await emit("Stamp", "inserting the patterned copies", "run", null);
            int oe = 0, ow = 0; try { app.OpenDoc6(seedPath, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow); } catch { }
            try { model.ClearSelection2(true); } catch { }

            for (int i = existingCount; i < count; i++)
            {
                double theta = stepRad * i;
                double[] rotated = RotateAroundAxis(radial, axis.D, theta);
                double[] pos = Add(Add(axis.O, Scale(axis.D, along)), rotated);
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
            res.Diag = "existing=" + existingCount + " requested=" + count + " added=" + res.InstancesAdded + " overDefined=" + over + " rebuildErr=" + res.RebuildErrors;

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

            res.Info = "Patterned " + res.SeedComponent + " into " + count + " total instances (" + res.InstancesAdded +
                       " new, over " + angleDeg + "°) — no over-define, rebuild clean. Forge never saves.";
            await emit("Sentinel", null, "done", res.InstancesAdded + " instance(s) verified in place · rebuild clean");
            return res;
        }

        // ---------- parsing ----------
        private static int ParseCount(string intent)
        {
            var m = Regex.Match((intent ?? "").ToLowerInvariant(), @"\b(\d+)\s*(times|instances|copies)\b");
            int n;
            if (m.Success && int.TryParse(m.Groups[1].Value, out n)) return n;
            return 0;
        }

        private static double ParseAngleDeg(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\bfull\s+circle\b")) return 360.0;
            var m = Regex.Match(c, @"(\d+(\.\d+)?)\s*(deg|degrees?|°)");
            double v;
            if (m.Success && double.TryParse(m.Groups[1].Value, out v) && v > 0) return v;
            return 360.0;   // default: spread evenly across the full circle
        }

        private static string ParseComponentName(string intent)
        {
            string raw = intent ?? "";
            var qm = Regex.Match(raw, "[\"']([^\"']{2,})[\"']");
            if (qm.Success) return qm.Groups[1].Value.Trim();
            var m = Regex.Match(raw, @"pattern\s+(?:the\s+)?(.+?)\s+component\b", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return null;
        }

        // ---- axis: largest-radius cylindrical face on any component OTHER than the seed, origin+direction in
        //      assembly space (same MultiplyTransform pattern LinearPatternComponent's CollectEdges uses). ----
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

        // Rodrigues' rotation of a vector already perpendicular to axis d (unit) by angle theta
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
        private static double Dot(double[] a, double[] b) => a[0]*b[0]+a[1]*b[1]+a[2]*b[2];
        private static double Norm(double[] v) => Math.Sqrt(v[0]*v[0]+v[1]*v[1]+v[2]*v[2]);
        private static double[] Sub(double[] a, double[] b) => new[] { a[0]-b[0], a[1]-b[1], a[2]-b[2] };
        private static double[] Add(double[] a, double[] b) => new[] { a[0]+b[0], a[1]+b[1], a[2]+b[2] };
        private static double[] Scale(double[] v, double s) => new[] { v[0]*s, v[1]*s, v[2]*s };

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
