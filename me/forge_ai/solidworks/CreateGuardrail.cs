using System;
using System.Text.RegularExpressions;

namespace Forge.SolidWorks
{
    /// <summary>
    /// CreateGuardrail — honest-refusal gate shared by every from-scratch CREATE handler (sphere/cylinder/plate/
    /// flange/cone/torus/tube) and the harness create-from-scratch branch. A bare request ("create a 50mm sphere")
    /// builds exactly ONE primitive; a request that ALSO names a second feature, a position, or a machining detail
    /// ("create a cylinder with a 10mm hole", "create a flange on top of the sphere", "create a plate with two
    /// flanges on the sides") is more than any single bare-primitive handler can honour. Silently dropping the extra
    /// part — the pre-fix behaviour that returned a plain solid cylinder for "cylinder with a 10mm hole" and still
    /// reported verified=true — is the worst kind of wrong, so the guardrail REFUSES first and honestly. UnsupportedCue
    /// returns the FIRST unsupported cue in the intent, or null when the intent is a bare shape the create handlers
    /// can build. One deliberate tolerance: the annulus primitives (flange/tube) name their OWN central void as a
    /// dimension label ("flange 50mm outer 20mm bore 10mm thick"), so a void word sitting in a plain dimension list
    /// is NOT treated as a compound-feature cue.
    /// </summary>
    public static class CreateGuardrail
    {
        private sealed class Cue
        {
            public string Label;
            public Regex Rx;
            public bool VoidDimension;   // hole/bore: may be the annulus's own dimension label (exemption-eligible)
        }

        // machining / feature-detail cues that no bare primitive can honour
        private static readonly Cue[] FeatureCues =
        {
            NewCue("bolt hole", @"\bbolt\s+holes?\b"),
            NewCue("hex hole",   @"\bhex(?:agonal)?\s+holes?\b"),
            NewCue("keyway",     @"\bkeyways?\b"),
            NewCue("key way",    @"\bkey\s+ways?\b"),
            NewCue("rounded corner", @"\bround(?:ed)?\s+corner(?:s)?\b"),
            NewCue("tapped",     @"\btapped\b"),
            NewCue("thread",     @"\bthread(?:s|ed|ing)?\b"),
            NewCue("fillet",     @"\bfillet(?:s|ed)?\b"),
            NewCue("chamfer",    @"\bchamfer(?:s|ed)?\b"),
            NewCue("drill",      @"\bdrill(?:s|ed|ing)?\b"),
            NewCue("slot",       @"\bslot(?:s)?\b"),
            NewCue("pocket",     @"\bpocket(?:s)?\b"),
            NewCue("groove",     @"\bgroove(?:s)?\b"),
            NewCue("notch",      @"\bnotch(?:es)?\b"),
            NewCue("cut",        @"\bcut(?:s|ting)?\b"),
            NewCue("hexagon",    @"\bhexagons?\b"),
            NewCue("hole",       @"\b(?:hole|holes)\b", true),
            NewCue("bore",       @"\bbore(?:s)?\b",      true)
        };

        // positional / compound cues: the intent places the shape somewhere, on something, or with a size/here cue
        private static readonly Cue[] PositionCues =
        {
            NewCue("through the center", @"\bthrough\s+the\s+cent(?:er|re)\b"),
            NewCue("on the top", @"\bon\s+the\s+top\b"),
            NewCue("on top", @"\bon\s+top\b"),
            NewCue("top of", @"\btop\s+of\b"),
            NewCue("top face", @"\btop\s+face\b"),
            NewCue("in the middle", @"\bin\s+the\s+middle\b"),
            NewCue("in the center", @"\bin\s+the\s+cent(?:er|re)\b"),
            NewCue("center of", @"\bcent(?:er|re)\s+of\b"),
            NewCue("on the side", @"\bon\s+the\s+side(?:s)?\b"),
            NewCue("sides", @"\bsides\b"),
            NewCue("around", @"\baround\b"),
            NewCue("this this", @"\bthis\s+this\b"),
            NewCue("this size", @"\bthis\s+size\b"),
            NewCue("here", @"\bhere\b"),
            NewCue("there", @"\bthere\b")
        };

        // compound connectors: a second clause means the intent asks for more than one bare primitive
        private static readonly Cue[] ConnectorCues =
        {
            NewCue("and",  @"\band\b"),
            NewCue("then", @"\bthen\b"),
            NewCue("plus", @"\bplus\b"),
            NewCue("also", @"\balso\b")
        };

        // dimension-label adjectives that may precede a "N mm <hole|bore>" slot inside a bare annulus spec
        private static readonly string[] DimensionAttrs =
        {
            "outer", "outside", "od", "o.d", "o/d", "inner", "inside", "bore", "hole", "holes",
            "thick", "thickness", "long", "tall", "high", "height", "dia", "diameter", "deep",
            "length", "major", "minor", "tube", "base", "id", "i.d", "i/d"
        };

        /// <summary>
        /// Returns a short human-readable cue when <paramref name="intent"/> names something the bare create handlers
        /// cannot honour (a second feature, a position, a machining detail, a compound connector), else null. The cue
        /// returned is the one appearing FIRST in the intent. Void words that are simply the annulus primitives' own
        /// dimension label ("20mm bore" inside a bare flange spec) do NOT count as compound cues.
        /// </summary>
        public static string UnsupportedCue(string intent)
        {
            if (string.IsNullOrWhiteSpace(intent)) return null;

            int bestStart = int.MaxValue;
            string best = null;
            foreach (Cue cue in AllCues)
            {
                foreach (Match m in cue.Rx.Matches(intent))
                {
                    if (cue.VoidDimension && IsOwnDimensionLabel(intent, m)) continue;
                    if (m.Index < bestStart) { bestStart = m.Index; best = cue.Label; }
                }
            }
            return best;
        }

        // A void word (hole/bore) that sits right after a "N mm" dimension value whose own preceding token is an
        // attribute name is the annulus primitive describing its own central void, not a request to ADD a feature.
        private static bool IsOwnDimensionLabel(string intent, Match m)
        {
            string before = intent.Substring(0, m.Index);
            Match lm = Regex.Match(before, @"(\d+(?:\.\d+)?)\s*mm\s*$");
            if (!lm.Success) return false;
            string seg = intent.Substring(0, lm.Index);
            Match tm = Regex.Match(seg, @"([A-Za-z][A-Za-z0-9./]*)\s*$");
            if (!tm.Success) return false;
            foreach (string a in DimensionAttrs)
                if (string.Equals(a, tm.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static Cue[] AllCues = ConcatAll();

        private static Cue[] ConcatAll()
        {
            var all = new Cue[FeatureCues.Length + PositionCues.Length + ConnectorCues.Length];
            Array.Copy(FeatureCues, all, FeatureCues.Length);
            Array.Copy(PositionCues, 0, all, FeatureCues.Length, PositionCues.Length);
            Array.Copy(ConnectorCues, 0, all, FeatureCues.Length + PositionCues.Length, ConnectorCues.Length);
            return all;
        }

        private static Cue NewCue(string label, string pattern)
        {
            return new Cue { Label = label, Rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) };
        }

        private static Cue NewCue(string label, string pattern, bool voidDimension)
        {
            return new Cue { Label = label, Rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), VoidDimension = voidDimension };
        }
    }
}
