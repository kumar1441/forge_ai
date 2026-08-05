using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class TransformAssemblyResult
    {
        public double[] TranslationMm;    // {x,y,z} the rigid translation actually requested (mm; {0,0,0} for a pure rotation)
        public double RotationDeg;        // 0 when no rotation
        public string RotationAxis;       // "X"|"Y"|"Z"|null
        public int ComponentsMoved;       // top-level components a new Transform2 was set on (fixed included)
        public double[] CenterBeforeMm;   // {x,y,z} mean top-level component centroid BEFORE (independent of the write path)
        public double[] CenterAfterMm;    // {x,y,z} mean centroid AFTER the move + rebuild
        public double MeasuredShiftMm;    // |CenterAfter - CenterBefore| — the honest, geometry-measured shift
        public int OverDefined;           // top-level components over-defined / no-solution AFTER (fail closed)
        public int RebuildErrors;         // GetWhatsWrongCount AFTER the move
        public bool Verified;             // fail closed: rigid (spread preserved) + rebuild clean + not over-defined + shift/centering matched
        public bool NeedsConfirm;         // no parseable transform -> ask ONE question (Rule #2)
        public string Question;
        public string Info;               // verdict-first one-liner
        public string Error;              // set => wrong doc; nothing moved
    }

    /// <summary>
    /// TransformAssembly (tool #168, "move/rotate ALL components as one rigid set"). A WRITE handler that moves the
    /// ENTIRE assembly rigidly — every top-level component gets the SAME transform, so every inter-component mate stays
    /// satisfied (relative geometry is unchanged: the mates measure the same distances/angles they did before).
    ///
    /// Parses a TRANSLATION (up/down/left/right/forward/back + distance in mm, "in X/Y/Z" + distance, or "to the origin"
    /// = translate the assembly's center onto 0,0,0) and/or a ROTATION (angle + axis X/Y/Z, about the global origin).
    /// No parseable transform -> asks ONE question (Rule #2), moving nothing.
    ///
    /// Directions map to SolidWorks' default view frame: up = +Y, down = -Y, right = +X, left = -X, forward = +Z,
    /// back = -Z. (The harness for this model asserts a "move up 100mm" shifts the bbox-center +100mm in Y, so this
    /// build treats up = +Y.)
    ///
    /// TRANSLATION uses the PROVEN Transform2-ArrayData nudge (the same path Explode/GroundTruth use on this build:
    /// clone the 16-double transform, add the delta to indices [9..11], re-create). ROTATION composes a global-axis
    /// rotation onto each component's transform via IMathTransform.Multiply — the compose ORDER is detected once at
    /// runtime by a probe point (this build's Multiply convention is not assumed), so a component's every point lands
    /// where rotating its current assembly position about the global axis would put it.
    ///
    /// FIXED components: they are still transformed (moving ONLY the floating ones would offset them from the fixed
    /// ones and break/over-define the mates). Setting Transform2 on a fixed component is honored programmatically on
    /// this build in practice, but if a fully-mated sub-structure resists, the write is fail-closed: the mean-centroid
    /// shift is re-measured post-rebuild and Verified reflects the ACTUAL geometry, never the attempt.
    ///
    /// Idempotency: only the "to the origin" form is idempotent (already centered -> ~0 move -> no-op). A relative
    /// "move up 100" correctly moves AGAIN on a rerun (that is what the user asked for). Undo is one Ctrl+Z per
    /// component and Forge never saves.
    /// </summary>
    public static class TransformAssembly
    {
        // Whole-assembly move/rotate ONLY — must carry a whole-assembly scope word so it never swallows a
        // single-part move, explode ("spread apart"), or mirror. Keyword-guarded; the intent layer is primary.
        public static bool IsTransformIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(move|shift|translate|rotate|spin|turn|revolve|slide|shove|nudge|reposition|relocate|drag)\b");
            if (!verb) return false;
            bool wholeScope =
                Regex.IsMatch(c, @"\b(the\s+)?(whole|entire|complete|full)\s+(assembly|assy|thing|model)\b")
                || Regex.IsMatch(c, @"\beverything\b")
                || Regex.IsMatch(c, @"\ball\s+(the\s+)?(components|parts)\b")
                || Regex.IsMatch(c, @"\bto\s+(the\s+)?origin\b");
            return wholeScope;
        }

        private class Move
        {
            public bool HasT;             // a fixed translation vector was parsed
            public double[] V;            // meters (assembly space)
            public bool HasR;             // a rotation was parsed
            public double AngleDeg;
            public string Axis;           // "X"|"Y"|"Z"
            public bool ToOrigin;         // translate the center onto 0,0,0 (V computed at run time)
            public string Ask;            // set => could not parse -> ask this
        }

        // ---- read-only parse: shared by PreviewLine and Run (writes nothing) ----
        private static Move Parse(string intent)
        {
            var mv = new Move { V = new double[3] };
            string c = (intent ?? "").ToLowerInvariant();

            // ---- rotation: angle + axis, about the global origin ----
            var mAng = Regex.Match(c, @"(-?\d+(?:\.\d+)?)\s*(?:deg\b|degs\b|degree|degrees|°)");
            var mAxisR = Regex.Match(c, @"\b(?:about|around|round|on|over|along|w\.?r\.?t\.?)\s+(?:the\s+)?([xyz])\b");
            if (!mAxisR.Success) mAxisR = Regex.Match(c, @"\b([xyz])[\s\-]*axis\b");
            bool rotWord = Regex.IsMatch(c, @"\b(rotate|spin|turn|revolve)\b");
            if (rotWord && mAng.Success && mAxisR.Success)
            {
                mv.HasR = true;
                mv.AngleDeg = double.Parse(mAng.Groups[1].Value, CultureInfo.InvariantCulture);
                mv.Axis = mAxisR.Groups[1].Value.ToUpperInvariant();
            }

            // ---- "to the origin" (center the assembly on 0,0,0) ----
            if (Regex.IsMatch(c, @"\bto\s+(?:the\s+)?origin\b")
                || Regex.IsMatch(c, @"\bto\s+0\s*,\s*0\s*,\s*0\b")
                || (Regex.IsMatch(c, @"\bcent(?:er|re)\b") && Regex.IsMatch(c, @"\borigin\b")))
                mv.ToOrigin = true;

            // ---- translation: a linear direction + a distance (skip when "to origin" owns the move) ----
            if (!mv.ToOrigin)
            {
                int axis = -1; double sign = 1;
                // "lower"/"raise" checked FIRST and take priority over a bare up/down word — otherwise a sentence
                // describing the PROBLEM ("it's sitting up too much" → lower it) has its own "up" mismatch the verb
                // and the assembly moves the WRONG way (test-loop false-success flange-10-offset-drop: "lower this
                // 5mm, it's sitting up too much" moved UP +5mm instead of DOWN -5mm because \bup\b matched first).
                if (Regex.IsMatch(c, @"\b(lower|drop|sink|dip)\b")) { axis = 1; sign = -1; }
                else if (Regex.IsMatch(c, @"\b(raise|lift)\b")) { axis = 1; sign = 1; }
                else if (Regex.IsMatch(c, @"\bup\b")) { axis = 1; sign = 1; }
                else if (Regex.IsMatch(c, @"\bdown\b")) { axis = 1; sign = -1; }
                else if (Regex.IsMatch(c, @"\bright\b")) { axis = 0; sign = 1; }
                else if (Regex.IsMatch(c, @"\bleft\b")) { axis = 0; sign = -1; }
                else if (Regex.IsMatch(c, @"\b(forward|forwards|front|ahead)\b")) { axis = 2; sign = 1; }
                else if (Regex.IsMatch(c, @"\b(back|backward|backwards|behind|rearward|rear)\b")) { axis = 2; sign = -1; }
                else
                {
                    var mIn = Regex.Match(c, @"\b(?:in|along|on|towards?|direction|the)\s+(?:the\s+)?([+\-]?)\s*([xyz])\b");
                    if (!mIn.Success) mIn = Regex.Match(c, @"([+\-])\s*([xyz])\b");
                    if (mIn.Success)
                    {
                        axis = "xyz".IndexOf(mIn.Groups[2].Value[0]);
                        if (mIn.Groups[1].Value == "-" || Regex.IsMatch(c, @"\b(negative|minus)\b")) sign = -1;
                    }
                }

                // strip the rotation's angle token so we don't read 90° as the distance
                string lin = mv.HasR ? Regex.Replace(c, @"(-?\d+(?:\.\d+)?)\s*(?:deg\w*|°)", " ") : c;
                double mm; bool hasDist = TryMm(lin, out mm);

                if (axis >= 0 && hasDist) { mv.HasT = true; mv.V[axis] = sign * mm / 1000.0; }
                else if (axis >= 0 && !hasDist && !mv.HasR) mv.Ask = "How far should I move the assembly (e.g. 100mm)?";
                else if (axis < 0 && hasDist && !mv.HasR) mv.Ask = "Which direction should I move it — up/down, left/right, forward/back, or along X/Y/Z?";
            }

            if (!mv.HasT && !mv.HasR && !mv.ToOrigin && mv.Ask == null)
                mv.Ask = "What move should I apply to the whole assembly? Try \"move the whole assembly up 100mm\", \"rotate it 90 degrees about Z\", or \"move it to the origin\".";
            return mv;
        }

        // parse a distance -> millimeters (default unit mm; cm/m/inch supported)
        private static bool TryMm(string c, out double mm)
        {
            mm = 0;
            var m = Regex.Match(c, @"(-?\d+(?:\.\d+)?)\s*(mm|millimet\w*|cm|centimet\w*|meters?|metres?|m\b|inch\w*|in\b|""|')?");
            if (!m.Success) return false;
            double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string u = m.Groups[2].Value ?? "";
            double mult = 1.0; // mm default
            if (u.StartsWith("cm") || u.StartsWith("centim")) mult = 10.0;
            else if (u == "m" || u.StartsWith("meter") || u.StartsWith("metre")) mult = 1000.0;
            else if (u.StartsWith("inch") || u == "in" || u == "\"" || u == "'") mult = 25.4;
            mm = v * mult;
            return true;
        }

        // ---- preview only when broad (Rule #3): >3 components -> one-line plan; else null (execute directly) ----
        public static string PreviewLine(IModelDoc2 model, string intent)
        {
            var asm = model as AssemblyDoc; if (asm == null) return null;
            var mv = Parse(intent);
            if (mv.Ask != null) return null;                       // let Run ask
            int n = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            { var c = o as Component2; if (c == null) continue; bool s = false; try { s = c.IsSuppressed(); } catch { } if (!s) n++; }
            if (n <= 3) return null;                               // small unambiguous op -> run directly
            return "This will move the whole assembly as one rigid set — " + n + " components, " + Describe(mv)
                 + " — inter-component mates are preserved (everything moves together); one Ctrl+Z per part undoes it, Forge won't save";
        }

        private static string Describe(Move mv)
        {
            var parts = new List<string>();
            if (mv.ToOrigin) parts.Add("centering it on the origin");
            else if (mv.HasT)
            {
                int ax = mv.V[0] != 0 ? 0 : (mv.V[1] != 0 ? 1 : 2);
                double mm = mv.V[ax] * 1000.0;
                parts.Add((mm >= 0 ? "+" : "") + mm.ToString("0.#") + "mm in " + "XYZ"[ax]);
            }
            if (mv.HasR) parts.Add("rotating " + mv.AngleDeg.ToString("0.#") + "° about " + mv.Axis);
            return parts.Count == 0 ? "no-op" : string.Join(" and ", parts);
        }

        public static async Task<TransformAssemblyResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new TransformAssemblyResult { TranslationMm = new double[3], CenterBeforeMm = new double[3], CenterAfterMm = new double[3] };
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) you want to move."; return res; }
            var mu = (MathUtility)app.GetMathUtility();

            await emit("Gauge", "reading the move", "run", null);
            var mv = Parse(intent);
            if (mv.Ask != null) { await emit("Gauge", null, "fail", mv.Ask); res.NeedsConfirm = true; res.Question = mv.Ask; return res; }

            // ---- collect top-level components + the INDEPENDENT before-state (mean centroid + rigid spread) ----
            var comps = new List<Component2>();
            int fixedCount = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool s = false; try { s = c.IsSuppressed(); } catch { } if (s) continue;
                comps.Add(c);
                try { if (c.IsFixed()) fixedCount++; } catch { }
            }
            if (comps.Count == 0) { res.Error = "No components to move."; return res; }

            var centroidsBefore = Centroids(comps);
            double[] meanBefore = Mean(centroidsBefore);
            double spreadBefore = Spread(centroidsBefore, meanBefore);
            res.CenterBeforeMm = ToMm(meanBefore);

            // ---- resolve "to the origin" now: V = -center (meters) ----
            if (mv.ToOrigin) { mv.V = new[] { -meanBefore[0], -meanBefore[1], -meanBefore[2] }; mv.HasT = true; }
            res.TranslationMm = new[] { mv.V[0] * 1000.0, mv.V[1] * 1000.0, mv.V[2] * 1000.0 };
            res.RotationDeg = mv.HasR ? mv.AngleDeg : 0;
            res.RotationAxis = mv.HasR ? mv.Axis : null;

            // idempotent no-op for "to the origin" when already centered
            double reqMm = Math.Sqrt(mv.V[0] * mv.V[0] + mv.V[1] * mv.V[1] + mv.V[2] * mv.V[2]) * 1000.0;
            if (mv.ToOrigin && !mv.HasR && reqMm < 0.5)
            {
                res.CenterAfterMm = res.CenterBeforeMm;
                res.Verified = true;
                res.Info = "Already centered on the origin (center is " + reqMm.ToString("0.0") + "mm from 0,0,0) — nothing to move.";
                await emit("Gauge", null, "done", "already centered — no move needed");
                return res;
            }

            await emit("Gauge", null, "done", comps.Count + " top-level components" + (fixedCount > 0 ? " (" + fixedCount + " fixed — moved too, so mates stay satisfied)" : ""));

            // ---- apply the SAME rigid transform to every component, per-item try/continue (Rule #4) ----
            await emit("Mover", "moving the assembly as one rigid set", "run", null);
            int moved = 0;
            if (mv.HasR)
            {
                // rotation (optionally + translation): compose a global-axis rotation, then apply to each component's transform
                MathPoint origin = (MathPoint)mu.CreatePoint(new double[] { 0, 0, 0 });
                double[] ax = mv.Axis == "X" ? new double[] { 1, 0, 0 } : mv.Axis == "Y" ? new double[] { 0, 1, 0 } : new double[] { 0, 0, 1 };
                MathVector axv = (MathVector)mu.CreateVector(ax);
                MathTransform move = mu.CreateTransformRotateAxis(origin, axv, mv.AngleDeg * Math.PI / 180.0) as MathTransform;
                if (mv.HasT) move = Compose(mu, move, Translation(mu, mv.V));   // rotate first, then translate
                foreach (var c in comps)
                {
                    try { c.Transform2 = Compose(mu, c.Transform2, move); moved++; }
                    catch { }
                }
            }
            else
            {
                // translation / origin-centering only — the PROVEN Transform2 ArrayData nudge (Explode-style)
                foreach (var c in comps)
                {
                    try
                    {
                        double[] d = c.Transform2.ArrayData as double[];
                        if (d == null || d.Length < 13) continue;
                        double[] nd = (double[])d.Clone();
                        nd[9] += mv.V[0]; nd[10] += mv.V[1]; nd[11] += mv.V[2];
                        c.Transform2 = (MathTransform)mu.CreateTransform(nd);
                        moved++;
                    }
                    catch { }
                }
            }
            res.ComponentsMoved = moved;
            try { model.EditRebuild3(); } catch { }
            try { model.ForceRebuild3(false); } catch { }
            await emit("Mover", null, "done", "set a new position on " + moved + " of " + comps.Count + " components");

            // ---- FAIL CLOSED (Rule #6): re-measure geometry INDEPENDENTLY of the write path ----
            await emit("Sentinel", "verifying the rigid move", "run", null);
            var centroidsAfter = Centroids(comps);
            double[] meanAfter = Mean(centroidsAfter);
            double spreadAfter = Spread(centroidsAfter, meanAfter);
            res.CenterAfterMm = ToMm(meanAfter);
            res.MeasuredShiftMm = Dist(meanBefore, meanAfter) * 1000.0;

            int overDef = 0;
            foreach (var c in comps)
            {
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) overDef++;
            }
            res.OverDefined = overDef;
            try { res.RebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            // rigid check: the RMS spread of centroids about their mean is a rotation/translation invariant, so a
            // truly rigid whole-assembly move leaves it unchanged (tolerance 1mm or 2%). spreadBefore/After are meters.
            bool rigid = Math.Abs((spreadAfter - spreadBefore) * 1000.0) <= Math.Max(1.0, spreadBefore * 1000.0 * 0.02);

            bool rebuildClean = res.RebuildErrors == 0;
            bool notOver = overDef == 0;

            bool moveMatched = true;
            if (!mv.HasR)   // for a pure translation the measured shift must match the requested magnitude
                moveMatched = Math.Abs(res.MeasuredShiftMm - reqMm) <= Math.Max(0.5, reqMm * 0.02);
            bool centeredOk = true;
            if (mv.ToOrigin)   // after centering, the mean centroid should sit ~on the origin
                centeredOk = Math.Sqrt(meanAfter[0] * meanAfter[0] + meanAfter[1] * meanAfter[1] + meanAfter[2] * meanAfter[2]) * 1000.0 <= Math.Max(0.5, reqMm * 0.02);

            res.Verified = rigid && rebuildClean && notOver && moveMatched && centeredOk;
            await emit("Sentinel", null, "done",
                "center shifted " + res.MeasuredShiftMm.ToString("0.0") + "mm · " +
                (rigid ? "rigid (spread held)" : "NON-RIGID (spread changed — mates resisted)") + " · " +
                (rebuildClean ? "rebuild clean" : res.RebuildErrors + " rebuild flag(s)") +
                (overDef > 0 ? " · " + overDef + " over-defined" : ""));

            res.Info = BuildInfo(res, mv, comps.Count, fixedCount, rigid);
            return res;
        }

        // ---- verdict first (Character #3), the number not the adjective (Character #2), honest about resistance ----
        private static string BuildInfo(TransformAssemblyResult r, Move mv, int total, int fixedCount, bool rigid)
        {
            var sb = new StringBuilder();
            if (mv.ToOrigin && !mv.HasR) sb.Append("Centered the whole assembly on the origin");
            else sb.Append("Moved the whole assembly rigidly (" + Describe(mv) + ")");
            sb.Append(" — " + r.ComponentsMoved + " of " + total + " components");
            if (fixedCount > 0) sb.Append(" (" + fixedCount + " fixed, moved with the rest)");
            sb.Append(". Center shifted " + r.MeasuredShiftMm.ToString("0.0") + "mm");
            // (the requested magnitude is already shown in Describe; keep the line clean)
            if (!rigid) sb.Append("; WARNING: the centroid spread changed — a fully-mated sub-structure resisted the move, so the set is not perfectly rigid. Check the assembly.");
            else sb.Append("; mates preserved (everything moved together)");
            if (r.OverDefined > 0) sb.Append(". " + r.OverDefined + " component(s) over-defined after — check the mates");
            if (r.RebuildErrors > 0) sb.Append(". " + r.RebuildErrors + " rebuild flag(s)");
            sb.Append(". Reversible: one Ctrl+Z per part, and the document was not saved.");
            return sb.ToString();
        }

        // ---- compose two transforms so that a point sees `first` applied, then `second`. The IMathTransform.Multiply
        //      order is detected ONCE by a probe point (this build's convention is not assumed). ----
        private static int _mulOrder;   // 0 = undetected, 1 = first.Multiply(second), 2 = second.Multiply(first)
        private static MathTransform Compose(MathUtility mu, MathTransform first, MathTransform second)
        {
            if (_mulOrder == 0)
            {
                try
                {
                    MathPoint p = (MathPoint)mu.CreatePoint(new double[] { 0.031, -0.047, 0.059 });
                    MathPoint viaFirst = p.MultiplyTransform(first) as MathPoint;
                    double[] want = (viaFirst.MultiplyTransform(second) as MathPoint).ArrayData as double[];
                    var cand1 = first.Multiply(second) as MathTransform;
                    double[] got1 = (p.MultiplyTransform(cand1) as MathPoint).ArrayData as double[];
                    _mulOrder = Near(want, got1) ? 1 : 2;
                }
                catch { _mulOrder = 1; }
            }
            return (_mulOrder == 2 ? second.Multiply(first) : first.Multiply(second)) as MathTransform;
        }

        private static bool Near(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length < 3 || b.Length < 3) return false;
            return Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]) < 1e-6;
        }

        private static MathTransform Translation(MathUtility mu, double[] v)
        {
            double[] id = new double[16];
            id[0] = 1; id[4] = 1; id[8] = 1; id[12] = 1;
            id[9] = v[0]; id[10] = v[1]; id[11] = v[2];
            return (MathTransform)mu.CreateTransform(id);
        }

        // ---- independent geometry read: top-level component bbox-centroids (assembly space, meters) ----
        private static List<double[]> Centroids(List<Component2> comps)
        {
            var list = new List<double[]>();
            foreach (var c in comps)
            {
                try
                {
                    double[] b = c.GetBox(false, false) as double[];
                    if (b == null || b.Length < 6) continue;
                    list.Add(new[] { (b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2 });
                }
                catch { }
            }
            return list;
        }
        private static double[] Mean(List<double[]> pts)
        {
            double sx = 0, sy = 0, sz = 0; int n = 0;
            foreach (var p in pts) { sx += p[0]; sy += p[1]; sz += p[2]; n++; }
            return n == 0 ? new double[3] : new[] { sx / n, sy / n, sz / n };
        }
        private static double Spread(List<double[]> pts, double[] mean)
        {
            double s = 0; int n = 0;
            foreach (var p in pts) { s += Sq(p[0] - mean[0]) + Sq(p[1] - mean[1]) + Sq(p[2] - mean[2]); n++; }
            return n == 0 ? 0 : Math.Sqrt(s / n);
        }
        private static double Dist(double[] a, double[] b) => Math.Sqrt(Sq(a[0] - b[0]) + Sq(a[1] - b[1]) + Sq(a[2] - b[2]));
        private static double[] ToMm(double[] m) => new[] { m[0] * 1000.0, m[1] * 1000.0, m[2] * 1000.0 };
        private static double Sq(double x) => x * x;
    }
}
