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
    public class MoveComponentResult
    {
        public string Component;          // the EXACT resolved component name that was moved (deterministic pick)
        public double[] TranslationMm;    // {x,y,z} requested translation (mm)
        public double[] CenterBeforeMm;   // {x,y,z} the target component's bbox centroid BEFORE (independent read)
        public double[] CenterAfterMm;    // {x,y,z} the target component's bbox centroid AFTER the move + rebuild
        public double MeasuredShiftMm;    // |CenterAfter - CenterBefore| — the honest, geometry-measured shift of the TARGET
        public int OthersMovedCount;      // OTHER components whose centroid moved (should be 0 for a single-part move)
        public int RebuildErrors;         // GetWhatsWrongCount AFTER the move
        public bool NeedsConfirm;         // no parseable target/vector, or genuinely ambiguous -> ask ONE question (Rule #2)
        public string Question;
        public bool Verified;             // fail closed: target shifted ~request, no OTHER component moved, rebuild clean
        public string Info;               // verdict-first one-liner
        public string Error;              // set => wrong doc / can't be moved freely (fixed/fully-mated); nothing moved
    }

    /// <summary>
    /// MoveComponent (tool #34, "translate a floating/unmated component by a vector"). A WRITE handler that moves ONE
    /// component by a linear vector — distinct from transform_assembly, which moves EVERY component rigidly. Here only
    /// the resolved target moves; everything else must stay exactly where it is.
    ///
    /// Parse (grounded in the LIVE model, Rule #8):
    ///   • TARGET — a named part (fuzzy token match) OR a kind (bolt/nut/flange/shaft/bracket/washer/screw/plate/housing/
    ///     gear). A kind matching MULTIPLE components picks the FIRST in tree order and REPORTS its exact name
    ///     (deterministic, never a guess between two). A phrase with no name and no kind -> ask ONE question.
    ///   • VECTOR — up=+Y, down=-Y, right=+X, left=-X, forward=+Z, back=-Z + a distance in mm, or "in X/Y/Z" + distance.
    ///     No parseable vector -> ask ONE question, moving nothing.
    ///
    /// Only a FLOATING (not fixed, under-constrained) component can be freely translated by Transform2. If the target is
    /// fixed or fully mated, the handler reports honestly that it can't be moved freely (Rule #4/#6) — never a fake move.
    ///
    /// The move is the PROVEN Transform2 ArrayData nudge (add the delta to indices [9..11], in METRES; the same path
    /// Explode/TransformAssembly use), applied to the ONE target only, then EditRebuild3/ForceRebuild3.
    ///
    /// A relative move is NOT idempotent (a rerun moves again — correct); this handler never asserts idempotency. Undo is
    /// one Ctrl+Z and Forge never saves. FAIL CLOSED (Rule #6): post-rebuild it INDEPENDENTLY re-reads the target's
    /// centroid, confirms it shifted by ~the requested vector, and confirms EVERY OTHER component's centroid is unchanged.
    /// </summary>
    public static class MoveComponent
    {
        // Single-component move — a move/nudge/shift/slide verb WITHOUT a whole-assembly scope word (that is
        // transform_assembly's territory). Keyword-guarded; the intent layer is primary. Placed AFTER
        // TransformAssembly.IsTransformIntent in the offline chain, so the whole-assembly form is claimed first.
        public static bool IsMoveComponentIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(move|nudge|shift|slide|shove|translate|reposition|relocate|drag|bump|scoot)\b");
            if (!verb) return false;
            // whole-assembly scope words belong to transform_assembly, not here
            bool wholeScope =
                Regex.IsMatch(c, @"\b(the\s+)?(whole|entire|complete|full)\s+(assembly|assy|thing|model)\b")
                || Regex.IsMatch(c, @"\beverything\b")
                || Regex.IsMatch(c, @"\ball\s+(the\s+)?(components|parts)\b")
                || Regex.IsMatch(c, @"\bto\s+(the\s+)?origin\b");
            if (wholeScope) return false;
            // must carry a linear direction so it doesn't grab "move on to the next" style noise
            return HasDirectionWord(c);
        }

        private static bool HasDirectionWord(string c)
        {
            return Regex.IsMatch(c, @"\b(up|down|left|right|forward|forwards|front|ahead|back|backward|backwards|behind|rear|rearward)\b")
                || Regex.IsMatch(c, @"\b(?:in|along|on|towards?|direction)\s+(?:the\s+)?[+\-]?\s*[xyz]\b")
                || Regex.IsMatch(c, @"[+\-]\s*[xyz]\b");
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

        private class Vec { public bool Has; public double[] V = new double[3]; public string Ask; }

        // ---- read-only VECTOR parse (writes nothing) ----
        private static Vec ParseVector(string intent)
        {
            var mv = new Vec();
            string c = (intent ?? "").ToLowerInvariant();
            int axis = -1; double sign = 1;
            if (Regex.IsMatch(c, @"\bup\b")) { axis = 1; sign = 1; }
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

            double mm; bool hasDist = TryMm(c, out mm);
            if (axis >= 0 && hasDist) { mv.Has = true; mv.V[axis] = sign * mm / 1000.0; }
            else if (axis >= 0 && !hasDist) mv.Ask = "How far should I move it (e.g. 30mm)?";
            else if (axis < 0 && hasDist) mv.Ask = "Which direction should I move it — up/down, left/right, forward/back, or along X/Y/Z?";
            else mv.Ask = "Where should I move it? Try \"move the bolt up 30mm\" or \"nudge the bracket 20mm in X\".";
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

        // ---- read-only TARGET resolution: deterministic single pick (writes nothing) ----
        private static Target ResolveTarget(AssemblyDoc asm, string intent)
        {
            var t = new Target();
            string c = (intent ?? "").ToLowerInvariant();
            object[] all = asm.GetComponents(false) as object[];   // tree order

            // strip the verb + the direction/distance clause so what remains is the target phrase
            string phrase = c;
            phrase = Regex.Replace(phrase, @"\b(move|nudge|shift|slide|shove|translate|reposition|relocate|drag|bump|scoot)\b", " ");
            phrase = Regex.Replace(phrase, @"\b(up|down|left|right|forward|forwards|front|ahead|back|backward|backwards|behind|rearward|rear)\b", " ");
            phrase = Regex.Replace(phrase, @"\b(?:in|along|on|towards?|direction)\s+(?:the\s+)?[+\-]?\s*[xyz]\b", " ");
            phrase = Regex.Replace(phrase, @"[+\-]?\s*\d+(?:\.\d+)?\s*(mm|millimet\w*|cm|centimet\w*|meters?|metres?|m\b|inch\w*|in\b|""|')?", " ");
            phrase = Regex.Replace(phrase, @"[+\-]\s*[xyz]\b", " ");

            var tokens = Tokens(phrase);

            // (1) a kind keyword wins deterministically: FIRST in tree order, report its exact name
            string wantKind = KindFromTokens(tokens, c);
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
                    // several parts share the token(s): deterministic first-in-tree, but say which one + how many
                    t.Comp = matches[0]; t.Name = NameOf(t.Comp);
                    t.How = "name (first of " + matches.Count + " — " + t.Name + ")";
                    return t;
                }
                t.Ask = "I couldn't find \"" + phrase.Trim() + "\" in this assembly. " + Present(all);
                return t;
            }

            // (3) genuinely ambiguous — no name, no kind
            t.Ask = "Which component should I move? " + Present(all);
            return t;
        }

        private static string KindFromTokens(List<string> tokens, string full)
        {
            // singular/plural kind detection over the target phrase
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

        // ---- preview only when needed (Rule #3): a single-part move is small + unambiguous -> null (execute directly) ----
        public static string PreviewLine(IModelDoc2 model, string intent)
        {
            return null;   // one component, one vector — never a broad destructive op; run directly, handler asks if unsure
        }

        public static async Task<MoveComponentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new MoveComponentResult { TranslationMm = new double[3], CenterBeforeMm = new double[3], CenterAfterMm = new double[3] };
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) with the component you want to move."; return res; }
            var mu = (MathUtility)app.GetMathUtility();

            await emit("Gauge", "reading the move", "run", null);
            var vec = ParseVector(intent);
            var t = ResolveTarget(asm, intent);

            // ask ONE question if either half is unresolved (target ambiguity takes precedence, then the vector)
            if (t.Ask != null) { await emit("Gauge", null, "fail", t.Ask); res.NeedsConfirm = true; res.Question = t.Ask; return res; }
            if (vec.Ask != null) { await emit("Gauge", null, "fail", vec.Ask); res.NeedsConfirm = true; res.Question = vec.Ask; return res; }

            res.Component = t.Name;
            res.TranslationMm = new[] { vec.V[0] * 1000.0, vec.V[1] * 1000.0, vec.V[2] * 1000.0 };
            double reqMm = Math.Sqrt(vec.V[0] * vec.V[0] + vec.V[1] * vec.V[1] + vec.V[2] * vec.V[2]) * 1000.0;

            // ---- only a FLOATING component can be freely translated; fixed / fully-mated cannot (Rule #4/#6) ----
            bool isFixed = false; try { isFixed = t.Comp.IsFixed(); } catch { }
            int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = t.Comp.GetConstrainedStatus(); } catch { }
            bool fullyMated = st == (int)swConstrainedStatus_e.swFullyConstrained
                           || st == (int)swConstrainedStatus_e.swOverConstrained;
            if (isFixed || fullyMated)
            {
                string why = isFixed ? "is Fixed" : "is fully mated";
                res.Error = "\"" + t.Name + "\" " + why + " — I can't move it freely by a vector without breaking its constraints. "
                          + "Float it (right-click -> Float) or delete its mates first, then ask again.";
                await emit("Gauge", null, "fail", t.Name + " " + why + " — can't move freely");
                return res;
            }
            await emit("Gauge", null, "done", "target: " + t.Name + " (" + t.How + "), " + DescribeVec(vec.V));

            // ---- INDEPENDENT before-state: the target's centroid AND every other component's centroid (Rule #6) ----
            var comps = TopComps(asm);
            var beforeByName = CentroidsByName(comps);
            double[] targetBefore; beforeByName.TryGetValue(t.Name, out targetBefore);
            if (targetBefore == null) targetBefore = Centroid(t.Comp);
            res.CenterBeforeMm = ToMm(targetBefore);

            // ---- apply the PROVEN Transform2 ArrayData nudge to the ONE target only ----
            await emit("Mover", "moving " + t.Name, "run", null);
            bool applied = false;
            try
            {
                double[] d = t.Comp.Transform2.ArrayData as double[];
                if (d != null && d.Length >= 13)
                {
                    double[] nd = (double[])d.Clone();
                    nd[9] += vec.V[0]; nd[10] += vec.V[1]; nd[11] += vec.V[2];
                    t.Comp.Transform2 = (MathTransform)mu.CreateTransform(nd);
                    applied = true;
                }
            }
            catch { }
            try { model.EditRebuild3(); } catch { }
            try { model.ForceRebuild3(false); } catch { }
            await emit("Mover", null, applied ? "done" : "fail", applied ? "set a new position on " + t.Name : "could not set the transform");

            // ---- FAIL CLOSED (Rule #6): re-read the target + every other centroid INDEPENDENTLY of the write path ----
            await emit("Sentinel", "verifying the move", "run", null);
            var afterByName = CentroidsByName(comps);
            double[] targetAfter; afterByName.TryGetValue(t.Name, out targetAfter);
            if (targetAfter == null) targetAfter = Centroid(t.Comp);
            res.CenterAfterMm = ToMm(targetAfter);
            res.MeasuredShiftMm = Dist(targetBefore, targetAfter) * 1000.0;

            int othersMoved = 0;
            foreach (var kv in beforeByName)
            {
                if (kv.Key == t.Name) continue;
                double[] a; if (!afterByName.TryGetValue(kv.Key, out a)) continue;
                if (Dist(kv.Value, a) * 1000.0 > 0.05) othersMoved++;   // 0.05mm tolerance
            }
            res.OthersMovedCount = othersMoved;
            try { res.RebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            bool shiftMatched = Math.Abs(res.MeasuredShiftMm - reqMm) <= Math.Max(0.5, reqMm * 0.02);
            bool nobodyElse = othersMoved == 0;
            bool rebuildClean = res.RebuildErrors == 0;
            res.Verified = applied && shiftMatched && nobodyElse && rebuildClean;

            await emit("Sentinel", null, "done",
                t.Name + " shifted " + res.MeasuredShiftMm.ToString("0.0") + "mm · " +
                (shiftMatched ? "matches request" : "does NOT match request (" + reqMm.ToString("0.0") + "mm)") + " · " +
                (nobodyElse ? "no other component moved" : othersMoved + " OTHER component(s) moved") + " · " +
                (rebuildClean ? "rebuild clean" : res.RebuildErrors + " rebuild flag(s)"));

            res.Info = BuildInfo(res, t, vec, shiftMatched, nobodyElse);
            return res;
        }

        // ---- verdict first (Character #3), the number not the adjective (Character #2), honest about resistance ----
        private static string BuildInfo(MoveComponentResult r, Target t, Vec vec, bool shiftMatched, bool nobodyElse)
        {
            var sb = new StringBuilder();
            sb.Append("Moved " + t.Name + " " + DescribeVec(vec.V) + " — shifted " + r.MeasuredShiftMm.ToString("0.0") + "mm");
            double reqMm = Math.Sqrt(vec.V[0] * vec.V[0] + vec.V[1] * vec.V[1] + vec.V[2] * vec.V[2]) * 1000.0;
            if (!shiftMatched) sb.Append("; WARNING: that does NOT match the requested " + reqMm.ToString("0.0")
                + "mm — a mate likely resisted (this component may not be fully free). Check it");
            if (!nobodyElse) sb.Append("; WARNING: " + r.OthersMovedCount + " OTHER component(s) also moved — this part shares a mate, it isn't independently floating. Check the assembly");
            else sb.Append("; nothing else moved");
            if (r.RebuildErrors > 0) sb.Append(". " + r.RebuildErrors + " rebuild flag(s) after — check the assembly");
            sb.Append(". Reversible: one Ctrl+Z, and the document was not saved.");
            return sb.ToString();
        }

        private static string DescribeVec(double[] v)
        {
            int ax = v[0] != 0 ? 0 : (v[1] != 0 ? 1 : 2);
            double mm = v[ax] * 1000.0;
            string dir;
            if (ax == 1) dir = mm >= 0 ? "up" : "down";
            else if (ax == 0) dir = mm >= 0 ? "right" : "left";
            else dir = mm >= 0 ? "forward" : "back";
            return dir + " " + Math.Abs(mm).ToString("0.#") + "mm (" + (mm >= 0 ? "+" : "") + mm.ToString("0.#") + "mm in " + "XYZ"[ax] + ")";
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

        private static double[] Centroid(Component2 c)
        {
            try
            {
                double[] b = c.GetBox(false, false) as double[];
                if (b == null || b.Length < 6) return null;
                return new[] { (b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2 };
            }
            catch { return null; }
        }

        private static double Dist(double[] a, double[] b)
        {
            if (a == null || b == null) return 0;
            return Math.Sqrt(Sq(a[0] - b[0]) + Sq(a[1] - b[1]) + Sq(a[2] - b[2]));
        }
        private static double[] ToMm(double[] m) => m == null ? new double[3] : new[] { m[0] * 1000.0, m[1] * 1000.0, m[2] * 1000.0 };
        private static double Sq(double x) => x * x;

        // ---- name / token helpers ----
        private static bool IsSup(Component2 c) { try { return c.IsSuppressed(); } catch { return false; } }
        private static string NameOf(Component2 c) { try { return c.Name2; } catch { return null; } }

        private static readonly HashSet<string> Stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "the", "a", "an", "and", "all", "part", "parts", "component", "components", "one", "ones", "them", "it",
              "this", "that", "please", "by", "of", "some", "just", "only", "over", "then", "next", "to", "for" };

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

        // "This assembly has: bolts, nuts, flanges. Which component should I move?" — grounded in the live model
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
            if (labels.Count == 0) return "Which component should I move?";
            return "This assembly has: " + string.Join(", ", labels) + ". Which one should I move?";
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
