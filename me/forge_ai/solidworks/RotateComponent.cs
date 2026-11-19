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
    public class RotateComponentResult
    {
        public string Component;          // the EXACT resolved component name that was rotated (deterministic pick)
        public double AngleDeg;           // requested rotation angle (degrees)
        public string Axis;               // "X"|"Y"|"Z" (assembly axis) or "its axis" (the component's local Z)
        public double CentroidShiftMm;    // |CenterAfter - CenterBefore| — should be ~0 (rotation about the OWN centre)
        public double MeasuredAngleDeg;   // the orientation change actually measured from before/after rotation matrices
        public int OthersMovedCount;      // OTHER components whose centroid OR orientation changed (should be 0)
        public int RebuildErrors;         // GetWhatsWrongCount AFTER the rotate
        public bool NeedsConfirm;         // no parseable target/angle, or genuinely ambiguous -> ask ONE question (Rule #2)
        public string Question;
        public bool Verified;             // fail closed: target turned ~request, centre held, no OTHER component changed, rebuild clean
        public string Info;               // verdict-first one-liner
        public string Error;              // set => wrong doc / can't be rotated freely (fixed/fully-mated); nothing rotated
    }

    /// <summary>
    /// RotateComponent (tool #35, "rotate a floating/unmated component about an axis by an angle"). A WRITE handler that
    /// turns ONE component in place about its OWN centroid — the component's orientation changes while its centre stays
    /// put (the natural "spin it where it sits" behaviour). Sibling of move_component: move_component translates a single
    /// component, this one rotates a single component; neither touches any other component, and both are distinct from
    /// transform_assembly, which rotates EVERY component rigidly about the global origin.
    ///
    /// Parse (grounded in the LIVE model, Rule #8):
    ///   • TARGET — a named part (fuzzy token match) OR a kind (bolt/nut/flange/shaft/bracket/washer/screw/plate/housing/
    ///     gear). A kind matching MULTIPLE components picks the FIRST in tree order and REPORTS its exact name
    ///     (deterministic, never a guess between two). A phrase with no name and no kind -> ask ONE question.
    ///   • ANGLE — a number of degrees ("90 degrees", "45°", bare "180"), or "quarter turn" = 90 / "half turn" = 180 /
    ///     "full turn" = 360. No parseable angle -> ask ONE question, rotating nothing.
    ///   • AXIS — "about X/Y/Z" (assembly axis), default Z if unspecified, or "about its (own) axis" = the component's
    ///     local Z axis (its shank/spin axis) expressed in assembly space.
    ///
    /// Only a FLOATING (not fixed, under-constrained) component can be freely rotated by Transform2. If the target is
    /// fixed or fully mated, the handler reports honestly that it can't be turned freely (Rule #4/#6) — never a fake spin.
    ///
    /// The rotate composes a CreateTransformRotateAxis(centre, axis, angle) onto the ONE target's current Transform2 (the
    /// same IMathTransform.Multiply compose path TransformAssembly uses, with the multiply order detected once at runtime),
    /// then EditRebuild3/ForceRebuild3. Because the axis line passes through the component's own centroid, the centre is a
    /// fixed point of the rotation: the part turns, the centre holds.
    ///
    /// A rotate is NOT idempotent (a rerun rotates AGAIN — correct); this handler never asserts idempotency. Undo is one
    /// Ctrl+Z and Forge never saves. FAIL CLOSED (Rule #6): post-rebuild it INDEPENDENTLY re-reads the target's rotation
    /// matrix and centroid, confirms the orientation turned by ~the requested angle (relative-rotation angle from the
    /// before/after matrices), the centroid barely moved, and EVERY OTHER component's centroid AND orientation is unchanged.
    /// </summary>
    public static class RotateComponent
    {
        // Single-component rotate — a rotate/turn/spin verb WITH a parseable angle and WITHOUT a whole-assembly scope word
        // (that is transform_assembly's territory). Keyword-guarded; the intent layer is primary. Placed AFTER
        // MoveComponent.IsMoveComponentIntent in the offline chain (which itself is after TransformAssembly), so the
        // whole-assembly rotate form is always claimed first and only single-component rotates fall through to here.
        public static bool IsRotateComponentIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(rotate|turn|spin|revolve|pivot|swivel|twist)\b");
            if (!verb) return false;
            // whole-assembly scope words belong to transform_assembly, not here
            bool wholeScope =
                Regex.IsMatch(c, @"\b(the\s+)?(whole|entire|complete|full)\s+(assembly|assy|thing|model)\b")
                || Regex.IsMatch(c, @"\beverything\b")
                || Regex.IsMatch(c, @"\ball\s+(the\s+)?(components|parts)\b");
            if (wholeScope) return false;
            // must carry a parseable angle so it doesn't grab "turn off ..." / "spin up ..." style noise
            double deg; string axis; bool its;
            return TryParseSpin(c, out deg, out axis, out its);
        }

        // ---- kind vocabulary: name substring -> canonical kind (grounded classification, self-contained) ----
        private static readonly string[] BoltHints = { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud", "vis", "boulon", "iso", "din", "b18" };
        private static string Classify(string n)
        {
            if (string.IsNullOrEmpty(n)) return "other";
            n = n.ToLowerInvariant();
            if (n.Contains("nut") || n.Contains("ecrou")) return "nut";
            if (n.Contains("washer") || n.Contains("rondelle")) return "washer";
            if (n.Contains("flange")) return "flange";
            if (n.Contains("shaft")) return "shaft";
            if (n.Contains("bracket")) return "bracket";
            if (n.Contains("plate")) return "plate";
            if (n.Contains("housing") || n.Contains("case") || n.Contains("casing")) return "housing";
            if (n.Contains("gear")) return "gear";
            foreach (var h in BoltHints) if (n.Contains(h)) return "bolt";
            return "other";
        }

        private class Target
        {
            public Component2 Comp;      // the resolved single target (null until resolved)
            public string Name;          // its exact name
            public string How;           // "kind:bolt (first of 4)" / "name" — for the info line
            public string Ask;           // set => ambiguous / not found -> ask this
        }

        private class Spin { public bool Has; public double AngleDeg; public string Axis = "Z"; public bool ItsAxis; public string Ask; }

        // ---- read-only ANGLE + AXIS parse (writes nothing) ----
        private static Spin ParseSpin(string intent)
        {
            var sp = new Spin();
            string c = (intent ?? "").ToLowerInvariant();
            double deg; string axis; bool its;
            if (TryParseSpin(c, out deg, out axis, out its))
            {
                sp.Has = true; sp.AngleDeg = deg; sp.Axis = axis; sp.ItsAxis = its;
            }
            else
            {
                sp.Ask = "How far should I rotate it, and about which axis? Try \"rotate the bolt 90 degrees about Z\" or \"spin the plate 180\".";
            }
            return sp;
        }

        // core angle+axis extraction — shared by the intent gate and the parse so they never disagree
        private static bool TryParseSpin(string c, out double deg, out string axis, out bool itsAxis)
        {
            deg = 0; axis = "Z"; itsAxis = false;
            if (string.IsNullOrEmpty(c)) return false;

            bool haveAngle = false;
            // (1) worded fractions of a turn
            if (Regex.IsMatch(c, @"\bquarter\s+turn\b")) { deg = 90; haveAngle = true; }
            else if (Regex.IsMatch(c, @"\bhalf\s+turn\b")) { deg = 180; haveAngle = true; }
            else if (Regex.IsMatch(c, @"\b(full|complete)\s+(turn|rotation|revolution)\b")) { deg = 360; haveAngle = true; }
            else
            {
                // (2) explicit degrees, then (3) a bare number (rotate verb already gates the caller, so a lone number = degrees)
                var mDeg = Regex.Match(c, @"(-?\d+(?:\.\d+)?)\s*(?:deg\b|degs\b|degree|degrees|°)");
                if (!mDeg.Success) mDeg = Regex.Match(c, @"(-?\d+(?:\.\d+)?)");
                if (mDeg.Success) { deg = double.Parse(mDeg.Groups[1].Value, CultureInfo.InvariantCulture); haveAngle = true; }
            }
            if (!haveAngle) return false;

            // axis: "about its (own) axis" = local Z; else about X/Y/Z; default Z
            if (Regex.IsMatch(c, @"\b(?:about|around|round|on|along)\s+(?:its|it'?s|the\s+component'?s)\s+(?:own\s+)?axis\b")
                || Regex.IsMatch(c, @"\bits\s+own\s+axis\b"))
            {
                itsAxis = true; axis = "its axis"; return true;
            }
            var mAxis = Regex.Match(c, @"\b(?:about|around|round|on|over|along|w\.?r\.?t\.?)\s+(?:the\s+)?([xyz])\b");
            if (!mAxis.Success) mAxis = Regex.Match(c, @"\b([xyz])[\s\-]*axis\b");
            if (mAxis.Success) axis = mAxis.Groups[1].Value.ToUpperInvariant();
            return true;
        }

        // ---- read-only TARGET resolution: deterministic single pick (writes nothing) ----
        private static Target ResolveTarget(AssemblyDoc asm, string intent)
        {
            var t = new Target();
            string c = (intent ?? "").ToLowerInvariant();
            object[] all = asm.GetComponents(false) as object[];   // tree order

            // strip the verb + the angle/axis clause so what remains is the target phrase
            string phrase = c;
            phrase = Regex.Replace(phrase, @"\b(rotate|turn|spin|revolve|pivot|swivel|twist)\b", " ");
            phrase = Regex.Replace(phrase, @"\b(quarter|half|full|complete)\s+(turn|rotation|revolution)\b", " ");
            phrase = Regex.Replace(phrase, @"\b(?:about|around|round|on|over|along|w\.?r\.?t\.?)\s+(?:its|it'?s|the\s+component'?s)?\s*(?:own\s+)?(?:the\s+)?[xyz]?\s*axis\b", " ");
            phrase = Regex.Replace(phrase, @"\b(?:about|around|round|on|over|along|w\.?r\.?t\.?)\s+(?:the\s+)?[xyz]\b", " ");
            phrase = Regex.Replace(phrase, @"\b[xyz][\s\-]*axis\b", " ");
            phrase = Regex.Replace(phrase, @"(-?\d+(?:\.\d+)?)\s*(?:deg\b|degs\b|degree|degrees|°)", " ");
            phrase = Regex.Replace(phrase, @"(-?\d+(?:\.\d+)?)", " ");
            phrase = Regex.Replace(phrase, @"\bby\b|\bdegrees?\b", " ");

            var tokens = Tokens(phrase);

            // (1) a kind keyword wins deterministically: FIRST in tree order, report its exact name
            string wantKind = KindFromTokens(tokens);
            if (wantKind != null)
            {
                var matches = new List<Component2>();
                foreach (var o in all ?? new object[0])
                {
                    var comp = o as Component2; if (comp == null) continue;
                    if (IsSup(comp)) continue;
                    if (Classify(NameOf(comp)) == wantKind) matches.Add(comp);
                }
                if (matches.Count == 0) { t.Ask = "I don't see a " + wantKind + " in this assembly. " + Present(all); return t; }
                t.Comp = matches[0];                         // deterministic: first in tree order
                t.Name = NameOf(t.Comp);
                t.How = matches.Count == 1 ? ("the " + wantKind) : ("kind:" + wantKind + " (first of " + matches.Count + " — " + t.Name + ")");
                return t;
            }

            // (2) a specific named part (fuzzy token match)
            if (tokens.Count > 0)
            {
                var matches = new List<Component2>();
                foreach (var o in all ?? new object[0])
                {
                    var comp = o as Component2; if (comp == null) continue;
                    if (IsSup(comp)) continue;
                    if (MatchesAny(NameOf(comp), tokens)) matches.Add(comp);
                }
                if (matches.Count == 1) { t.Comp = matches[0]; t.Name = NameOf(t.Comp); t.How = "name"; return t; }
                if (matches.Count > 1)
                {
                    t.Comp = matches[0]; t.Name = NameOf(t.Comp);
                    t.How = "name (first of " + matches.Count + " — " + t.Name + ")";
                    return t;
                }
                t.Ask = "I couldn't find \"" + phrase.Trim() + "\" in this assembly. " + Present(all);
                return t;
            }

            // (3) genuinely ambiguous — no name, no kind
            t.Ask = "Which component should I rotate? " + Present(all);
            return t;
        }

        private static string KindFromTokens(List<string> tokens)
        {
            foreach (var tk in tokens)
            {
                string s = tk.EndsWith("s") && tk.Length > 3 ? tk.Substring(0, tk.Length - 1) : tk;
                if (s == "bolt" || s == "screw" || s == "cap" || s == "hcs" || s == "shcs") return "bolt";
                if (s == "nut") return "nut";
                if (s == "washer") return "washer";
                if (s == "flange") return "flange";
                if (s == "shaft") return "shaft";
                if (s == "bracket") return "bracket";
                if (s == "plate") return "plate";
                if (s == "housing" || s == "case" || s == "casing") return "housing";
                if (s == "gear") return "gear";
            }
            return null;
        }

        // ---- preview only when needed (Rule #3): a single-part rotate is small + unambiguous -> null (execute directly) ----
        public static string PreviewLine(IModelDoc2 model, string intent)
        {
            return null;   // one component, one rotation — never a broad destructive op; run directly, handler asks if unsure
        }

        public static async Task<RotateComponentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RotateComponentResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) with the component you want to rotate."; return res; }
            var mu = (MathUtility)app.GetMathUtility();

            await emit("Gauge", "reading the rotate", "run", null);
            var sp = ParseSpin(intent);
            var t = ResolveTarget(asm, intent);

            // ask ONE question if either half is unresolved (target ambiguity takes precedence, then the angle)
            if (t.Ask != null) { await emit("Gauge", null, "fail", t.Ask); res.NeedsConfirm = true; res.Question = t.Ask; return res; }
            if (sp.Ask != null) { await emit("Gauge", null, "fail", sp.Ask); res.NeedsConfirm = true; res.Question = sp.Ask; return res; }

            res.Component = t.Name;
            res.AngleDeg = sp.AngleDeg;
            res.Axis = sp.ItsAxis ? "its axis" : sp.Axis;

            // ---- only a FLOATING component can be freely rotated; fixed / fully-mated cannot (Rule #4/#6) ----
            bool isFixed = false; try { isFixed = t.Comp.IsFixed(); } catch { }
            int cst = (int)swConstrainedStatus_e.swUnderConstrained; try { cst = t.Comp.GetConstrainedStatus(); } catch { }
            bool fullyMated = cst == (int)swConstrainedStatus_e.swFullyConstrained
                           || cst == (int)swConstrainedStatus_e.swOverConstrained;
            if (isFixed || fullyMated)
            {
                string why = isFixed ? "is Fixed" : "is fully mated";
                res.Error = "\"" + t.Name + "\" " + why + " — I can't rotate it freely without breaking its constraints. "
                          + "Float it (right-click -> Float) or delete its mates first, then ask again.";
                await emit("Gauge", null, "fail", t.Name + " " + why + " — can't rotate freely");
                return res;
            }
            await emit("Gauge", null, "done", "target: " + t.Name + " (" + t.How + "), " + DescribeSpin(sp));

            // ---- INDEPENDENT before-state: the target's rotation + centroid AND every other component's (Rule #6) ----
            var comps = TopComps(asm);
            var beforeCtr = CentroidsByName(comps);
            var beforeRot = RotationsByName(comps);
            double[] targetCtrBefore; beforeCtr.TryGetValue(t.Name, out targetCtrBefore);
            if (targetCtrBefore == null) targetCtrBefore = Centroid(t.Comp);
            double[] targetRotBefore; beforeRot.TryGetValue(t.Name, out targetRotBefore);
            if (targetRotBefore == null) targetRotBefore = Rotation(t.Comp);

            // ---- build the rotation about the target's OWN centroid and compose it onto the target's transform only ----
            await emit("Rotor", "rotating " + t.Name, "run", null);
            bool applied = false;
            try
            {
                double[] cm = targetCtrBefore != null ? new[] { targetCtrBefore[0] / 1000.0, targetCtrBefore[1] / 1000.0, targetCtrBefore[2] / 1000.0 } : new double[3];
                double[] axisArr;
                if (sp.ItsAxis)
                {
                    // the component's local Z axis, expressed in assembly space (rotation-only via MultiplyTransform)
                    MathVector lz = (MathVector)mu.CreateVector(new double[] { 0, 0, 1 });
                    var lzA = lz.MultiplyTransform(t.Comp.Transform2) as MathVector;
                    double[] a = lzA != null ? lzA.ArrayData as double[] : null;
                    axisArr = (a != null && a.Length >= 3) ? new[] { a[0], a[1], a[2] } : new double[] { 0, 0, 1 };
                }
                else axisArr = sp.Axis == "X" ? new double[] { 1, 0, 0 } : sp.Axis == "Y" ? new double[] { 0, 1, 0 } : new double[] { 0, 0, 1 };

                MathPoint ctr = (MathPoint)mu.CreatePoint(cm);
                MathVector axv = (MathVector)mu.CreateVector(axisArr);
                MathTransform rot = mu.CreateTransformRotateAxis(ctr, axv, sp.AngleDeg * Math.PI / 180.0) as MathTransform;
                if (rot != null)
                {
                    t.Comp.Transform2 = Compose(mu, t.Comp.Transform2, rot);   // current transform first, then rotate in assembly space
                    applied = true;
                }
            }
            catch { }
            try { model.EditRebuild3(); } catch { }
            try { model.ForceRebuild3(false); } catch { }
            await emit("Rotor", null, applied ? "done" : "fail", applied ? "set a new orientation on " + t.Name : "could not set the transform");

            // ---- FAIL CLOSED (Rule #6): re-read the target + every other component INDEPENDENTLY of the write path ----
            await emit("Sentinel", "verifying the rotate", "run", null);
            var afterCtr = CentroidsByName(comps);
            var afterRot = RotationsByName(comps);
            double[] targetCtrAfter; afterCtr.TryGetValue(t.Name, out targetCtrAfter);
            if (targetCtrAfter == null) targetCtrAfter = Centroid(t.Comp);
            double[] targetRotAfter; afterRot.TryGetValue(t.Name, out targetRotAfter);
            if (targetRotAfter == null) targetRotAfter = Rotation(t.Comp);

            res.CentroidShiftMm = Dist(targetCtrBefore, targetCtrAfter);   // centroids already in mm
            res.MeasuredAngleDeg = RelAngleDeg(targetRotBefore, targetRotAfter);

            int othersMoved = 0;
            foreach (var kv in beforeCtr)
            {
                if (kv.Key == t.Name) continue;
                double[] ca; if (!afterCtr.TryGetValue(kv.Key, out ca)) continue;
                bool ctrChanged = Dist(kv.Value, ca) > 0.05;   // 0.05mm tolerance
                bool rotChanged = false;
                double[] rb, ra;
                if (beforeRot.TryGetValue(kv.Key, out rb) && afterRot.TryGetValue(kv.Key, out ra))
                    rotChanged = RelAngleDeg(rb, ra) > 0.5;     // 0.5deg tolerance
                if (ctrChanged || rotChanged) othersMoved++;
            }
            res.OthersMovedCount = othersMoved;
            try { res.RebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            // requested angle folded to the [0,180] range that a relative-rotation measurement can represent (270 -> 90 etc.)
            double reqEff = Math.Abs(sp.AngleDeg) % 360.0;
            if (reqEff > 180.0) reqEff = 360.0 - reqEff;
            bool angleMatched = Math.Abs(res.MeasuredAngleDeg - reqEff) <= Math.Max(1.5, reqEff * 0.02);
            bool centreHeld = res.CentroidShiftMm <= 0.2;   // rotation about the OWN centre -> the centre is a fixed point
            bool nobodyElse = othersMoved == 0;
            bool rebuildClean = res.RebuildErrors == 0;
            res.Verified = applied && angleMatched && centreHeld && nobodyElse && rebuildClean;

            await emit("Sentinel", null, "done",
                t.Name + " turned " + res.MeasuredAngleDeg.ToString("0.0") + "° · " +
                (angleMatched ? "matches request" : "does NOT match request (" + reqEff.ToString("0.0") + "°)") + " · " +
                (centreHeld ? "centre held (" + res.CentroidShiftMm.ToString("0.00") + "mm)" : "centre DRIFTED " + res.CentroidShiftMm.ToString("0.00") + "mm") + " · " +
                (nobodyElse ? "no other component moved" : othersMoved + " OTHER component(s) changed") + " · " +
                (rebuildClean ? "rebuild clean" : res.RebuildErrors + " rebuild flag(s)"));

            res.Info = BuildInfo(res, t, sp, angleMatched, centreHeld, nobodyElse, reqEff);
            return res;
        }

        // ---- verdict first (Character #3), the number not the adjective (Character #2), honest about resistance ----
        private static string BuildInfo(RotateComponentResult r, Target t, Spin sp, bool angleMatched, bool centreHeld, bool nobodyElse, double reqEff)
        {
            var sb = new StringBuilder();
            sb.Append("Rotated " + t.Name + " " + DescribeSpin(sp) + " — turned " + r.MeasuredAngleDeg.ToString("0.0") + "°");
            if (!angleMatched) sb.Append("; WARNING: that does NOT match the requested " + reqEff.ToString("0.0")
                + "° — a mate likely resisted (this component may not be fully free). Check it");
            if (!centreHeld) sb.Append("; WARNING: the centre drifted " + r.CentroidShiftMm.ToString("0.00") + "mm — the rotation was not cleanly about the component's own centre. Check it");
            if (!nobodyElse) sb.Append("; WARNING: " + r.OthersMovedCount + " OTHER component(s) also changed — this part shares a mate, it isn't independently floating. Check the assembly");
            else sb.Append("; centre held, nothing else moved");
            if (r.RebuildErrors > 0) sb.Append(". " + r.RebuildErrors + " rebuild flag(s) after — check the assembly");
            sb.Append(". Reversible: one Ctrl+Z, and the document was not saved.");
            return sb.ToString();
        }

        private static string DescribeSpin(Spin sp)
        {
            return sp.AngleDeg.ToString("0.#") + "° about " + (sp.ItsAxis ? "its own axis" : sp.Axis);
        }

        // ---- relative-rotation angle between two 3x3 matrices (each stored as 9 elements): the Frobenius inner product
        //      trace(R0^T·R1) = Σ R0[k]·R1[k] is layout-independent (row- vs column-major), so angle = acos((Σ-1)/2). ----
        private static double RelAngleDeg(double[] r0, double[] r1)
        {
            if (r0 == null || r1 == null || r0.Length < 9 || r1.Length < 9) return 0;
            double dot = 0;
            for (int k = 0; k < 9; k++) dot += r0[k] * r1[k];
            double cos = (dot - 1.0) / 2.0;
            if (cos > 1.0) cos = 1.0; else if (cos < -1.0) cos = -1.0;
            return Math.Acos(cos) * 180.0 / Math.PI;
        }

        // ---- compose two transforms so a point sees `first` applied, then `second`. The IMathTransform.Multiply order is
        //      detected ONCE by a probe point (this build's convention is not assumed) — same approach as TransformAssembly. ----
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

        // ---- independent geometry reads ----
        private static List<Component2> TopComps(AssemblyDoc asm)
        {
            var list = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                if (IsSup(c)) continue;
                list.Add(c);
            }
            return list;
        }

        private static Dictionary<string, double[]> CentroidsByName(List<Component2> comps)
        {
            var map = new Dictionary<string, double[]>();
            foreach (var c in comps)
            {
                string n = NameOf(c); if (string.IsNullOrEmpty(n) || map.ContainsKey(n)) continue;
                double[] ctr = Centroid(c); if (ctr != null) map[n] = ctr;
            }
            return map;
        }

        private static Dictionary<string, double[]> RotationsByName(List<Component2> comps)
        {
            var map = new Dictionary<string, double[]>();
            foreach (var c in comps)
            {
                string n = NameOf(c); if (string.IsNullOrEmpty(n) || map.ContainsKey(n)) continue;
                double[] r = Rotation(c); if (r != null) map[n] = r;
            }
            return map;
        }

        // bbox center in assembly space, MILLIMETERS
        private static double[] Centroid(Component2 c)
        {
            try
            {
                double[] b = c.GetBox(false, false) as double[];
                if (b == null || b.Length < 6) return null;
                return new[] { (b[0] + b[3]) / 2 * 1000.0, (b[1] + b[4]) / 2 * 1000.0, (b[2] + b[5]) / 2 * 1000.0 };
            }
            catch { return null; }
        }

        // the 9 rotation-matrix elements of the component's Transform2 (ArrayData [0..8])
        private static double[] Rotation(Component2 c)
        {
            try
            {
                double[] d = c.Transform2.ArrayData as double[];
                if (d == null || d.Length < 9) return null;
                var r = new double[9];
                Array.Copy(d, 0, r, 0, 9);
                return r;
            }
            catch { return null; }
        }

        private static double Dist(double[] a, double[] b)
        {
            if (a == null || b == null) return 0;
            return Math.Sqrt(Sq(a[0] - b[0]) + Sq(a[1] - b[1]) + Sq(a[2] - b[2]));
        }
        private static double Sq(double x) => x * x;

        // ---- name / token helpers ----
        private static bool IsSup(Component2 c) { try { return c.IsSuppressed(); } catch { return false; } }
        private static string NameOf(Component2 c) { try { return c.Name2; } catch { return null; } }

        private static readonly HashSet<string> Stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "the", "a", "an", "and", "all", "part", "parts", "component", "components", "one", "ones", "them", "it",
              "this", "that", "please", "by", "of", "some", "just", "only", "over", "then", "next", "to", "for",
              "degrees", "degree", "deg", "about", "around", "axis", "own" };

        private static List<string> Tokens(string phrase)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(phrase)) return list;
            foreach (Match m in Regex.Matches(phrase.ToLowerInvariant(), @"[a-z0-9]+"))
            {
                string w = m.Value;
                if (w.Length < 2 || Stop.Contains(w)) continue;
                list.Add(w);
            }
            return list;
        }

        private static bool MatchesAny(string name, List<string> tokens)
        {
            if (string.IsNullOrEmpty(name) || tokens == null || tokens.Count == 0) return false;
            string n = name.ToLowerInvariant();
            foreach (var tk in tokens)
            {
                if (n.Contains(tk)) return true;
                if (tk.Length > 3 && tk.EndsWith("s") && n.Contains(tk.Substring(0, tk.Length - 1))) return true;
            }
            return false;
        }

        // "This assembly has: bolts, nuts, flanges. Which component should I rotate?" — grounded in the live model
        private static string Present(object[] all)
        {
            var counts = new Dictionary<string, int>();
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                if (IsSup(c)) continue;
                string k = Coarse(NameOf(c));
                int n; counts.TryGetValue(k, out n); counts[k] = n + 1;
            }
            var kinds = new List<KeyValuePair<string, int>>(counts);
            kinds.Sort((a, b) => b.Value.CompareTo(a.Value));
            var labels = new List<string>();
            foreach (var kv in kinds) { labels.Add(kv.Key); if (labels.Count >= 6) break; }
            if (labels.Count == 0) return "Which component should I rotate?";
            return "This assembly has: " + string.Join(", ", labels) + ". Which one should I rotate?";
        }

        private static string Coarse(string n)
        {
            if (string.IsNullOrEmpty(n)) return "other";
            n = n.ToLowerInvariant();
            if (n.Contains("nut") || n.Contains("ecrou")) return "nuts";
            if (n.Contains("washer") || n.Contains("rondelle")) return "washers";
            foreach (var h in BoltHints) if (n.Contains(h)) return "bolts";
            if (n.Contains("flange")) return "flanges";
            if (n.Contains("shaft")) return "shafts";
            if (n.Contains("bracket")) return "brackets";
            if (n.Contains("plate")) return "plates";
            if (n.Contains("housing") || n.Contains("case") || n.Contains("casing")) return "housings";
            if (n.Contains("gear")) return "gears";
            return "other";
        }
    }
}
