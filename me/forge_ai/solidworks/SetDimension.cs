using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SetDimensionResult
    {
        public string TargetDim;               // resolved dimension FullName, e.g. "D1@Sketch1@Part"
        public double OldValueMm = -1;         // the dimension's value BEFORE the write (mm), independently read
        public double NewValueMm = -1;         // the dimension's value AFTER the write + rebuild (mm), read back fresh
        public double RequestedValueMm = -1;   // the value the user asked for (mm), after unit conversion
        public int MatchedDims;                // how many dims the target phrase matched (1 => unique; >1 => asked)
        public int RebuildErrors;              // GetWhatsWrongCount() post-rebuild (0 => clean)
        public bool RolledBack;                // the change broke the rebuild → old value restored, part unchanged
        public bool Verified;                  // fail closed: true ONLY when the fresh read-back == requested AND rebuild clean
        public bool AlreadyDone;               // idempotent: the dim already sits at the requested value → nothing to change
        public bool NeedsConfirm;              // zero/multiple matches, or no value stated → ask ONE question, run nothing
        public string Question;                // the one clarifying question when NeedsConfirm
        public string Info;                    // verdict-first panel line
        public string Error;                   // honest failure text (no dims, unmeasurable, rebuild broke)
    }

    /// <summary>
    /// SetDimension (tool #63 "set a model dimension by plain English") — the core "change a dimension by talking to
    /// the model" WRITE on a PART (assembly-level dims are allowed too — anything reachable by the same feature-tree
    /// traversal). "change the boss height to 80", "set the length to 100mm", "make the bore diameter 25",
    /// "change D1 to 50".
    ///
    /// Approach (deliberate): RESOLVE the target dimension by traversing the feature tree
    /// (GetFirstDisplayDimension / GetNextDisplayDimension → GetDimension2(0)) and fuzzy-scoring every display
    /// dimension against the target phrase — its short name ("D1"), its owning feature ("Boss-Extrude1"), and a
    /// type synonym set (a diameter-type dim answers to "diameter"/"bore"; a linear dim to "height"/"length"/"depth").
    /// Then set IDimension.SystemValue (metres — the same field IDimension.SetSystemValue3(swThisConfiguration) writes)
    /// and ForceRebuild3.
    ///
    /// Robustness (the 12 rules): RESOLVE-OR-ASK (Rule #2) — ZERO matches → one question listing the actual dims and
    /// their current values; MULTIPLE matches → one question listing the candidates so the engineer picks; a missing
    /// value → one question. Never a guess. PREVIEW (Rule #3) — SetDimension.Preview builds the exact "D1@Sketch1 is
    /// 60mm → 80mm, proceed?" line the Destructive pipeline shows before the write. IDEMPOTENT (Rule #5) — already at
    /// the requested value → "already 80mm, nothing to change." ROLLBACK (Rule #6) — if the rebuild ERRORS after the
    /// change, the old value is restored and the failure reported ("setting the bore to 25 broke the rebuild —
    /// reverted"). FAIL CLOSED (Rule #6) — the dimension is re-read fresh (a direct model.Parameter lookup, a
    /// different path than the traversal that found it) and Verified is set only when the read-back equals the
    /// requested value AND the rebuild is clean. UNDO (Rule #7) — one dim moves and Forge never saves; one Ctrl+Z
    /// restores it.
    /// </summary>
    public static class SetDimension
    {
        private const double EpsMm = 1e-3;   // a dim "already at" the target if within 1 micron of it

        // Verb ("change/set/make/adjust") + a target + a NEW numeric value. Deliberately NOT "scale"/"shrink"
        // (whole-part factor → ScalePart) nor "M6→M8" (a fastener swap → Resizer). See SetDimension.integration.md (b).
        public static bool IsSetDimensionIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            var low = cmd.ToLowerInvariant();
            if (ScalePart.IsScaleIntent(low)) return false;                       // "scale 2x" is a whole-part scale
            if (Regex.IsMatch(low, @"\bm\d+\b.*\bm\d+\b")) return false;          // "M6 to M8" is a fastener resize
            if (Regex.IsMatch(low, @"\bmate\b")) return false;                    // "change the distance mate to 25" is edit_mate_value, not a dimension
            if (Regex.IsMatch(low, @"\bdesign\s*table\b")) return false;          // "set Variant-1's depth to 25mm in the design table" is manage_design_table (tool 194), not a direct dimension edit
            // test-loop hedged finding change-thread-size: "need to resize the valve thread to 2 inch" carries no
            // M-number pair at all (Resizer.IsResizeIntent needs TWO, e.g. "M6 to M8", ASSEMBLY-only) — the
            // M-number-pair exclusion above already guards the true fastener-swap collision, so "resize"/"upsize"
            // are safe to own here too when there's no such pair.
            bool verb = Regex.IsMatch(low, @"\b(change|set|make|adjust|update|modify|resize|upsize)\b");
            bool hasNumber = Regex.IsMatch(low, @"(?<![a-z0-9])\d+(\.\d+)?");     // a standalone value, not "d1"/"m6"
            return verb && hasNumber;
        }

        // ---- PREVIEW (Rule #3): resolve the unique target and describe the exact change the pipeline will confirm. ----
        // Returns "D1@Sketch1 is 60mm → 80mm, proceed?" for a clean unique match; a generic line otherwise (the ask
        // for zero/multiple/no-value cases is raised by Run, not here). Never throws.
        public static string Preview(IModelDoc2 model, string intent)
        {
            try
            {
                if (model == null) return null;
                if (!ParseValue(intent, out double reqMm, out _, out bool isRelative)) return null;
                var dims = ReadDims(model);
                if (dims.Count == 0) return null;
                var hits = MatchDims(dims, intent);
                if (hits.Count != 1) return null;
                var d = hits[0];
                if (isRelative) reqMm = d.CurMm + reqMm;   // delta resolves against this unique target's current value
                if (reqMm <= 0) return null;
                if (Math.Abs(d.CurMm - reqMm) <= EpsMm)
                    return d.Full + " is already " + Trim(d.CurMm) + "mm — nothing to change.";
                return d.Full + " is " + Trim(d.CurMm) + "mm → set to " + Trim(reqMm) + "mm, proceed?";
            }
            catch { return null; }
        }

        public static async Task<SetDimensionResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SetDimensionResult();
            if (model == null) { res.Error = "Open a part first — there's no model to set a dimension on."; return res; }

            // test-loop hedged findings change-roller-taper-angle / change-inner-ring-bore: on an ASSEMBLY, the cloud
            // correctly NAMED the target sub-component ("Inner ring with case and rollers-1") but Forge had no code
            // path that actually opened it and edited its dimension — it only ever asked a (correct, but unhelpful)
            // clarifying question about needing the part document open. Same shape as ScalePart's replace-battery
            // fix (FindNamedComponentPart): resolve a component whose name substring-matches the intent's meaningful
            // words and operate on ITS PartDoc directly instead of the bare assembly. Only takes over when a real
            // match is found — an unnamed/ambiguous "change the bore to 25" on a multi-part assembly still falls
            // through to ReadDims(assembly) below (0 dims → the existing honest "no driving dimensions" refusal, or
            // whatever ambiguities-list ask the cloud already raised).
            if (model != null && (int)model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                var namedComp = FindNamedComponentPart(model as AssemblyDoc, intent);
                var namedDoc = namedComp?.GetModelDoc2() as IModelDoc2;
                if (namedDoc != null) model = namedDoc;
            }

            await emit("Scribe", "reading the model's dimensions", "run", null);

            // ---- READ every display dimension in the live model (Rule #8: ground the answer in the real tree). ----
            var dims = ReadDims(model);
            if (dims.Count == 0)
            {
                res.Error = "This model has no editable driving dimensions to set (an imported dumb solid, or nothing dimensioned). It would work on a native part with a feature tree.";
                await emit("Scribe", null, "done", "no driving dimensions found");
                return res;
            }

            // ---- VALUE: never guess one (Rule #2). No number → ask. RELATIVE asks ("make it 2mm bigger", "shrink
            // it by 3mm") carry a DELTA, not a target — the actual new value depends on the resolved dim's current
            // value, so it's computed AFTER the target is found below, not here. ----
            if (!ParseValue(intent, out double reqMm, out string unitEcho, out bool isRelative))
            {
                res.NeedsConfirm = true;
                res.Question = "Set that dimension to what value? e.g. \"change the boss height to 80\", \"set the bore to 25mm\", \"make D1 2.5in\".";
                await emit("Scribe", null, "ask", "no target value stated");
                return res;
            }
            double deltaMm = isRelative ? reqMm : 0;
            if (!isRelative)
            {
                res.RequestedValueMm = reqMm;
                if (reqMm <= 0)
                {
                    res.NeedsConfirm = true;
                    res.Question = "A dimension value has to be positive — did you mean a value like 80mm? (I read " + Trim(reqMm) + "mm" + (unitEcho != null ? " from \"" + unitEcho + "\"" : "") + ".)";
                    await emit("Scribe", null, "ask", "non-positive value");
                    return res;
                }
            }

            // ---- RESOLVE the target dimension: fuzzy-score the phrase against the real dims (Rule #2/#8). ----
            var hits = MatchDims(dims, intent);
            res.MatchedDims = hits.Count;
            if (hits.Count == 0)
            {
                res.NeedsConfirm = true;
                res.Question = "I couldn't tell which dimension you mean. This model has: " + ListDims(dims, 6) + ". Which one should I " +
                               (isRelative ? "change by " + Trim(deltaMm) + "mm" : "set to " + Trim(reqMm) + "mm") + "?";
                await emit("Scribe", null, "ask", "target dimension not found");
                return res;
            }
            if (hits.Count > 1)
            {
                res.NeedsConfirm = true;
                res.Question = "That matches " + hits.Count + " dimensions — which one? " + ListHits(hits, 6) + ". (" +
                               (isRelative ? "Changing by " + Trim(deltaMm) + "mm." : "Setting to " + Trim(reqMm) + "mm.") + ")";
                await emit("Scribe", null, "ask", hits.Count + " candidate dims");
                return res;
            }

            var dim = hits[0];
            res.TargetDim = dim.Full;
            res.OldValueMm = dim.CurMm;
            await emit("Scribe", null, "done", "target: " + dim.Full + " = " + Trim(dim.CurMm) + "mm");

            // ---- RELATIVE delta resolves against the target's OWN current value, now that it's known. ----
            if (isRelative)
            {
                reqMm = dim.CurMm + deltaMm;
                if (reqMm <= 0)
                {
                    res.NeedsConfirm = true;
                    res.Question = dim.Full + " is " + Trim(dim.CurMm) + "mm — reducing it by " + Trim(-deltaMm) + "mm would go non-positive (" + Trim(reqMm) + "mm). What value did you actually want?";
                    await emit("Scribe", null, "ask", "relative change goes non-positive");
                    return res;
                }
                res.RequestedValueMm = reqMm;
            }

            // ---- IDEMPOTENT (Rule #5): already at the requested value → nothing to change. ----
            if (Math.Abs(dim.CurMm - reqMm) <= EpsMm)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.NewValueMm = dim.CurMm;
                res.Info = dim.Full + " is already " + Trim(reqMm) + "mm — nothing to change.";
                await emit("Setter", null, "done", "already at " + Trim(reqMm) + "mm");
                return res;
            }

            // ---- WRITE: set the dimension (metres), then rebuild. Capture the old value for rollback. ----
            await emit("Setter", "setting " + Short(dim.Full) + " to " + Trim(reqMm) + "mm", "run", null);
            double oldMeters = reqMm; // placeholder; overwritten below
            try { oldMeters = dim.Dim.SystemValue; } catch { oldMeters = dim.CurMm / 1000.0; }
            try
            {
                dim.Dim.SystemValue = reqMm / 1000.0;   // SystemValue is metres — same field SetSystemValue3(swThisConfiguration) writes
            }
            catch (Exception ex)
            {
                res.Error = "Couldn't set " + dim.Full + " (" + ex.GetType().Name + ") — SolidWorks refused the value. The dimension is unchanged.";
                await emit("Setter", null, "fail", "value refused");
                return res;
            }

            await emit("Sentinel", "rebuilding and verifying", "run", null);
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);

            // ---- ROLLBACK (Rule #6): a rebuild error after the change → restore the old value, report honestly. ----
            if (res.RebuildErrors > 0)
            {
                try { dim.Dim.SystemValue = oldMeters; } catch { }
                try { model.ForceRebuild3(false); } catch { }
                res.RolledBack = true;
                res.NewValueMm = ReadBackMm(model, dim.Full);
                res.Error = "Setting " + dim.Full + " to " + Trim(reqMm) + "mm broke the rebuild (" + res.RebuildErrors +
                            " error(s)) — reverted to " + Trim(res.OldValueMm) + "mm; the part is unchanged.";
                await emit("Sentinel", null, "fail", "rebuild broke — reverted");
                return res;
            }

            // ---- FAIL CLOSED (Rule #6): re-read the dim FRESH via a direct model.Parameter lookup (a different path
            //      than the traversal that found it) and confirm it landed on the requested value AND rebuild is clean. ----
            res.NewValueMm = ReadBackMm(model, dim.Full);
            bool landed = res.NewValueMm > 0 && Math.Abs(res.NewValueMm - reqMm) <= Math.Max(EpsMm, 1e-4 * reqMm);
            if (!landed)
            {
                // the write did not take (driven/locked dim, config scope) — restore and report, never a fake green.
                try { dim.Dim.SystemValue = oldMeters; } catch { }
                try { model.ForceRebuild3(false); } catch { }
                res.RolledBack = true;
                res.NewValueMm = ReadBackMm(model, dim.Full);
                res.Error = dim.Full + " didn't take the new value (read back " + Trim(res.NewValueMm) + "mm, wanted " +
                            Trim(reqMm) + "mm — it may be driven by an equation or locked) — reverted; the part is unchanged.";
                await emit("Sentinel", null, "fail", "value didn't take — reverted");
                return res;
            }

            res.Verified = true;
            res.Info = dim.Full + " set " + Trim(res.OldValueMm) + "mm → " + Trim(res.NewValueMm) + "mm, rebuild clean. " +
                       "One Ctrl+Z restores it; Forge didn't save.";
            await emit("Sentinel", null, "done", Short(dim.Full) + ": " + Trim(res.OldValueMm) + " → " + Trim(res.NewValueMm) + "mm, clean");
            return res;
        }

        // ============================ named sub-component (assembly) ============================

        // words that talk ABOUT the edit itself, not the target component — stripped before name-matching so
        // "change the roller taper angle to 20 degrees" reduces to {roller, taper, angle}, not {change, degrees}.
        private static readonly string[] SetTalkWords = {
            "change","set","make","adjust","update","modify","the","to","its","maybe","about","roughly",
            "approximately","slip","fit","degree","degrees","deg","mm","millimeter","millimeters","millimetre",
            "millimetres","cm","centimeter","centimeters","inch","inches","please"
        };

        // Same technique as ScalePart.FindNamedComponentPart (test-loop hedged finding replace-battery): resolve a
        // NAMED sub-component so an assembly-level dimension edit can act on that one component's part instead of
        // giving up. Scores by matched-word COUNT (not first-hit) so "inner ring" beats a bare "ring" collision
        // against a same-assembly "outer ring" component.
        private static Component2 FindNamedComponentPart(AssemblyDoc asm, string intent)
        {
            if (asm == null || string.IsNullOrEmpty(intent)) return null;
            var words = new List<string>();
            foreach (Match wm in Regex.Matches(intent.ToLowerInvariant(), @"[a-z]+"))
                if (wm.Value.Length >= 3 && Array.IndexOf(SetTalkWords, wm.Value) < 0) words.Add(wm.Value);
            if (words.Count == 0) return null;

            object[] comps = asm.GetComponents(false) as object[];
            if (comps == null) return null;
            Component2 best = null; int bestScore = 0;
            foreach (var o in comps)
            {
                var c = o as Component2;
                if (c == null || c.IsSuppressed()) continue;
                string nm = (c.Name2 ?? "").ToLowerInvariant();
                int score = 0;
                foreach (var w in words) if (nm.Contains(w)) score++;
                if (score > bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        // ============================ resolve ============================

        private class DimHit
        {
            public Dimension Dim;
            public string Full;    // FullName, e.g. "D1@Sketch1@Part"
            public string Short;   // "D1"
            public string Feature; // owning feature name, e.g. "Boss-Extrude1"
            public double CurMm;    // current value (mm)
            public string[] TypeSyn; // synonym words this dim's TYPE answers to
        }

        // read every display dimension by traversing the feature tree (the docs/SOLIDWORKS-GOTCHAS.md landmine path)
        private static List<DimHit> ReadDims(IModelDoc2 model)
        {
            var list = new List<DimHit>();
            var seen = new HashSet<string>();
            try
            {
                var feat = model.FirstFeature() as Feature;
                while (feat != null)
                {
                    // Pattern features (CirPattern/LPattern/SketchDrivenPattern/…) expose non-length driving
                    // "dimensions" — instance COUNT and total sweep ANGLE — through the exact same Dimension/
                    // SystemValue API as a real linear dim, with no distinct swDimensionType_e of their own. Reading
                    // SystemValue*1000 as if it were metres turns a 4-instance count into a bogus "4000mm" and a
                    // 360° sweep into "6283mm" (2*pi radians*1000) — both LOOK like plausible linear values and
                    // pollute SetDimension's fuzzy match (test-loop hedged finding rim-change-width: a real 8mm width
                    // edit tied 2-way against CirPattern1/CirPattern2's fake "4000mm"/"12000mm" instance counts,
                    // narrowing from the doc-name fix's 19 candidates but never reaching a unique match). Pattern
                    // edits are EditPatternCount/EditPatternSpacing's territory anyway (same ownership split as the
                    // existing mate exclusion in IsSetDimensionIntent) — skip pattern features here entirely.
                    string typeName = null; try { typeName = feat.GetTypeName2(); } catch { }
                    if (typeName != null && typeName.IndexOf("Pattern", StringComparison.OrdinalIgnoreCase) >= 0)
                    { feat = feat.GetNextFeature() as Feature; continue; }

                    string fname = null; try { fname = feat.Name; } catch { }
                    var dd = feat.GetFirstDisplayDimension() as DisplayDimension;
                    while (dd != null)
                    {
                        var d = dd.GetDimension2(0) as Dimension;
                        if (d != null)
                        {
                            string full = null; try { full = d.FullName; } catch { }
                            if (!string.IsNullOrEmpty(full) && seen.Add(full))
                            {
                                double mm = -1; try { mm = d.SystemValue * 1000.0; } catch { }
                                list.Add(new DimHit
                                {
                                    Dim = d,
                                    Full = full,
                                    Short = full.Split('@')[0],
                                    Feature = fname ?? "",
                                    CurMm = mm,
                                    TypeSyn = TypeSynonyms(d)
                                });
                            }
                        }
                        dd = feat.GetNextDisplayDimension(dd) as DisplayDimension;
                    }
                    feat = feat.GetNextFeature() as Feature;
                }
            }
            catch { }
            return list;
        }

        // fuzzy-score every dim against the target phrase; return the best-scoring dims (ties => the caller asks).
        private static List<DimHit> MatchDims(List<DimHit> dims, string intent)
        {
            var tokens = TargetTokens(intent);
            if (tokens.Count == 0) return new List<DimHit>();

            // Full is "D1@Sketch2@Rim.Part" — the trailing "@<document name>" segment is IDENTICAL across every
            // dimension in the doc, so a command that names the part/assembly itself ("set RIM width to 8") made
            // "rim" match every single dim's fullL.Contains(t) (score 1 each) and swamped a genuinely narrower
            // match, turning a typo'd-but-answerable request into a 19-way tie (test-loop hedged finding
            // rim-change-width). Strip that non-discriminating trailing segment before substring-matching.
            int best = 0;
            var scored = new List<KeyValuePair<DimHit, int>>();
            foreach (var d in dims)
            {
                string shortL = (d.Short ?? "").ToLowerInvariant();
                string fullRaw = (d.Full ?? "");
                int atLast = fullRaw.LastIndexOf('@');
                string fullL = (atLast > 0 ? fullRaw.Substring(0, atLast) : fullRaw).ToLowerInvariant();
                string featL = (d.Feature ?? "").ToLowerInvariant();
                int score = 0;
                foreach (var t in tokens)
                {
                    if (shortL == t) score += 4;                                   // "D1" spoken verbatim — decisive
                    else if (featL.Length > 0 && featL.Contains(t)) score += 2;    // "boss" → Boss-Extrude1
                    else if (d.TypeSyn.Contains(t)) score += 1;                     // "height"/"bore" → dim TYPE
                    else if (NearestTypeSyn(d.TypeSyn, t) != null) score += 1;      // "widht" (typo) → "width"
                    else if (fullL.Contains(t)) score += 1;
                }
                if (score > 0) scored.Add(new KeyValuePair<DimHit, int>(d, score));
                if (score > best) best = score;
            }
            if (best == 0) return new List<DimHit>();
            return scored.Where(kv => kv.Value == best).Select(kv => kv.Key).ToList();
        }

        // 1-edit Levenshtein tolerance for a typo'd dimension-type word ("widht"->"width", "hieght"->"height"), same
        // technique as ApplyAppearance.NearestColorWord for typo'd color words. Only checked when the exact TypeSyn
        // match already failed, and only for words length >= 4 (avoids 2-3 letter noise matching everything).
        private static string NearestTypeSyn(string[] typeSyn, string t)
        {
            if (t.Length < 4) return null;
            foreach (var k in typeSyn)
                if (Math.Abs(t.Length - k.Length) <= 1 && Levenshtein(t, k) == 1) return k;
            return null;
        }

        // Damerau-Levenshtein (adjacent-transposition included, cost 1): plain Levenshtein counts a swapped pair of
        // adjacent letters as TWO edits ("widht" vs "width" = 2), which missed the single most common typo shape
        // (fat-fingering two keys in the wrong order) — the transposition term below is what makes "widht" register
        // at distance 1 so NearestTypeSyn actually catches it.
        private static int Levenshtein(string a, string b)
        {
            int[,] d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int val = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                        val = Math.Min(val, d[i - 2, j - 2] + 1);
                    d[i, j] = val;
                }
            return d[a.Length, b.Length];
        }

        // meaningful target words: drop the verb, articles, and the value+unit; keep names like "d1", "bore", "height".
        private static List<string> TargetTokens(string intent)
        {
            string cmd = (intent ?? "").ToLowerInvariant();
            // cut everything from the value onward so the number/unit never becomes a "target" token
            var vm = Regex.Match(cmd, @"\bto\s+\d");
            if (vm.Success) cmd = cmd.Substring(0, vm.Index);
            else
            {
                var nm = Regex.Matches(cmd, @"(?<![a-z0-9])\d+(\.\d+)?");
                if (nm.Count > 0) cmd = cmd.Substring(0, nm[nm.Count - 1].Index);
            }
            var stop = new HashSet<string> { "change", "set", "make", "adjust", "update", "modify",
                "the", "a", "an", "to", "value", "of", "dim", "dimension", "please", "its", "it", "this", "that", "on", "for" };
            var outl = new List<string>();
            foreach (Match m in Regex.Matches(cmd, @"[a-z0-9]+"))
            {
                var w = m.Value;
                if (w.Length < 2 || stop.Contains(w)) continue;
                if (!outl.Contains(w)) outl.Add(w);
            }
            return outl;
        }

        // the synonym words a dimension's TYPE answers to. IDimension.GetType() shadows Object.GetType and returns
        // swDimensionType_e (see VariantGenerator.DimTypeName).
        private static string[] TypeSynonyms(Dimension dim)
        {
            int t; try { t = dim.GetType(); } catch { return new string[0]; }
            switch (t)
            {
                case 5: case 6: case 14: case 15:   // radial / diameter
                    // "thread" added for test-loop hedged finding change-thread-size ("resize the valve thread to
                    // 2 inch") — a thread's nominal SIZE is its diameter (NPT/BSP pipe-thread sizes are diameter-
                    // denominated), so a dimensioned bore/boss diameter is the right target to answer to it.
                    return new[] { "diameter", "dia", "bore", "hole", "radius", "rad", "thread" };
                case 2: case 11: case 12:           // linear distances
                    return new[] { "length", "height", "depth", "width", "thickness", "distance", "len", "long" };
                case 3:                             // angular
                    return new[] { "angle", "angular" };
                case 10:                            // chamfer
                    return new[] { "chamfer" };
                default:
                    return new string[0];
            }
        }

        // ============================ read-back / helpers ============================

        // FRESH read of a dimension's value (mm) by a DIRECT name lookup — a different path than the tree traversal
        // that resolved it, so verification is a genuine independent read, not a mirror of the write.
        private static double ReadBackMm(IModelDoc2 model, string fullName)
        {
            try
            {
                var d = model.Parameter(fullName) as Dimension;
                if (d != null) return d.SystemValue * 1000.0;
            }
            catch { }
            return -1;
        }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        private static string ListDims(List<DimHit> dims, int cap)
        {
            var parts = dims.Take(cap).Select(d => d.Short + " (" + Trim(d.CurMm) + "mm)").ToList();
            string s = string.Join(", ", parts);
            if (dims.Count > cap) s += ", …";
            return s;
        }

        private static string ListHits(List<DimHit> hits, int cap)
        {
            var parts = hits.Take(cap).Select(d => d.Full + " = " + Trim(d.CurMm) + "mm").ToList();
            return string.Join(" · ", parts);
        }

        // short label for step lines: the dim id plus its owning sketch/feature, e.g. "D1@Sketch1" (drop the doc scope).
        private static string Short(string full)
        {
            if (string.IsNullOrEmpty(full)) return full;
            var segs = full.Split('@');
            return segs.Length >= 2 ? segs[0] + "@" + segs[1] : segs[0];
        }

        // ---- value parse: "to 80" / "100mm" / "2.5in" / bare trailing number → millimetres. ----
        //   prefers "to <num><unit?>"; else the LAST standalone number (avoids the "1" in "D1"). Unit defaults to mm.
        // RELATIVE delta wording: "make it 2mm bigger", "bigger by 2mm", "shrink the hole by 1mm", "increase the
        // bore 3mm". valueMm is signed (+grow / -shrink) and isRelative=true tells the caller to add it to the
        // resolved target's OWN current value, not treat it as an absolute target (the shape of the bug behind
        // test-loop no-change finding hole-enlarge-and-fillet: "make the hole bigger by 2mm" was previously read as
        // an absolute 2mm target — on a real hole that's almost always a drastic, wrong shrink, not a 2mm growth).
        private const string GrowWords = "bigger|larger|wider|taller|longer|deeper|thicker|increase[sd]?|enlarge[sd]?|grow[n]?|expand(?:ed)?";
        private const string ShrinkWords = "smaller|narrower|shorter|thinner|decrease[sd]?|shrink|reduce[d]?";
        // named groups so re-ordering/nesting can never silently shift a positional index (a real prior class of
        // bug in this file: ScalePart's own relative/absolute regex, see docs/kb/landmines.md tool 76 entry).
        private static readonly Regex GrowRe = new Regex(
            @"\b(?:" + GrowWords + @")\b[\s\w]{0,20}?\bby\s+(?<n>\d+(?:\.\d+)?)\s*(?<u>[a-z""']*)" +
            @"|\bby\s+(?<n>\d+(?:\.\d+)?)\s*(?<u>[a-z""']*)\s+(?:" + GrowWords + @")\b", RegexOptions.IgnoreCase);
        private static readonly Regex ShrinkRe = new Regex(
            @"\b(?:" + ShrinkWords + @")\b[\s\w]{0,20}?\bby\s+(?<n>\d+(?:\.\d+)?)\s*(?<u>[a-z""']*)" +
            @"|\bby\s+(?<n>\d+(?:\.\d+)?)\s*(?<u>[a-z""']*)\s+(?:" + ShrinkWords + @")\b", RegexOptions.IgnoreCase);

        private static bool TryParseDelta(string cmd, out double deltaMm)
        {
            deltaMm = 0;
            var g = GrowRe.Match(cmd);
            var s = ShrinkRe.Match(cmd);
            Match hit = g.Success ? g : (s.Success ? s : null);
            if (hit == null) return false;
            bool grow = hit == g;
            if (!double.TryParse(hit.Groups["n"].Value, out double num)) return false;
            deltaMm = num * UnitToMm(hit.Groups["u"].Value) * (grow ? 1.0 : -1.0);
            return true;
        }

        private static bool ParseValue(string intent, out double valueMm, out string echo, out bool isRelative)
        {
            valueMm = -1; echo = null; isRelative = false;
            string cmd = (intent ?? "").ToLowerInvariant();

            if (TryParseDelta(cmd, out double delta))
            {
                valueMm = delta; isRelative = true; echo = null;
                return true;
            }

            Match m = Regex.Match(cmd, @"\bto\s+(\d+(\.\d+)?)\s*([a-z""']*)");
            if (!m.Success)
            {
                var all = Regex.Matches(cmd, @"(?<![a-z0-9])(\d+(\.\d+)?)\s*([a-z""']*)");
                if (all.Count == 0) return false;
                m = all[all.Count - 1];
            }
            if (!double.TryParse(m.Groups[1].Value, out double num)) return false;
            string unit = m.Groups[3].Value ?? "";
            echo = m.Value.Trim();
            valueMm = num * UnitToMm(unit);
            return true;
        }

        private static double UnitToMm(string unit)
        {
            switch ((unit ?? "").Trim())
            {
                case "mm": case "millimeter": case "millimeters": case "millimetre": case "millimetres": return 1.0;
                case "cm": case "centimeter": case "centimeters": case "centimetre": case "centimetres": return 10.0;
                case "m": case "meter": case "meters": case "metre": case "metres": return 1000.0;
                case "in": case "inch": case "inches": case "\"": return 25.4;
                case "'": return 304.8;   // feet, for completeness
                default: return 1.0;      // no/unknown unit → mm (the shop default)
            }
        }

        private static string Trim(double v) => v.ToString("0.###");
    }
}
