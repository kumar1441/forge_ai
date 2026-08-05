using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT single-mate read — shares NO code with GetMateInfo. Given the mate-name fragment, finds the mate
        // by its own traversal and reports its type-number + entity count via IMate2, so the harness can confirm the
        // handler read the right mate's details. Returns type=-1 if not found (handler would have errored).
        public static JObject MeasureGetMateInfo(IModelDoc2 model, string frag)
        {
            var res = new JObject();
            if (model as AssemblyDoc == null || string.IsNullOrEmpty(frag)) { res["type"] = -1; return res; }
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
                        if (nm != null && nm.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var mate = s.GetSpecificFeature2() as Mate2;
                            res["name"] = nm;
                            res["type"] = mate != null ? mate.Type : -1;
                            int n = 0; try { n = mate != null ? mate.GetMateEntityCount() : 0; } catch { }
                            res["entityCount"] = n;
                            bool sup = false; try { sup = s.IsSuppressed(); } catch { }
                            res["suppressed"] = sup;
                            return res;
                        }
                        s = s.GetNextSubFeature() as Feature;
                    }
                }
                f = f.GetNextFeature() as Feature;
            }
            res["type"] = -1;
            return res;
        }

        // parse the mate-name fragment from the intent (its own parse; the type/entity read is what's verified)
        public static string MateFrag(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var mn = System.Text.RegularExpressions.Regex.Match(intent, @"\b(coincident|concentric|distance|parallel|tangent|angle|width|lock)\s*\d*\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mn.Success) return mn.Value.Replace(" ", "");
            var m2 = System.Text.RegularExpressions.Regex.Match(intent, @"mate\s+([A-Za-z0-9_\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m2.Success ? m2.Groups[1].Value : null;
        }
    }
}
