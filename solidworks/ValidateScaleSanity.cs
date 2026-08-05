using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ValidateScaleSanityResult
    {
        public bool IsPart;
        public double MaxDimMm;
        public double DxMm, DyMm, DzMm;
        public string Verdict;       // "sane" | "review-scale"
        public string Hypothesis;    // ASCII-only recovered size, e.g. "/25.4 -> 80.0mm (inch->mm import)"
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 254 — validate_scale_sanity (READ). A STEP/IGES import read at the wrong unit comes in 25.4x (inch->mm) or
    /// 1000x (m->mm) too big; the bolt that should be 20mm arrives 508mm. This is a sanity FLAG (offer, don't auto-fix):
    /// read the part's bounding box (IBody2.GetBodyBox — analytic, part-local) and, if the largest dimension exceeds a
    /// machined-part ceiling, report it for human review with the most-likely unit-error hypothesis (which of /25.4 or
    /// /1000 recovers the rounder size). Read-only. The independent GT re-derives the box a DIFFERENT way (the vertex
    /// hull, IBody2.GetVertices) so the two APIs cross-check, and applies the same ceiling. Part-only.
    /// </summary>
    public static class ValidateScaleSanity
    {
        public const double CeilingMm = 1000.0;   // parts larger than 1 m in any dimension are flagged for a unit sanity check

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // scale / unit-sanity vocabulary. Requires a scale/unit-error noun so it never shadows get_bounding_box
            // (a plain size read) or get_document_units (the unit SETTING).
            if (Regex.IsMatch(c, @"\b(scale sanity|scale check|scaling|wrong scale|scaled wrong|right scale|correct scale)\b")) return true;
            if (Regex.IsMatch(c, @"\b(unit|units|inch|imperial|metric)\b") &&
                Regex.IsMatch(c, @"\b(off|wrong|mismatch|misread|import|sanity|too (big|large|small)|error)\b")) return true;
            if (Regex.IsMatch(c, @"\b(is (this|the) (part|model|import|geometry)) (the )?(right|correct) size\b")) return true;
            if (Regex.IsMatch(c, @"\b(does (this|the) (part|model|import)) (look|seem) (the )?(right|correct) (size|scale)\b")) return true;
            return false;
        }

        public static async Task<ValidateScaleSanityResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ValidateScaleSanityResult();
            if (model == null) { res.Error = "Open a part to sanity-check its scale."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "Scale sanity is a part check - open the part, not the assembly."; return res; }
            res.IsPart = true;

            await emit("Sentinel", "checking the part's scale", "run", null);

            double xmin = double.MaxValue, ymin = double.MaxValue, zmin = double.MaxValue;
            double xmax = double.MinValue, ymax = double.MinValue, zmax = double.MinValue;
            int bodies = 0;
            try
            {
                var bs = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var o in bs ?? new object[0])
                {
                    var b = o as Body2; if (b == null) continue;
                    double[] box = null; try { box = b.GetBodyBox() as double[]; } catch { }
                    if (box == null || box.Length < 6) continue;
                    bodies++;
                    if (box[0] < xmin) xmin = box[0]; if (box[1] < ymin) ymin = box[1]; if (box[2] < zmin) zmin = box[2];
                    if (box[3] > xmax) xmax = box[3]; if (box[4] > ymax) ymax = box[4]; if (box[5] > zmax) zmax = box[5];
                }
            }
            catch (Exception ex) { res.Diag = "body box read threw: " + ex.Message; }

            if (bodies == 0 || xmax <= xmin) { res.Error = "No solid body to measure."; return res; }

            res.DxMm = (xmax - xmin) * 1000.0;
            res.DyMm = (ymax - ymin) * 1000.0;
            res.DzMm = (zmax - zmin) * 1000.0;
            res.MaxDimMm = Math.Max(res.DxMm, Math.Max(res.DyMm, res.DzMm));

            if (res.MaxDimMm >= CeilingMm)
            {
                res.Verdict = "review-scale";
                res.Hypothesis = BestHypothesis(res.MaxDimMm);
            }
            else res.Verdict = "sane";

            res.Diag = "verdict=" + res.Verdict + " maxDimMm=" + res.MaxDimMm.ToString("0.##") +
                       " dims=" + res.DxMm.ToString("0.#") + "x" + res.DyMm.ToString("0.#") + "x" + res.DzMm.ToString("0.#") +
                       " bodies=" + bodies + (res.Hypothesis != null ? " hyp=" + res.Hypothesis : "");

            await emit("Sentinel", null, "done",
                res.Verdict == "sane" ? ("scale looks sane (max " + res.MaxDimMm.ToString("0.#") + "mm)")
                                      : ("possible unit error - max " + res.MaxDimMm.ToString("0.#") + "mm; " + res.Hypothesis));

            res.Info = BuildInfo(res);
            return res;
        }

        // Which of /25.4 (inch->mm) or /1000 (m->mm) recovers the "rounder" plausible size? ASCII only (panel eats glyphs).
        private static string BestHypothesis(double maxMm)
        {
            double inch = maxMm / 25.4, metre = maxMm / 1000.0;
            double inchErr = Math.Abs(inch - Math.Round(inch)), metreErr = Math.Abs(metre - Math.Round(metre));
            if (inchErr <= metreErr)
                return "/25.4 -> " + inch.ToString("0.##") + "mm (inch->mm import)";
            return "/1000 -> " + metre.ToString("0.##") + "mm (m->mm import)";
        }

        private static string BuildInfo(ValidateScaleSanityResult r)
        {
            if (r.Verdict == "sane")
                return "Scale looks sane - largest dimension " + r.MaxDimMm.ToString("0.#") + "mm, within a normal machined-part range.";
            var sb = new StringBuilder();
            sb.Append("Possible unit error - largest dimension is " + r.MaxDimMm.ToString("0.#") + "mm (over " + CeilingMm.ToString("0") + "mm).");
            sb.Append("\nLikely a wrong-unit import: " + r.Hypothesis + ". Confirm before converting - never auto-rescale.");
            return sb.ToString();
        }
    }
}
