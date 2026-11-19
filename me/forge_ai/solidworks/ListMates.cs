using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ListMatesResult
    {
        public int Total;
        public int Suppressed;
        public Dictionary<string, int> ByType = new Dictionary<string, int>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 7 — list_mates (READ). Every mate in the assembly with its type and suppression state, plus a by-type
    /// breakdown. A support act for understanding an assembly before touching it (pairs with fix_red_wave / diagnose).
    /// The mate-READ COM APIs (GetMates / MateGroup sub-features via the doc) are dead on this 3DEXPERIENCE build, so
    /// this walks the Mates folder in the feature tree — the one method that works here. Read-only; never modifies.
    /// </summary>
    public static class ListMates
    {
        public static bool IsListMatesIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(list|show|what|how many|count|which)\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\bmate(s)?\b") &&
                   !System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(fix|repair|error|broken|red|over.?defin|add|create|delete|remove)\b");
        }

        public static async Task<ListMatesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ListMatesResult();
            if (model as AssemblyDoc == null) { res.Error = "Open the assembly (.SLDASM) to list its mates."; return res; }

            await emit("Reader", "reading the mates folder", "run", null);
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "MateGroup")
                {
                    var s = f.GetFirstSubFeature() as Feature;
                    while (s != null)
                    {
                        res.Total++;
                        bool sup = false; try { sup = s.IsSuppressed(); } catch { }
                        if (sup) res.Suppressed++;
                        string kind = "other";
                        try { var m = s.GetSpecificFeature2() as Mate2; if (m != null) kind = NounOf(m.Type); } catch { }
                        if (!res.ByType.ContainsKey(kind)) res.ByType[kind] = 0;
                        res.ByType[kind]++;
                        s = s.GetNextSubFeature() as Feature;
                    }
                }
                f = f.GetNextFeature() as Feature;
            }

            await emit("Reader", null, "done",
                res.Total + " mate" + (res.Total == 1 ? "" : "s") +
                (res.Suppressed > 0 ? " (" + res.Suppressed + " suppressed)" : ""));

            if (res.Total == 0) { res.Info = "This assembly has no mates."; return res; }

            var parts = new List<string>();
            foreach (var kv in res.ByType) parts.Add(kv.Value + " " + kv.Key);
            var sb = new StringBuilder(res.Total + " mate" + (res.Total == 1 ? "" : "s") + ": " + string.Join(", ", parts.ToArray()) + ".");
            if (res.Suppressed > 0) sb.Append(" " + res.Suppressed + " suppressed.");
            res.Info = sb.ToString();
            return res;
        }

        private static string NounOf(int type)
        {
            switch ((swMateType_e)type)
            {
                case swMateType_e.swMateCOINCIDENT: return "coincident";
                case swMateType_e.swMateCONCENTRIC: return "concentric";
                case swMateType_e.swMatePERPENDICULAR: return "perpendicular";
                case swMateType_e.swMatePARALLEL: return "parallel";
                case swMateType_e.swMateTANGENT: return "tangent";
                case swMateType_e.swMateDISTANCE: return "distance";
                case swMateType_e.swMateANGLE: return "angle";
                case swMateType_e.swMateSYMMETRIC: return "symmetric";
                case swMateType_e.swMateWIDTH: return "width";
                case swMateType_e.swMateCAMFOLLOWER: return "cam";
                case swMateType_e.swMateGEAR: return "gear";
                default: return "other";
            }
        }
    }
}
