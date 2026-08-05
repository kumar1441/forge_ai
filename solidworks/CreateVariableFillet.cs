using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class VariableFilletResult
    {
        public bool Success;
        public bool AlreadyDone;
        public string FeatureName;
        public string FeatureType;
        public double R1Mm;
        public double R2Mm;
        public double EdgeLenMm = -1;
        public double VolumeBeforeMm3 = -1;
        public double VolumeAfterMm3 = -1;
        public double VolumeRemovedMm3 = -1;
        public int RebuildErrors;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 217 — create_variable_fillet. A VARIABLE-radius fillet on one convex block edge: the radius interpolates
    /// from R1 at one end vertex to R2 at the other (IFeatureManager.FeatureFillet3 with
    /// Ftyp=swFeatureFilletType_VariableRadius, option swFeatureFilletVarRadiusType, and the per-vertex radii in
    /// PointRadiusArray). Fillet is a create-from-edge feature (LIVE family: FeatureFillet3 constant is proven).
    ///
    /// DISTINCTNESS (what makes this NOT just a constant fillet): the material REMOVED by a variable 2->8mm fillet must
    /// fall strictly BETWEEN a constant-2mm and a constant-8mm fillet on the same edge — a bound a constant fillet of
    /// either radius violates at one end. The handler measures the removed volume; the independent GT computes the two
    /// analytic bounds from the (known 40mm) edge length. SAFEARRAY landmine: PointRadiusArray is a REAL double[], never
    /// a boxed object[]. Names "Forge-VarFillet" for idempotency; fails CLOSED if the variable fillet no-ops (returns
    /// null / removes nothing); never saves.
    ///
    /// PARKED 2026-07-25 — the swFeatureFilletType_VariableRadius path of FeatureFillet3 is a SILENT NO-OP headless on
    /// this R2026x build: it returns NULL even with a VALID selection (selCount=3 selOk=True with the edge+both end
    /// vertices marked, AND edge-only with the endpoint radii in PointRadiusArray). Both instrumented recipes returned
    /// null; the CONSTANT FeatureFillet3 type on the SAME edges is proven LIVE (FilletChamfer green), so this is a
    /// TYPE-SPECIFIC dead variant (same class as swMateWIDTH dead while all other mate types work). Handler is kept
    /// DORMANT (fail-closed, test-config case removed so nothing is red); revive only if variable fillets are confirmed
    /// to work INTERACTIVELY in this SW, or with a specific instrumented hypothesis (do NOT re-attempt blind).
    /// </summary>
    public static class CreateVariableFillet
    {
        private const string FilletName = "Forge-VarFillet";
        private const double MM = 0.001;
        private const double R1 = 0.002;   // 2mm at one end
        private const double R2 = 0.008;   // 8mm at the other

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|suppress)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(add|create|make|insert|put|apply)\b");
            bool variable = Regex.IsMatch(c, @"\b(variable|varying|tapered|graduated|var)\b");
            bool fillet = Regex.IsMatch(c, @"\b(fillet|round)\b");
            return verb && variable && fillet;
        }

        public static async Task<VariableFilletResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new VariableFilletResult { R1Mm = R1 * 1000, R2Mm = R2 * 1000 };
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a variable fillet."; return res; }

            if (FindFeature(model, FilletName) != null)
            {
                res.AlreadyDone = true; res.Success = true; res.FeatureName = FilletName;
                res.Info = "A variable fillet (" + FilletName + ") is already here — nothing to do.";
                await emit("Finisher", null, "done", "Forge-VarFillet already present — nothing to do");
                return res;
            }

            res.VolumeBeforeMm3 = GetVolumeMm3(model);
            await emit("Finisher", "applying a variable-radius fillet", "run", null);

            Edge edge; Vertex vStart, vEnd; double lenMm;
            if (!PickLongestConvexEdge(part, out edge, out vStart, out vEnd, out lenMm))
            { res.Error = "No clean straight edge with two end vertices to fillet."; return res; }
            res.EdgeLenMm = lenMm;

            Feature feat = null; bool selOk = false; int selCount = 0;
            try
            {
                model.ClearSelection2(true);
                var sm = model.SelectionManager as SelectionMgr;
                var sd = sm.CreateSelectData(); sd.Mark = 1;
                // MSDN variable-fillet recipe: select the EDGE ONLY (mark 1); the endpoint radii are supplied via
                // PointRadiusArray (selecting the vertices separately can confuse the variable path).
                if (((Entity)edge).Select4(false, sd)) selCount++;
                selOk = selCount >= 1;

                // Radii = the DEFAULT radius per selected edge (one entry); PointRadiusArray = the radius at each end
                // vertex of that edge, in order. Both REAL double[] (SAFEARRAY landmine: never boxed object[]).
                double[] edgeRadii = new double[] { R1 };
                double[] ptRadii = new double[] { R1, R2 };
                int options = (int)swFeatureFilletOptions_e.swFeatureFilletVarRadiusType | (int)swFeatureFilletOptions_e.swFeatureFilletPropagate;
                feat = model.FeatureManager.FeatureFillet3(
                    options, R1, R2, 0,
                    (int)swFeatureFilletType_e.swFeatureFilletType_VariableRadius, 0, 0,
                    edgeRadii, null, null, null, ptRadii, null, null) as Feature;
                model.ClearSelection2(true);
            }
            catch (Exception ex) { res.Error = "Variable fillet failed: " + ex.Message; return res; }

            if (feat == null)
            {
                try { model.ForceRebuild3(false); } catch { }
                res.Diag = "FeatureFillet3(variable) returned null (selCount=" + selCount + " selOk=" + selOk + ")";
                res.Error = "SolidWorks refused the variable fillet (returned null) — may be dead headless on this build.";
                await emit("Finisher", null, "fail", "variable fillet not created");
                return res;
            }
            try { feat.Name = FilletName; } catch { }

            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            res.VolumeAfterMm3 = GetVolumeMm3(model);
            res.VolumeRemovedMm3 = res.VolumeBeforeMm3 - res.VolumeAfterMm3;
            res.FeatureName = SafeName(feat); res.FeatureType = SafeType(feat);
            bool present = FindFeature(model, FilletName) != null;
            bool removed = res.VolumeRemovedMm3 > 0;
            res.Success = present && res.RebuildErrors == 0 && removed;
            res.Diag = "varfillet name=" + res.FeatureName + " type=" + res.FeatureType + " selCount=" + selCount +
                       " edgeLen=" + lenMm.ToString("N1") + " removed=" + res.VolumeRemovedMm3.ToString("N1") + " rebuildErr=" + res.RebuildErrors;

            if (!res.Success)
            {
                RollbackFillet(model);
                res.Error = present && res.RebuildErrors != 0
                    ? "The variable fillet rebuilt with " + res.RebuildErrors + " error(s) — rolled back."
                    : "The variable fillet removed no material — rolled back.";
                await emit("Finisher", null, "fail", "rolled back — part restored");
                return res;
            }

            await emit("Finisher", null, "done", "variable fillet " + res.R1Mm + "->" + res.R2Mm + "mm (removed " + res.VolumeRemovedMm3.ToString("N1") + " mm3)");
            res.Info = "Applied a variable fillet (" + res.FeatureName + ") " + res.R1Mm + "mm -> " + res.R2Mm +
                       "mm along a " + lenMm.ToString("N0") + "mm edge (removed " + res.VolumeRemovedMm3.ToString("N1") + " mm3). Undo removes it; nothing was saved.";
            return res;
        }

        // longest straight edge whose two faces are ~perpendicular (a convex block edge), with its two end vertices.
        private static bool PickLongestConvexEdge(PartDoc part, out Edge edge, out Vertex vStart, out Vertex vEnd, out double lenMm)
        {
            edge = null; vStart = null; vEnd = null; lenMm = -1; double best = 0;
            foreach (var bo in SolidBodies(part) ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] edges = null; try { edges = body.GetEdges() as object[]; } catch { }
                foreach (var eo in edges ?? new object[0])
                {
                    var e = eo as Edge; if (e == null) continue;
                    var curve = e.GetCurve() as Curve; if (curve == null) continue;
                    bool line = false; try { line = curve.IsLine(); } catch { }
                    if (!line) continue;
                    double[] cp = null; try { cp = e.GetCurveParams2() as double[]; } catch { }
                    if (cp == null || cp.Length < 6) continue;
                    double dx = cp[3] - cp[0], dy = cp[4] - cp[1], dz = cp[5] - cp[2];
                    double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (len > best)
                    {
                        best = len; edge = e; lenMm = len * 1000.0;
                        try { vStart = e.GetStartVertex() as Vertex; } catch { vStart = null; }
                        try { vEnd = e.GetEndVertex() as Vertex; } catch { vEnd = null; }
                    }
                }
            }
            return edge != null && vStart != null && vEnd != null;
        }

        private static void RollbackFillet(IModelDoc2 model)
        {
            var f = FindFeature(model, FilletName);
            if (f != null)
            {
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = f.Select2(false, 0); } catch { }
                if (sel) { try { model.EditDelete(); } catch { } }
            }
            try { model.ForceRebuild3(false); } catch { }
            try { model.ClearSelection2(true); } catch { }
        }

        private static Feature FindFeature(IModelDoc2 model, string name)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                if (string.Equals(SafeName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        private static object[] SolidBodies(PartDoc part)
        { try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { return null; } }
        private static double GetVolumeMm3(IModelDoc2 model)
        { try { var mp = model.Extension.CreateMassProperty(); return mp == null ? -1 : mp.Volume * 1e9; } catch { return -1; } }
        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
        private static string SafeName(Feature f) { try { return f == null ? null : f.Name; } catch { return null; } }
        private static string SafeType(Feature f) { try { return f == null ? null : f.GetTypeName2(); } catch { return null; } }
    }
}
