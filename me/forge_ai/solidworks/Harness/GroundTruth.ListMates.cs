using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT mate inventory — shares NO code with ListMates. Same reality (the Mates folder in the tree,
        // since the mate-read COM APIs are dead on this build), measured by its own traversal + its own counting, so
        // a handler that miscounts or mis-buckets shows up as a mismatch. Returns total, suppressed, and a by-type map.
        public static JObject MeasureListMates(IModelDoc2 model)
        {
            var res = new JObject();
            if (model as AssemblyDoc == null) { res["error"] = "not an assembly"; return res; }

            int total = 0, suppressed = 0;
            var byType = new JObject();
            var feat = model.FirstFeature() as Feature;
            while (feat != null)
            {
                string tn = ""; try { tn = feat.GetTypeName2(); } catch { }
                if (tn == "MateGroup")
                {
                    var sub = feat.GetFirstSubFeature() as Feature;
                    while (sub != null)
                    {
                        total++;
                        bool sup = false; try { sup = sub.IsSuppressed(); } catch { }
                        if (sup) suppressed++;
                        string kind = "other";
                        try
                        {
                            var m = sub.GetSpecificFeature2() as Mate2;
                            if (m != null) kind = TypeName(m.Type);
                        }
                        catch { }
                        byType[kind] = (byType[kind] != null ? (int)byType[kind] : 0) + 1;
                        sub = sub.GetNextSubFeature() as Feature;
                    }
                }
                feat = feat.GetNextFeature() as Feature;
            }

            res["total"] = total;
            res["suppressed"] = suppressed;
            res["byType"] = byType;
            return res;
        }

        // deliberately its own switch (not shared with ListMates.NounOf) so the two agree only if the reality agrees
        private static string TypeName(int t)
        {
            switch (t)
            {
                case 0: return "coincident";
                case 1: return "concentric";
                case 2: return "perpendicular";
                case 3: return "parallel";
                case 4: return "tangent";
                case 5: return "distance";
                case 6: return "angle";
                case 9: return "symmetric";
                case 11: return "width";
                case 7: return "cam";
                case 8: return "gear";
                default: return "other";
            }
        }
    }
}
