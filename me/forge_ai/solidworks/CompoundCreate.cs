using System;
using System.Text.RegularExpressions;

namespace Forge.SolidWorks
{
    /// <summary>
    /// CompoundCreate — the parser behind "create X and add Y on top". A bare create intent names exactly ONE primitive
    /// ("create a 50mm cylinder") and is handled by a single create handler. A COMPOUND intent connects a first shape and
    /// a second feature on it with a connector: "create a 100x60x8 plate and add a 20mm boss on top", "create a 50mm
    /// cylinder and add a flange on the top". Before this parser existed those intents were REFUSED by CreateGuardrail
    /// (its "and"/"on top" cues fire), because no single bare-primitive handler could honour both steps. Parse() splits
    /// the connector into a PRIMARY clause (the bare "create/make X" the existing handlers already build) and a SECONDARY
    /// clause (the "add/make/put Y" feature that CreateOnTop then fuses onto the primary's top face), so the caller can
    /// EXECUTE both steps instead of refusing.
    ///
    /// Honest boundaries: an intent with NO connector is not compound. An intent that HAS a connector but where both
    /// shapes cannot be confidently identified (e.g. a bare dimension pair "make a donut 40 and 10 mm", where "10 mm" is
    /// no shape) sets Error — the caller then falls back to the EXISTING honest guardrail refusal, never to silent
    /// truncation. Position words map to "top" (on top / on the top / top face / top of) and "center" (in the center /
    /// middle — treated as top for now, since the top-face builder stands every secondary on the upward face).
    /// </summary>
    public class CompoundResult
    {
        public bool IsCompound;
        public string PrimaryIntent;      // the bare "create/make X + specs" clause routed to one existing create handler
        public string SecondaryIntent;    // the "add/make/put Y" clause handed to CreateOnTop.AddOnTopFace
        public string SecondaryShape;     // canonical shape word of the second clause (flange/cylinder/boss/block/plate/…)
        public string Position = "top";   // "top" or "center" (center is treated as top today)
        public string Error;              // null when the parse is confident; else an honest fall-back cue for the guardrail
    }

    public static class CompoundCreate
    {
        // compound connectors: a second clause after one of these means the ask has more than one step. NOTE the very
        // first one ("and") is also what the guardrail refuses on for a NON-split intent — the compound path must run
        // BEFORE CreateGuardrail.UnsupportedCue so a genuine two-step ask is executed, not refused.
        private static readonly Regex ConnectorRx = new Regex(@"\b(and|then|plus|also)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // canonical shape -> the noun words that name it (longest phrases first so "hollow cylinder" wins over "cylinder").
        // Order is irrelevant to matching (earliest-match-in-text wins), but the canonical names are the contract the
        // primary router and CreateOnTop switch on.
        private static readonly string[][] ShapeNouns =
        {
            new[] { "flange", "flanges", "washer", "washers", "annulus", "annuli", "ring", "rings" },
            new[] { "sphere", "spheres", "spherical", "ball", "balls", "orb", "orbs", "globe", "globes" },
            new[] { "cone", "cones", "conical", "frustum", "frustums" },
            new[] { "torus", "tori", "donut", "donuts", "doughnut", "doughnuts" },
            new[] { "tube", "tubes", "pipe", "pipes", "hollow cylinder", "hollow cylindrical", "sleeve", "sleeves" },
            new[] { "cylinder", "cylinders", "cylindrical", "pillar", "pillars", "rod", "rods", "shaft", "shafts", "axle", "axles", "spindle", "spindles" },
            new[] { "plate", "plates", "panel", "panels", "slab", "slabs", "blank", "rectangular" },
            new[] { "boss", "bosses", "pad", "pads", "stud", "studs", "protrusion", "protrusions" },
            new[] { "block", "blocks", "cube", "cubes" }
        };
        private static readonly string[] CanonicalNames =
        {
            "flange", "sphere", "cone", "torus", "tube", "cylinder", "plate", "boss", "block"
        };

        /// <summary>
        /// Parse a create intent into its compound parts. Never throws. Returns a result with IsCompound=false when the
        /// intent names no connector (a bare single-shape create — the caller keeps its existing one-handler path), and
        /// sets Error when a connector exists but both shapes cannot be confidently identified (so the caller falls back
        /// to the honest guardrail refusal instead of silently building a truncated shape).
        /// </summary>
        public static CompoundResult Parse(string intent)
        {
            var r = new CompoundResult();
            if (string.IsNullOrWhiteSpace(intent)) return r;

            string text = intent.Trim();
            Match con = ConnectorRx.Match(text);
            if (!con.Success) return r;                       // no connector -> not compound

            string primaryRaw = text.Substring(0, con.Index).Trim();
            string secondaryRaw = text.Substring(con.Index + con.Length).Trim();
            if (primaryRaw.Length == 0 || secondaryRaw.Length == 0) return r;

            string pShape = FindShape(primaryRaw);
            string sShape = FindShape(secondaryRaw);
            if (pShape == null || sShape == null)
            {
                // connector present but only one side names a shape (e.g. "make a donut 40 and 10 mm"): cannot run two
                // steps -> honest error so the caller falls back to the guardrail (which refuses this exactly as before).
                r.Error = "compound connector found but the intent doesn't name two shapes I can build (" +
                          (pShape == null ? "no shape before \"" + ConnectorWord(text, con) + "\"" : "no shape after \"" + ConnectorWord(text, con) + "\"") + ")";
                return r;
            }

            // only ONE feature on top is supported: a second connector carrying yet another shape after the first
            // secondary (e.g. "… and add a flange and a cone") is a three-step ask -> refuse honestly, never truncate.
            string sTail = ConnectorRx.Replace(secondaryRaw, "\u0001", 1);   // blank the first connector, scan the rest
            int cut = sTail.IndexOf('\u0001');
            if (cut >= 0 && cut < sTail.Length)
            {
                string after = sTail.Substring(cut + 1);
                if (FindShape(after) != null)
                {
                    r.Error = "more than one feature on top isn't supported yet — Forge builds one primary solid then one secondary feature";
                    return r;
                }
            }

            r.IsCompound = true;
            r.PrimaryIntent = primaryRaw;
            r.SecondaryIntent = secondaryRaw;
            r.SecondaryShape = sShape;
            r.Position = PositionOf(secondaryRaw) ?? PositionOf(text) ?? "top";
            return r;
        }

        /// <summary>
        /// Route a PRIMARY clause to the create-handler action the harness/panel switch on. Specific-first, mirroring the
        /// existing dispatch order (tube before the widened create_cylinder matcher, which also fires on tube|pipe). The
        /// plate family uses the SAME widened offline net the panel intercepts use, so a primary that names a plate-family
        /// noun (e.g. "create a block 50x50x50" — no mm suffix, no literal "plate") still lands on create_plate instead of
        /// stranding the compound. Returns null when no from-scratch handler matches the clause.
        /// </summary>
        public static string RoutePrimaryAction(string clause)
        {
            if (string.IsNullOrWhiteSpace(clause)) return null;
            if (CreateSphere.IsIntent(clause)) return "create_sphere";
            if (CreateFlange.IsIntent(clause)) return "create_flange";
            if (CreateCone.IsIntent(clause)) return "create_cone";
            if (CreateTorus.IsIntent(clause)) return "create_torus";
            if (CreateTube.IsIntent(clause)) return "create_tube";
            if (CreateCylinder.IsIntent(clause)) return "create_cylinder";
            if (IsPlateFamilyCreate(clause)) return "create_plate";
            return null;
        }

        // ----- helpers -----

        // the earliest shape noun in a clause -> its canonical name, or null. E.g. "add a 20mm boss on top" -> "boss",
        // "create a block 50x50x50" -> "block", "10 mm" -> null. Earliest-in-text wins so multi-word phrases like
        // "hollow cylinder" (which contain "cylinder") resolve to the LONGER noun at the earlier index.
        private static string FindShape(string clause)
        {
            if (string.IsNullOrWhiteSpace(clause)) return null;
            int bestIndex = int.MaxValue;
            string bestCanonical = null;
            for (int i = 0; i < ShapeNouns.Length; i++)
            {
                var m = Regex.Match(clause, @"\b(" + string.Join("|", ShapeNouns[i]) + @")\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (m.Success && m.Index < bestIndex)
                {
                    bestIndex = m.Index;
                    bestCanonical = CanonicalNames[i];
                }
            }
            return bestCanonical;
        }

        // position words -> "top" / "center" (first hit in the text wins; null when none is named).
        private static string PositionOf(string clause)
        {
            var onTop = Regex.Match(clause, @"\b(?:on\s+the\s+top|on\s+top|top\s+(?:face|surface)|top\s+of)\b", RegexOptions.IgnoreCase);
            var centre = Regex.Match(clause, @"\b(?:in\s+the\s+cent(?:er|re)|in\s+the\s+middle|middle\s+of|cent(?:er|re)\s+of)\b", RegexOptions.IgnoreCase);
            if (onTop.Success && (!centre.Success || onTop.Index < centre.Index)) return "top";
            if (centre.Success) return "center";
            return null;
        }

        // the raw connector word that was matched, for error text
        private static string ConnectorWord(string text, Match m)
        {
            return text.Substring(m.Index, m.Length);
        }

        // Widened plate-family create net (create verb + a plate-family noun, guarded so document nouns stay on their own
        // routes) — mirrors the panel's IsOfflinePlateIntent, shared here so the harness and panel compound paths route a
        // primary identically. Accepts dims with or without an mm suffix (CreatePlate parses the raw "LxWxT" numbers).
        private static bool IsPlateFamilyCreate(string clause)
        {
            string c = (clause ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(c)) return false;
            if (CreatePlate.IsIntent(c)) return true;
            if (Regex.IsMatch(c, @"\b(part|component|assembly|drawing|mate|sketch|pattern|configur|reference)\b")) return false;
            bool noun = Regex.IsMatch(c, @"\b(plate|panel|slab|blank|block|cube|rectangular)\b");
            bool verb = Regex.IsMatch(c, @"\b(create|make|build|new|start)\b");
            return verb && noun;
        }
    }
}
