using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetMateInfoResult
    {
        public string Name;
        public string Type;
        public int EntityCount = -1;   // components/faces the mate references
        public bool Suppressed;
        public List<string> Components = new List<string>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 8 — get_mate_info (READ). One named mate's details: type, suppression, and the components it references.
    /// The singular counterpart to list_mates. Walks the Mates folder (mate COM-reads are dead here), resolves the
    /// named mate, and reads its type + entity references via IMate2.MateEntity/ReferenceComponent. Read-only.
    /// </summary>
    public static class GetMateInfo
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return (Regex.IsMatch(c, @"\b(info|details|about|what is|describe|tell me about)\b") &&
                    (Regex.IsMatch(c, @"\bmate\b") || Regex.IsMatch(c, @"\b(coincident|concentric|distance|parallel|tangent|angle|width)\s*\d+\b"))) &&
                   !Regex.IsMatch(c, @"\b(all the mates|list|how many|suppress|delete|remove|fix)\b");
        }

        public static async Task<GetMateInfoResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetMateInfoResult();
            if (model as AssemblyDoc == null) { res.Error = "Open the assembly (.SLDASM) to inspect a mate."; return res; }

            string want = null;
            var mn = Regex.Match(intent ?? "", @"\b(coincident|concentric|distance|parallel|tangent|angle|width|lock)\s*\d*\b", RegexOptions.IgnoreCase);
            if (mn.Success) want = mn.Value.Replace(" ", "");
            if (want == null) { var m2 = Regex.Match(intent ?? "", @"mate\s+([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase); if (m2.Success) want = m2.Groups[1].Value; }
            if (string.IsNullOrWhiteSpace(want)) { res.Error = "Which mate? e.g. \"info on the Concentric2 mate\"."; return res; }

            await emit("Reader", "reading mate '" + want + "'", "run", null);
            Feature found = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "MateGroup")
                {
                    var s = f.GetFirstSubFeature() as Feature;
                    while (s != null)
                    {
                        string nm = null; try { nm = s.Name; } catch { }
                        if (nm != null && nm.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) { found = s; break; }
                        s = s.GetNextSubFeature() as Feature;
                    }
                }
                if (found != null) break;
                f = f.GetNextFeature() as Feature;
            }
            if (found == null) { res.Error = "No mate matches '" + want + "'."; await emit("Reader", null, "fail", "no match"); return res; }

            try { res.Name = found.Name; } catch { }
            try { res.Suppressed = found.IsSuppressed(); } catch { }
            try
            {
                var mate = found.GetSpecificFeature2() as Mate2;
                if (mate != null)
                {
                    res.Type = NounOf(mate.Type);
                    int n = 0; try { n = mate.GetMateEntityCount(); } catch { }
                    res.EntityCount = n;
                    for (int i = 0; i < n; i++)
                    {
                        try { var me = mate.MateEntity(i) as MateEntity2; var comp = me?.ReferenceComponent as Component2; if (comp != null) res.Components.Add(comp.Name2); } catch { }
                    }
                }
            }
            catch { }

            await emit("Reader", null, "done", (res.Type ?? "?") + " mate · " + res.EntityCount + " entities" + (res.Suppressed ? " · suppressed" : ""));
            res.Info = "Mate '" + res.Name + "': " + (res.Type ?? "?") + ", " + res.EntityCount + " entities" +
                       (res.Components.Count > 0 ? " (" + string.Join(", ", res.Components.ToArray()) + ")" : "") +
                       (res.Suppressed ? ", suppressed" : "") + ".";
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
                case swMateType_e.swMateWIDTH: return "width";
                default: return "other";
            }
        }
    }
}
