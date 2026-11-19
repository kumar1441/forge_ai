using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ApplyAppearanceResult
    {
        public string ColorName;        // the spoken color ("red"), or "by material"
        public string RequestedRgb;     // "255,0,0" for a single color; "N materials → N colors" in by-material mode
        public string TargetFilter;     // what the user asked to color ("bolts", "housing", "all", the named phrase, "by material")
        public int Matched;             // components the target resolved to in the live model
        public int Colored;             // newly colored AND independently confirmed at the requested RGB (read-back)
        public int AlreadyColored;      // matched but already at the requested RGB at run start (idempotency)
        public int Failed;              // attempted but NOT confirmed at the requested RGB afterward (fail closed)
        public string Info;             // verdict-first one-liner
        public string Error;            // set => nothing was colored (ambiguity / wrong doc / unknown color)
    }

    /// <summary>
    /// ApplyAppearance (tool #233 — "color/appearance by filter"). A VISUAL write handler: it sets a component's
    /// display color (the material/appearance color override) — NO geometry is touched, so it is inherently undoable
    /// (one Ctrl+Z per component) and Forge never saves the document.
    ///
    /// Two shapes:
    ///   • "color all the bolts red" / "make the housing blue" / "color the flanges grey" — one COLOR applied to a
    ///     resolved TARGET set (a kind, "all", or a named part, resolved against the LIVE model, Rule #8).
    ///   • "color by material" — a DISTINCT color per material: parts are grouped by their material name and each
    ///     group gets its own palette color.
    ///
    /// Color is set via Component2.SetMaterialPropertyValues2 (double[9]: [0..2]=R,G,B in 0..1, [3..8] optical) at
    /// swThisConfiguration, then GraphicsRedraw2(). Zero target matches → ONE question naming the kinds present
    /// (Rule #2), coloring nothing. Idempotent (Rule #5): a component already at the target RGB is skipped. Fail
    /// closed (Rule #6): Colored counts ONLY components INDEPENDENTLY re-read (GetMaterialPropertyValues2) at the
    /// requested RGB within tolerance — never the set call's success.
    /// </summary>
    public static class ApplyAppearance
    {
        private const double Tol = 0.02;   // ~5/255 — read-back tolerance for a color match

        // spoken color -> RGB in 0..1
        private static readonly Dictionary<string, double[]> Colors = new Dictionary<string, double[]>
        {
            { "red",    new[] { 1.0, 0.0, 0.0 } },
            { "green",  new[] { 0.0, 0.6, 0.0 } },
            { "blue",   new[] { 0.0, 0.0, 1.0 } },
            { "grey",   new[] { 0.5, 0.5, 0.5 } },
            { "gray",   new[] { 0.5, 0.5, 0.5 } },
            { "black",  new[] { 0.05, 0.05, 0.05 } },
            { "white",  new[] { 1.0, 1.0, 1.0 } },
            { "yellow", new[] { 1.0, 0.9, 0.0 } },
            { "orange", new[] { 1.0, 0.5, 0.0 } },
            { "purple", new[] { 0.5, 0.0, 0.5 } },
            { "brown",  new[] { 0.4, 0.2, 0.05 } },
            { "pink",   new[] { 1.0, 0.4, 0.7 } },
            { "cyan",   new[] { 0.0, 0.8, 0.8 } },
        };

        // palette for "color by material" — one distinct color per distinct material, in order
        private static readonly double[][] Palette =
        {
            new[] { 0.85, 0.20, 0.20 }, new[] { 0.20, 0.45, 0.85 }, new[] { 0.25, 0.65, 0.30 },
            new[] { 0.95, 0.75, 0.10 }, new[] { 0.60, 0.30, 0.70 }, new[] { 0.95, 0.55, 0.15 },
            new[] { 0.20, 0.70, 0.70 }, new[] { 0.80, 0.40, 0.60 }, new[] { 0.50, 0.50, 0.50 },
            new[] { 0.45, 0.30, 0.15 },
        };

        // Trigger: a color VERB (color/colour/paint/appearance) OR a bare color adjective, OR "by material" coloring.
        // "make X steel" stays with set_material (steel/brass/… are material words, not in Colors); this fires only on
        // a display-color request. In the offline dispatch this handler is checked BEFORE Materializer so that
        // "color by material" (which contains the word "material") routes here, not to a material change.
        public static bool IsAppearanceIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            cmd = cmd.ToLowerInvariant();
            // "shade" (test-loop hedged finding color-keys: "make each wrench a different shade or something") is a
            // bare synonym for "color" in this domain — no known collision risk (a CAD coloring request is the only
            // sense "shade" takes here; grep of the fixture corpus found zero unrelated uses).
            bool verb = Regex.IsMatch(cmd, @"\b(colou?r|paint|appearance|shade)\b");
            if (verb) return true;                                  // "color the flanges grey", "color by material"
            foreach (var w in Colors.Keys)                          // "make the housing blue" (no verb, bare color)
                if (Regex.IsMatch(cmd, @"\b" + w + @"\b")) return true;
            // The 1-edit fuzzy typo scan below only earns its keep on a genuine color intent with no color/paint
            // verb ("make the base redd"). An unrelated READ/measure request whose trailing noun coincidentally
            // collides with a color name at edit-distance 1 must never get silently rerouted into a WRITE — test-loop
            // wrong-route finding ls7-deck-height: "measure the deck height on this LS block" hijacked into
            // "color it black" ("block" is 1 edit from "black") before the deck-height measurement was ever
            // attempted. Same collision family as belt-clip/back->black and rim-appearance/redo->red (both already
            // fixed by narrowing the scan window) — this one survives the window narrowing because the colliding
            // word genuinely IS the last word of a legitimate, unrelated sentence, so bail on an explicit read/
            // measure verb instead of narrowing further.
            // "whats the bore size" (no apostrophe) missed the "what's"/"what is" bail above — test-loop wrong-route
            // finding ls7-bore-diameter: same "block"->"black" collision as ls7-deck-height, but the contraction-
            // without-apostrophe spelling slipped past what(?:'s|\s+is), so the "block" collision still fired.
            if (Regex.IsMatch(cmd, @"\b(measure|distance|how\s+(far|much|many)|what(?:'s|s\b|\s+is)|weigh|weight|count|list|find|check)\b")) return false;
            return NearestColorWord(cmd) != null;                   // "make the base redd" (typo) — same fuzzy path as ParseColor
        }

        // 1-edit Levenshtein tolerance for a typo'd color word ("redd"->"red", "blu"->"blue"). test-loop hedged finding
        // change-base-color: the assistant re-parses the RAW intent text itself (never trusts the cloud's already-
        // typo-corrected color field — see the ApplyAppearance.Run call site), so an exact \bred\b regex missed "redd"
        // entirely and the handler asked a clarifying question instead of just coloring it red. Only checked when the
        // exact match already failed, and only for whole words length >= 3 (avoids 1-2 letter noise matching everything).
        private static int Levenshtein(string a, string b)
        {
            int[,] d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            return d[a.Length, b.Length];
        }

        // Scanning EVERY word in the whole sentence for a 1-edit color match is too broad: ordinary words collide
        // with color names at edit-distance 1 ("back"->"black", "read"->"red", "link"->"pink", "bold"->"gold"), so
        // an unrelated command like "add a belt clip to the back of this iphone case" got hijacked into recoloring
        // the part (test-loop regression exposed via add-hex-flat/add-belt-clip re-testing, 2026-07-28). The typo'd
        // color word is always the trailing bare adjective ("make the base redd", "color the bolts redd") — restrict
        // the fuzzy scan to the LAST 3 words so an incidental collision buried mid-sentence can't trigger it.
        // Even the last-3 window still collided: test-loop no-change finding rim-change-material-and-appearance,
        // "...and redo mass property" — "redo" sits 3rd-from-last and is 1 edit from "red" (delete the 'o'),
        // so an UNRELATED trailing clause hijacked the whole 3-op set_material/apply_appearance/mass-properties
        // chain into a single miscolored apply_appearance ("coloring the part red"). Both known-good typo cases
        // put the typo'd adjective as the literal LAST word, never 2nd/3rd-from-last — narrow to the last 2 words
        // (one word of slack for a trailing filler like "please") to close this without reopening the belt-clip bug.
        private static string NearestColorWord(string cmd)
        {
            var words = new List<string>();
            foreach (Match wm in Regex.Matches(cmd, @"[a-z]+")) words.Add(wm.Value);
            for (int i = Math.Max(0, words.Count - 2); i < words.Count; i++)
            {
                string w = words[i];
                if (w.Length < 3) continue;
                foreach (var k in Colors.Keys)
                    if (Math.Abs(w.Length - k.Length) <= 1 && Levenshtein(w, k) == 1) return k;
            }
            return null;
        }

        private class Target
        {
            public string Filter;
            public List<Component2> Comps = new List<Component2>();
            public string Ask;
            public bool ByMaterial;
        }

        // spoken color -> rgb; when MULTIPLE color words are present ("switch the green button's color to blue"),
        // the LAST one mentioned is the target (people name the current/reference color before the instruction,
        // e.g. "was red, make it blue" / "green button ... to blue") — pick the rightmost match, not dictionary
        // iteration order (Dictionary enumeration order previously made the FIRST-declared color in the Colors
        // dict win regardless of sentence position, so "green button ... to blue" silently painted it green).
        private static KeyValuePair<string, double[]>? ParseColor(string cmd)
        {
            KeyValuePair<string, double[]>? best = null;
            int bestIdx = -1;
            foreach (var kv in Colors)
            {
                var m = Regex.Match(cmd, @"\b" + kv.Key + @"\b");
                if (m.Success && m.Index > bestIdx) { bestIdx = m.Index; best = kv; }
            }
            if (best != null) return best;
            string near = NearestColorWord(cmd);                    // "redd" -> "red" (see NearestColorWord)
            return near != null ? (KeyValuePair<string, double[]>?)new KeyValuePair<string, double[]>(near, Colors[near]) : null;
        }

        // ---- read-only target resolution: writes nothing, safe to call from PreviewLine and Run ----
        private static Target Resolve(AssemblyDoc asm, string cmd)
        {
            var t = new Target();
            object[] all = asm.GetComponents(false) as object[];

            // ---- "by material" → a distinct color per material; target is every active part-component ----
            if (Regex.IsMatch(cmd, @"\bby (?:their )?material\b|\bper material\b"))
            {
                t.ByMaterial = true;
                t.Filter = "by material";
                foreach (var o in all ?? new object[0])
                {
                    var c = o as Component2; if (c == null || IsSup(c)) continue;
                    if (IsPart(c)) t.Comps.Add(c);
                }
                if (t.Comps.Count == 0) t.Ask = "No parts to color by material here.";
                return t;
            }

            // ---- kind keywords (union of requested kinds) ----
            bool wantFast = Regex.IsMatch(cmd, @"\b(fastener|fasteners|hardware)\b");
            bool wantBolt = wantFast || Regex.IsMatch(cmd, @"\b(bolt|bolts|screw|screws|cap ?screw|cap ?screws)\b");
            bool wantNut = wantFast || Regex.IsMatch(cmd, @"\b(nut|nuts)\b");
            bool wantWasher = wantFast || Regex.IsMatch(cmd, @"\b(washer|washers)\b");
            bool wantFlange = Regex.IsMatch(cmd, @"\b(flange|flanges)\b");
            bool wantHousing = Regex.IsMatch(cmd, @"\b(housing|housings|case|casing|casings|enclosure)\b");
            bool wantShaft = Regex.IsMatch(cmd, @"\b(shaft|shafts)\b");
            bool wantGear = Regex.IsMatch(cmd, @"\b(gear|gears)\b");
            bool wantPlate = Regex.IsMatch(cmd, @"\b(plate|plates)\b");
            bool wantBracket = Regex.IsMatch(cmd, @"\b(bracket|brackets)\b");

            if (wantBolt || wantNut || wantWasher || wantFlange || wantHousing || wantShaft || wantGear || wantPlate || wantBracket)
            {
                foreach (var o in all ?? new object[0])
                {
                    var c = o as Component2; if (c == null || IsSup(c)) continue;
                    string k = Classify(NameOf(c));
                    if ((k == "bolt" && wantBolt) || (k == "nut" && wantNut) || (k == "washer" && wantWasher) ||
                        (k == "flange" && wantFlange) || (k == "housing" && wantHousing) || (k == "shaft" && wantShaft) ||
                        (k == "gear" && wantGear) || (k == "plate" && wantPlate) || (k == "bracket" && wantBracket))
                        t.Comps.Add(c);
                }
                t.Filter = KindLabel(wantFast, wantBolt, wantNut, wantWasher, wantFlange, wantHousing, wantShaft, wantGear, wantPlate, wantBracket);
                if (t.Comps.Count == 0) t.Ask = "No " + t.Filter + " found. " + Present(all);
                return t;
            }

            // ---- "all"/"everything"/"whole assembly" → every active component ----
            // "everything"/"whole/entire assembly"/"the whole thing" are unambiguous no matter what else is in the
            // sentence (e.g. "make the entire bench dark green, everything" carries filler words "bench"/"dark" that
            // ExtractName doesn't recognize as stopwords — those must NOT block the blanket-color read). Bare "all"
            // is a weaker signal (could pair with a named target the kind-keyword checks above already missed), so
            // it keeps the stricter no-leftover-tokens guard.
            bool strongAll = Regex.IsMatch(cmd, @"\b(everything|whole assembly|entire assembly|the whole thing)\b");
            bool weakAll = Regex.IsMatch(cmd, @"\b(all|all parts|all components)\b");
            if (strongAll || (weakAll && ExtractName(cmd).Count == 0))
            {
                foreach (var o in all ?? new object[0])
                {
                    var c = o as Component2; if (c == null || IsSup(c)) continue;
                    t.Comps.Add(c);
                }
                t.Filter = "all";
                if (t.Comps.Count == 0) t.Ask = "Nothing here to color.";
                return t;
            }

            // ---- a specific named part (fuzzy), color words already stripped ----
            var tokens = ExtractName(cmd);
            if (tokens.Count > 0)
            {
                foreach (var o in all ?? new object[0])
                {
                    var c = o as Component2; if (c == null || IsSup(c)) continue;
                    if (MatchesAny(NameOf(c), tokens)) t.Comps.Add(c);
                }
                t.Filter = string.Join(" ", tokens);
            }
            if (t.Comps.Count == 0) t.Ask = "I couldn't tell what to color. " + Present(all);
            return t;
        }

        // broad-op preview (Rule #3): only when the set is >3 and unambiguous, else null (execute directly)
        public static string PreviewLine(IModelDoc2 model, string intent)
        {
            var asm = model as AssemblyDoc; if (asm == null) return null;
            string cmd = (intent ?? "").ToLowerInvariant();
            var t = Resolve(asm, cmd);
            if (t.Ask != null || t.Comps.Count == 0) return null;
            if (t.Comps.Count <= 3) return null;
            if (t.ByMaterial) return "Coloring " + t.Comps.Count + " parts by material (a distinct color per material) — visual only, one Ctrl+Z per part, Forge never saves";
            var col = ParseColor(cmd);
            string cn = col.HasValue ? col.Value.Key : "the chosen color";
            return "Coloring " + t.Comps.Count + " components (" + t.Filter + ") " + cn + " — visual only, one Ctrl+Z per part, Forge never saves";
        }

        public static async Task<ApplyAppearanceResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ApplyAppearanceResult();
            string cmd = (intent ?? "").ToLowerInvariant();

            // ---- PART doc: color the whole part, UNLESS it's a MULTI-BODY part and the ask is for each body to
            //      get a distinct color ("make each wrench a different shade") — test-loop hedged finding color-keys:
            //      an "Allen Key SET" is a single multi-body PART (each key is its own body, not an assembly
            //      component), so the zero-op fallback's "no wrenches found, open an assembly" was itself the bug —
            //      the model never needed an assembly at all. ----
            if (model != null && (int)model.GetType() == (int)swDocumentTypes_e.swDocPART)
            {
                if (IsColorEachDifferently(cmd))
                {
                    var byBody = await TryRunPartByBody(model, emit);
                    if (byBody != null) return byBody;   // >1 body: handled per-body, done
                }
                return await RunPart(model, cmd, emit);
            }

            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open an assembly (or a part) to color."; return res; }

            await emit("Palette", "resolving what to color", "run", null);
            var t = Resolve(asm, cmd);
            res.TargetFilter = t.Filter;
            if (t.Ask != null) { await emit("Palette", null, "fail", t.Ask); res.Error = t.Ask; return res; }

            if (t.ByMaterial) return await RunByMaterial(model, asm, t, res, emit);

            // ---- single color path ----
            var col = ParseColor(cmd);
            if (col == null)
            {
                res.Error = "Which color? Try red, blue, green, grey, black, white, yellow, or orange.";
                await emit("Palette", null, "ask", res.Error);
                return res;
            }
            double[] rgb = col.Value.Value;
            res.ColorName = col.Value.Key;
            res.RequestedRgb = Rgb255(rgb);
            res.Matched = t.Comps.Count;
            await emit("Palette", null, "done", res.Matched + " component" + (res.Matched == 1 ? "" : "s") + " match \"" + t.Filter + "\" → " + res.ColorName);

            // ---- color each, per-item try/continue (Rule #4), idempotent skip (Rule #5) ----
            await emit("Brush", "applying " + res.ColorName, "run", null);
            var attempted = new List<Component2>();
            int idx = 0;
            foreach (var c in t.Comps)
            {
                idx++;
                if (RgbMatch(ReadComp(c), rgb)) { res.AlreadyColored++; continue; }
                try { c.SetMaterialPropertyValues2(BuildProps(ReadComp(c), rgb), (int)swInConfigurationOpts_e.swThisConfiguration, null); attempted.Add(c); }
                catch { res.Failed++; }
                if (res.Matched > 25 && idx % 10 == 0) await emit(null, null, "done", "coloring… " + idx + "/" + res.Matched);
            }
            try { model.GraphicsRedraw2(); } catch { }
            await emit("Brush", null, "done", attempted.Count + " colored · " + res.AlreadyColored + " already " + res.ColorName);

            // ---- FAIL CLOSED (Rule #6): re-read each component's color; count only those confirmed at the requested RGB ----
            await emit("Sentinel", "verifying color by read-back", "run", null);
            foreach (var c in attempted)
            {
                if (RgbMatch(ReadComp(c), rgb)) res.Colored++; else res.Failed++;
            }
            await emit("Sentinel", null, "done",
                res.Colored + " confirmed " + res.ColorName + (res.Failed > 0 ? " · " + res.Failed + " unconfirmed" : ""));

            res.Info = BuildInfo(res);
            return res;
        }

        // ---- PART doc: color the whole part via IModelDoc2.MaterialPropertyValues ----
        private static async Task<ApplyAppearanceResult> RunPart(IModelDoc2 model, string cmd, Func<string, string, string, string, Task> emit)
        {
            var res = new ApplyAppearanceResult { TargetFilter = "this part" };
            var col = ParseColor(cmd);
            if (col == null) { res.Error = "Which color? Try red, blue, green, grey, black, white, yellow, or orange."; return res; }
            double[] rgb = col.Value.Value;
            res.ColorName = col.Value.Key; res.RequestedRgb = Rgb255(rgb); res.Matched = 1;

            await emit("Brush", "coloring the part " + res.ColorName, "run", null);
            double[] before = model.MaterialPropertyValues as double[];
            if (RgbMatch(before, rgb)) { res.AlreadyColored = 1; res.Info = "Part is already " + res.ColorName + " — nothing to do."; await emit("Brush", null, "done", "already " + res.ColorName); return res; }
            try { model.MaterialPropertyValues = BuildProps(before, rgb); } catch { res.Failed = 1; }
            try { model.GraphicsRedraw2(); } catch { }

            await emit("Sentinel", "verifying color by read-back", "run", null);
            if (RgbMatch(model.MaterialPropertyValues as double[], rgb)) { res.Colored = 1; res.Failed = 0; }
            else res.Failed = 1;
            await emit("Sentinel", null, "done", res.Colored == 1 ? "confirmed " + res.ColorName : "could not confirm color");
            res.Info = res.Colored == 1
                ? "Colored the part " + res.ColorName + ". Visual only — one Ctrl+Z undoes it, and the document was not saved."
                : "Couldn't confirm the part turned " + res.ColorName + " — left unchanged.";
            return res;
        }

        // "each"/"different"/"distinct"/"own"/"separate" combined, in either order — "make each wrench a different
        // shade or something" / "give every body its own color". Narrow to word-pairs that ALWAYS mean per-item
        // distinct coloring, never a plain single-color ask ("color it a different shade of blue" has no "each").
        private static readonly Regex ColorEachDifferentlyRe = new Regex(
            @"\beach\b.{0,40}\b(different|distinct|unique|own|separate)\b|\b(different|distinct|unique|separate)\b.{0,40}\b(each|every)\b",
            RegexOptions.IgnoreCase);

        private static bool IsColorEachDifferently(string cmd) => ColorEachDifferentlyRe.IsMatch(cmd ?? "");

        // ---- MULTI-BODY PART doc: a distinct palette color per solid body via IBody2.MaterialPropertyValues2 ----
        //      Returns null (caller falls back to RunPart's single-color path) when the part has <= 1 body — a
        //      single-body part has nothing to distinguish "each" from "the whole part".
        private static async Task<ApplyAppearanceResult> TryRunPartByBody(IModelDoc2 model, Func<string, string, string, string, Task> emit)
        {
            var part = model as PartDoc;
            object[] bodies = null;
            try { bodies = part?.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            if (bodies == null || bodies.Length <= 1) return null;

            var res = new ApplyAppearanceResult { TargetFilter = "each body", ColorName = "by body" };
            res.Matched = bodies.Length;
            res.RequestedRgb = bodies.Length + " bodies → " + Math.Min(bodies.Length, Palette.Length) + " palette color" + (bodies.Length == 1 ? "" : "s");

            await emit("Palette", null, "done", res.Matched + " solid bodies found — assigning a distinct color to each");
            await emit("Brush", "coloring each body", "run", null);
            var attempted = new List<KeyValuePair<Body2, double[]>>();
            for (int idx = 0; idx < bodies.Length; idx++)
            {
                var body = bodies[idx] as Body2; if (body == null) continue;
                double[] rgb = Palette[idx % Palette.Length];
                double[] before = null; try { before = body.MaterialPropertyValues2 as double[]; } catch { }
                if (RgbMatch(before, rgb)) { res.AlreadyColored++; continue; }
                try { body.MaterialPropertyValues2 = BuildProps(before, rgb); attempted.Add(new KeyValuePair<Body2, double[]>(body, rgb)); }
                catch { res.Failed++; }
            }
            try { model.GraphicsRedraw2(); } catch { }
            await emit("Brush", null, "done", attempted.Count + " colored · " + res.AlreadyColored + " already distinct");

            await emit("Sentinel", "verifying color by read-back", "run", null);
            foreach (var kv in attempted)
            {
                double[] after = null; try { after = kv.Key.MaterialPropertyValues2 as double[]; } catch { }
                if (RgbMatch(after, kv.Value)) res.Colored++; else res.Failed++;
            }
            await emit("Sentinel", null, "done", res.Colored + " confirmed distinct" + (res.Failed > 0 ? " · " + res.Failed + " unconfirmed" : ""));

            res.Info = res.Colored > 0 || res.AlreadyColored == bodies.Length
                ? "Colored " + res.Colored + " of " + bodies.Length + " bodies with a distinct color each" +
                  (res.AlreadyColored > 0 ? " (" + res.AlreadyColored + " already distinct)" : "") +
                  ". Visual only — one Ctrl+Z per body undoes it, and the document was not saved."
                : "Couldn't confirm distinct colors on the " + bodies.Length + " bodies — left unchanged.";
            if (res.Colored == 0 && res.AlreadyColored < bodies.Length) res.Error = res.Info;
            return res;
        }

        // ---- "color by material": one distinct palette color per distinct material name ----
        private static async Task<ApplyAppearanceResult> RunByMaterial(IModelDoc2 model, AssemblyDoc asm, Target t, ApplyAppearanceResult res, Func<string, string, string, string, Task> emit)
        {
            res.ColorName = "by material";
            res.Matched = t.Comps.Count;

            // group components by material name, assign each group a palette color
            var order = new List<string>();
            var colorOf = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            var matOf = new Dictionary<Component2, string>();
            foreach (var c in t.Comps)
            {
                string m = MaterialOf(c);
                matOf[c] = m;
                if (!colorOf.ContainsKey(m)) { colorOf[m] = Palette[order.Count % Palette.Length]; order.Add(m); }
            }
            res.RequestedRgb = order.Count + " material" + (order.Count == 1 ? "" : "s") + " → " + order.Count + " color" + (order.Count == 1 ? "" : "s");
            res.TargetFilter = "by material";
            await emit("Palette", null, "done", res.Matched + " parts across " + order.Count + " material" + (order.Count == 1 ? "" : "s"));

            await emit("Brush", "coloring by material", "run", null);
            var attempted = new List<KeyValuePair<Component2, double[]>>();
            int idx = 0;
            foreach (var c in t.Comps)
            {
                idx++;
                double[] rgb = colorOf[matOf[c]];
                if (RgbMatch(ReadComp(c), rgb)) { res.AlreadyColored++; continue; }
                try { c.SetMaterialPropertyValues2(BuildProps(ReadComp(c), rgb), (int)swInConfigurationOpts_e.swThisConfiguration, null); attempted.Add(new KeyValuePair<Component2, double[]>(c, rgb)); }
                catch { res.Failed++; }
                if (res.Matched > 25 && idx % 10 == 0) await emit(null, null, "done", "coloring… " + idx + "/" + res.Matched);
            }
            try { model.GraphicsRedraw2(); } catch { }
            await emit("Brush", null, "done", attempted.Count + " colored · " + res.AlreadyColored + " already matched");

            await emit("Sentinel", "verifying colors by read-back", "run", null);
            foreach (var kv in attempted)
            {
                if (RgbMatch(ReadComp(kv.Key), kv.Value)) res.Colored++; else res.Failed++;
            }
            await emit("Sentinel", null, "done", res.Colored + " confirmed" + (res.Failed > 0 ? " · " + res.Failed + " unconfirmed" : ""));

            res.Info = res.Colored + " of " + res.Matched + " parts colored across " + order.Count + " material" + (order.Count == 1 ? "" : "s") +
                       (res.AlreadyColored > 0 ? " · " + res.AlreadyColored + " already matched" : "") +
                       (res.Failed > 0 ? " · " + res.Failed + " unconfirmed" : "") +
                       ". Visual only — one Ctrl+Z per part, and the document was not saved.";
            return res;
        }

        // ---- verdict first (Character #3), the number not the adjective (Character #2), honest about what was VERIFIED ----
        private static string BuildInfo(ApplyAppearanceResult r)
        {
            if (r.Matched > 0 && r.Colored == 0 && r.Failed == 0 && r.AlreadyColored == r.Matched)
                return "All " + r.Matched + " " + r.TargetFilter + " are already " + r.ColorName + " — nothing to do.";
            var sb = new StringBuilder();
            sb.Append("Colored " + r.Colored + " " + r.TargetFilter + " " + r.ColorName + ".");
            if (r.AlreadyColored > 0) sb.Append(" " + r.AlreadyColored + " already " + r.ColorName + ".");
            if (r.Failed > 0) sb.Append(" " + r.Failed + " couldn't be confirmed — left for review.");
            sb.Append(" Visual only: one Ctrl+Z per part, and the document was not saved.");
            return sb.ToString();
        }

        // ================= color read / write helpers =================

        private static double[] ReadComp(Component2 c)
        {
            try { return c.GetMaterialPropertyValues2((int)swInConfigurationOpts_e.swThisConfiguration, null) as double[]; }
            catch { return null; }
        }

        // preserve optical props [3..8] if present, else sensible defaults; overwrite [0..2] with the requested RGB
        private static double[] BuildProps(double[] existing, double[] rgb)
        {
            double[] p = (existing != null && existing.Length >= 9)
                ? (double[])existing.Clone()
                : new[] { 0.0, 0.0, 0.0, 1.0, 1.0, 0.3, 0.3, 0.0, 0.0 };
            p[0] = rgb[0]; p[1] = rgb[1]; p[2] = rgb[2];
            return p;
        }

        private static bool RgbMatch(double[] a, double[] rgb)
        {
            if (a == null || a.Length < 3) return false;
            return Math.Abs(a[0] - rgb[0]) < Tol && Math.Abs(a[1] - rgb[1]) < Tol && Math.Abs(a[2] - rgb[2]) < Tol;
        }

        private static string Rgb255(double[] rgb)
        {
            return (int)Math.Round(rgb[0] * 255) + "," + (int)Math.Round(rgb[1] * 255) + "," + (int)Math.Round(rgb[2] * 255);
        }

        // ================= model helpers =================

        private static bool IsSup(Component2 c) { try { return c.IsSuppressed(); } catch { return false; } }
        private static string NameOf(Component2 c) { try { return c.Name2; } catch { return null; } }
        private static bool IsPart(Component2 c)
        {
            try { var m = c.GetModelDoc2() as IModelDoc2; return m != null && (int)m.GetType() == (int)swDocumentTypes_e.swDocPART; }
            catch { return false; }
        }
        private static string MaterialOf(Component2 c)
        {
            try { var pd = c.GetModelDoc2() as PartDoc; if (pd == null) return "(no material)"; string db; string m = pd.GetMaterialPropertyName2("", out db); return string.IsNullOrEmpty(m) ? "(no material)" : m; }
            catch { return "(no material)"; }
        }

        // ================= name classification =================

        private static readonly string[] BoltHints = { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud", "vis", "boulon", "bulong", "iso", "din", "b18" };
        private static string Classify(string n)
        {
            if (string.IsNullOrEmpty(n)) return "other";
            n = n.ToLowerInvariant();
            if (n.Contains("nut") || n.Contains("ecrou")) return "nut";
            if (n.Contains("washer") || n.Contains("rondelle")) return "washer";
            if (n.Contains("flange")) return "flange";
            if (n.Contains("housing") || n.Contains("case") || n.Contains("casing") || n.Contains("enclosure")) return "housing";
            if (n.Contains("shaft")) return "shaft";
            if (n.Contains("gear")) return "gear";
            if (n.Contains("plate")) return "plate";
            if (n.Contains("bracket")) return "bracket";
            foreach (var h in BoltHints) if (n.Contains(h)) return "bolt";
            return "other";
        }

        // coarse label for the "what's present" prompt when nothing matched
        private static string Coarse(string n)
        {
            string k = Classify(n);
            if (k == "other") return "other";
            return k + "s";
        }

        private static string Present(object[] all)
        {
            var counts = new Dictionary<string, int>();
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null || IsSup(c)) continue;
                string k = Coarse(NameOf(c));
                int n; counts.TryGetValue(k, out n); counts[k] = n + 1;
            }
            var kinds = new List<KeyValuePair<string, int>>(counts);
            kinds.Sort((a, b) => b.Value.CompareTo(a.Value));
            var labels = new List<string>();
            foreach (var kv in kinds) { labels.Add(kv.Key); if (labels.Count >= 6) break; }
            if (labels.Count == 0) return "What should I color?";
            return "This assembly has: " + string.Join(", ", labels) + ". What should I color?";
        }

        private static string KindLabel(bool fast, bool b, bool n, bool w, bool fl, bool ho, bool sh, bool ge, bool pl, bool br)
        {
            if (fast) return "fasteners";
            var parts = new List<string>();
            if (b) parts.Add("bolts");
            if (n) parts.Add("nuts");
            if (w) parts.Add("washers");
            if (fl) parts.Add("flanges");
            if (ho) parts.Add("housing");
            if (sh) parts.Add("shafts");
            if (ge) parts.Add("gears");
            if (pl) parts.Add("plates");
            if (br) parts.Add("brackets");
            if (parts.Count == 0) return "components";
            if (parts.Count == 1) return parts[0];
            return string.Join(" and ", parts);
        }

        // ================= named-part token extraction (color words + connectives stripped) =================

        private static readonly HashSet<string> Stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","and","all","part","parts","component","components","one","ones","them","it",
            "everything","other","than","just","only","to","color","colour","paint","appearance","make","set",
            "please","turn","whole","entire","assembly","thing","by","material",
            "red","green","blue","grey","gray","black","white","yellow","orange","purple","brown","pink","cyan"
        };

        private static List<string> ExtractName(string cmd)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(cmd)) return list;
            foreach (Match m in Regex.Matches(cmd.ToLowerInvariant(), @"[a-z0-9]+"))
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
    }
}
