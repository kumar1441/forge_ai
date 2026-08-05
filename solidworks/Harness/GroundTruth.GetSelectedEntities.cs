using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for get_selected_entities (tool 16). Never re-reads live SolidWorks selection —
    /// same non-negotiable as select_face/edge/plane/component (the harness's own ForceRebuild3 between the
    /// handler's run and this measurement drops it). The handler only ever produces a non-empty selection when
    /// the intent carries an embedded "select the X face" sub-command (see GetSelectedEntities.cs class doc), so
    /// this GT re-parses that same sub-command from scratch and reuses MeasureSelectFace's independent
    /// (linked-list, not the handler's array-walk) per-criterion area to cross-check the handler's own reported
    /// pre-select area. With no embedded sub-command the expected count is simply 0 — not a live-state read, a
    /// fact derivable from the intent text itself (a fresh harness case starts with nothing selected).
    /// </summary>
    public static partial class GroundTruth
    {
        private static string GseParsePreSelectCriterion(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bselect\b") || !Regex.IsMatch(c, @"\bface\b")) return null;
            if (Regex.IsMatch(c, @"\b(largest|biggest)\b")) return "largest";
            if (Regex.IsMatch(c, @"\btop\b")) return "top";
            if (Regex.IsMatch(c, @"\bbottom\b")) return "bottom";
            if (Regex.IsMatch(c, @"\bleft\b")) return "left";
            if (Regex.IsMatch(c, @"\bright\b")) return "right";
            return null;
        }

        public static JObject MeasureGetSelectedEntities(IModelDoc2 model, string intent)
        {
            var mo = new JObject();
            string crit = GseParsePreSelectCriterion(intent);
            mo["preSelectCriterion"] = crit;
            if (crit == null) { mo["expectedCount"] = 0; return mo; }

            var sf = MeasureSelectFace(model);
            mo["expectedCount"] = 1;
            string key = "independent" + char.ToUpperInvariant(crit[0]) + crit.Substring(1) + "AreaMm2";
            mo["expectedAreaMm2"] = sf[key];
            return mo;
        }
    }
}
