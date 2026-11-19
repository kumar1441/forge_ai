using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class LinearPatternComponentResult
    {
        public string SeedComponent;
        public int Count;              // requested total instances (including any already present)
        public double SpacingMm;
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
    /// LinearPatternComponent (tool 41, "linear_pattern_components") — a WRITE handler that patterns ONE named
    /// assembly component along a straight-line direction, with count + spacing given DIRECTLY by the user
    /// (never derived from an existing hole ring — that's PatternComponent.cs's fastener-specific job, a
    /// different tool this one must never shadow or be shadowed by).
    ///
    /// LANDMINE (instrumented, not guessed): calling `IFeatureManager.FeatureLinearPattern4` with a COMPONENT
    /// (Component2) as the seed — same selection-mark scheme (1=direction, 4=seed) proven live for
    /// PatternComponent.cs's CIRCULAR component pattern — returns a NULL feature every time on this R2026x
    /// build, even with confirmed-good selection (edgeSel=True seedSel=True selCount=2). Same "returns
    /// null/no-op" class as InsertMoveCopyBody2/InsertCombineFeature, except FeatureLinearPattern4 itself is
    /// proven LIVE at the FEATURE level (PatternFeature.cs, GREEN) — so it's specifically the COMPONENT-mode of
    /// the LINEAR variant that's dead here (circular component-mode is fine). Do NOT re-attempt that path blind.
    ///
    /// WORKING ROUTE INSTEAD: `AssemblyDoc.AddComponent5` (proven live — InsertComponent.cs, tool 29) to insert
    /// N-1 more instances of the seed's OWN file at seed-origin + i·spacing·direction (assembly-space metres,
    /// read/written via the proven `Component2.Transform2.ArrayData` translation slots [9..11] — the same
    /// primitive MoveComponent.cs already relies on). Each insert lands at the seed's default orientation
    /// (matching the seed when it sits at identity rotation, the common unmated-component case); a seed placed
    /// with a custom rotation is an honest, undocumented-for-now scope gap, same shape as this codebase's other
    /// documented "translation only" notes. Direction: an explicit "along X/Y/Z" word resolves to the global
    /// axis (a straight edge on any OTHER component parallel to it, else the seed's own longest edge); otherwise
    /// the auto-picked longest straight edge in the assembly (excluding the seed) sets the line.
    ///
    /// Component resolution reuses SelectComponent's normalized-name matcher (exact-first, substring fallback,
    /// ambiguous ties asked not guessed — Rule #2). No single pattern feature exists to Ctrl+Z as a unit — each
    /// insert is its own undo step, same shape as InsertComponent.cs; Forge never saves. Idempotent: if the
    /// seed's file already has >= the requested count of instances, nothing is added.
    /// </summary>
    public static class LinearPatternComponent
    {
        private const double MM = 0.001; // mm -> SW metres

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // must be a LINEAR component pattern: "linear(ly) pattern" (never bare "pattern"/"circular pattern"),
            // naming a component, with an explicit count AND a spacing value — disjoint from PatternComponent's
            // broad bolt-hole-ring vocabulary (no "hole"/"bolt circle"/"fill"/"around" words here).
            if (!Regex.IsMatch(c, @"\blinear(ly)?\s*pattern\b")) return false;
            if (Regex.IsMatch(c, @"\b(circular|bolt\s*circle|around\s+the|every\s+hole|each\s+hole)\b")) return false;
            if (!Regex.IsMatch(c, @"\bcomponent\b") && !Regex.IsMatch(c, "[\"']([^\"']{2,})[\"']")) return false;
            if (!Regex.IsMatch(c, @"\b\d+\s*(times|instances|copies)\b")) return false;
            return Regex.IsMatch(c, @"\d+(\.\d+)?\s*(mm|millimeters?|millimetres?|cm|centimeters?|in\b|inch(es)?)");
        }

        public static async Task<LinearPatternComponentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new LinearPatternComponentResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to pattern a component."; return res; }

            await emit("Gauge", "reading the assembly", "run", null);

            int count = ParseCount(intent);
            if (count < 2)
            { res.Error = "How many total instances? Say a count like \"4 times\" (need at least 2)."; await emit("Gauge", null, "fail", res.Error); return res; }
            double spacingMm = ParseSpacingMm(intent);
            if (spacingMm <= 0)
            { res.Error = "What spacing? Say a distance like \"20mm apart\"."; await emit("Gauge", null, "fail", res.Error); return res; }
            res.Count = count; res.SpacingMm = spacingMm;

            string query = ParseComponentName(intent);
            if (string.IsNullOrEmpty(query))
            { res.Error = "Which component? Name it (e.g. \"linearly pattern the tab component 4 times, 20mm apart\")."; await emit("Gauge", null, "fail", res.Error); return res; }

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

            // ---- direction: explicit X/Y/Z axis word wins, else the longest straight edge in the assembly
            //      (excluding the seed — see the FindDirection docstring for why the seed's own edges are the
            //      last resort here too, matching the FeatureLinearPattern4 selection lesson above). ----
            double[] axisWanted = ParseAxis(intent);
            double[] dir;
            if (!FindDirection(mu, seed, comps, axisWanted, out dir))
            {
                string axisMsg = axisWanted != null ? "parallel to the requested axis " : "";
                res.Error = "Couldn't find a straight edge " + axisMsg + "to set the pattern direction — add a straight edge and re-run.";
                await emit("Gauge", null, "fail", res.Error); return res;
            }

            double[] seedXf = null; try { seedXf = seed.Transform2.ArrayData as double[]; } catch { }
            if (seedXf == null || seedXf.Length < 12)
            { res.Error = "Couldn't read '" + res.SeedComponent + "'s placement transform."; await emit("Gauge", null, "fail", res.Error); return res; }
            double[] origin = { seedXf[9], seedXf[10], seedXf[11] };

            res.ExpectedInstances = count - existingCount;
            await emit("Gauge", null, "done",
                "seed " + res.SeedComponent + " · " + existingCount + "→" + count + " instances, " + spacingMm + "mm spacing → adding " + res.ExpectedInstances + " new cop" + (res.ExpectedInstances == 1 ? "y" : "ies"));

            // =================== Stamp: insert the new instances along the line ===================
            await emit("Stamp", "inserting the patterned copies", "run", null);
            int oe = 0, ow = 0; try { app.OpenDoc6(seedPath, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow); } catch { }
            try { model.ClearSelection2(true); } catch { }

            for (int i = existingCount; i < count; i++)
            {
                double[] pos = {
                    origin[0] + dir[0] * spacingMm * MM * i,
                    origin[1] + dir[1] * spacingMm * MM * i,
                    origin[2] + dir[2] * spacingMm * MM * i
                };
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
                // best-effort cleanup: trim back any instances beyond the ORIGINAL count we just added
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
                       " new, " + spacingMm + "mm apart) — no over-define, rebuild clean. Forge never saves.";
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

        private static double ParseSpacingMm(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            double v;
            var m = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm");
            if (m.Success && double.TryParse(m.Groups[1].Value, out v) && v > 0) return v;
            m = Regex.Match(c, @"(\d+(\.\d+)?)\s*(cm|centimeters?|centimetres?)");
            if (m.Success && double.TryParse(m.Groups[1].Value, out v) && v > 0) return v * 10.0;
            m = Regex.Match(c, @"(\d+(\.\d+)?)\s*(inch|inches|in\b)");
            if (m.Success && double.TryParse(m.Groups[1].Value, out v) && v > 0) return v * 25.4;
            return 0;
        }

        private static string ParseComponentName(string intent)
        {
            string raw = intent ?? "";
            var qm = Regex.Match(raw, "[\"']([^\"']{2,})[\"']");
            if (qm.Success) return qm.Groups[1].Value.Trim();
            // "pattern the <name> component" / "linearly pattern <name> component"
            var m = Regex.Match(raw, @"pattern\s+(?:the\s+)?(.+?)\s+component\b", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return null;
        }

        private static double[] ParseAxis(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\balong\s+x\b") || Regex.IsMatch(c, @"\bx[\s-]?axis\b")) return new[] { 1.0, 0.0, 0.0 };
            if (Regex.IsMatch(c, @"\balong\s+y\b") || Regex.IsMatch(c, @"\by[\s-]?axis\b")) return new[] { 0.0, 1.0, 0.0 };
            if (Regex.IsMatch(c, @"\balong\s+z\b") || Regex.IsMatch(c, @"\bz[\s-]?axis\b")) return new[] { 0.0, 0.0, 1.0 };
            return null;
        }

        // ---- direction: a unit vector, preferring a straight edge on ANOTHER (non-seed) component — mirrors
        //      the same "don't lean on the seed's own geometry" caution the dead FeatureLinearPattern4 selection
        //      path taught, kept here defensively even though this route no longer selects an Entity at all.
        //      Explicit axis word -> first parallel edge found this way; else -> the longest one overall. ----
        private static bool FindDirection(MathUtility mu, Component2 seed, object[] allComps, double[] axisWanted, out double[] dir)
        {
            dir = null;
            var others = new List<LinEdge>();
            foreach (var o in allComps ?? new object[0])
            {
                var c = o as Component2; if (c == null || c == seed) continue;
                others.AddRange(CollectEdges(mu, c));
            }
            var seedEdges = CollectEdges(mu, seed);

            if (axisWanted != null)
            {
                foreach (var e in others)
                    if (Math.Abs(Math.Abs(Dot(e.D, axisWanted)) - 1.0) < 1e-2) { dir = e.D; return true; }
                foreach (var e in seedEdges)
                    if (Math.Abs(Math.Abs(Dot(e.D, axisWanted)) - 1.0) < 1e-2) { dir = e.D; return true; }
                return false;
            }
            LinEdge best = null; double bestLen = -1;
            foreach (var e in others) if (e.Len > bestLen) { bestLen = e.Len; best = e; }
            if (best == null) foreach (var e in seedEdges) if (e.Len > bestLen) { bestLen = e.Len; best = e; }
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

        // component-local edge length (unaffected by the component's rigid placement transform, so no need to
        // transform it) — same GetCurveParams2 endpoint-distance shape as GetEdges.cs's own EdgeLength.
        private static double EdgeLength(Edge edge)
        {
            try
            {
                var cp = edge.GetCurveParams2() as double[];   // [sx,sy,sz, ex,ey,ez, sParam, eParam]
                if (cp == null || cp.Length < 6) return 0;
                double dx = cp[3] - cp[0], dy = cp[4] - cp[1], dz = cp[5] - cp[2];
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            catch { return 0; }
        }

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Norm(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

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

        // rollback: delete instances of seedPath beyond keepCount (newest-first is not guaranteed by traversal
        // order, so this trims by COUNT, not by which ones were just added — acceptable for a same-session,
        // never-saved rollback where every instance of the seed file is otherwise identical).
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
