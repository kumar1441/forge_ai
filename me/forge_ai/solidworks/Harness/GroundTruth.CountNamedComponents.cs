using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        /// <summary>
        /// INDEPENDENT count for the count_named_components (READ) handler. Shares the keyword-extraction
        /// regex (a pure text parse, not geometry — re-deriving it would just be a second copy of the same
        /// regex, not an independent measurement) but walks the tree differently: a recursive descent via
        /// IGetChildren() off the root component, not the handler's flat asm.GetComponents(false) call.
        /// </summary>
        public static JObject MeasureCountNamedComponents(IModelDoc2 model, string intent)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["applicable"] = false; return res; }
            res["applicable"] = true;

            var keywords = CountNamedComponents.ExtractKeywords(intent ?? "");
            var normKeywords = new List<string>();
            foreach (var k in keywords) { var n = CountNamedComponents.Normalize(k); if (n.Length > 0) normKeywords.Add(n); }
            res["keywords"] = new JArray(keywords);
            if (normKeywords.Count == 0) { res["count"] = 0; res["suppressed"] = 0; res["fingerprint"] = new JObject { ["count"] = 0 }; return res; }

            int count = 0, suppressed = 0;
            Component2 root = null;
            try { root = model.ConfigurationManager.ActiveConfiguration.GetRootComponent3(true) as Component2; } catch { }
            if (root != null) WalkChildren(root, normKeywords, ref count, ref suppressed);

            res["count"] = count;
            res["suppressed"] = suppressed;
            res["fingerprint"] = new JObject { ["count"] = count };
            return res;
        }

        private static void WalkChildren(Component2 comp, List<string> normKeywords, ref int count, ref int suppressed)
        {
            foreach (var o in (comp.GetChildren() as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                string name = null; try { name = c.Name2; } catch { }
                string file = null;
                try { var p = c.GetPathName(); file = string.IsNullOrEmpty(p) ? null : System.IO.Path.GetFileNameWithoutExtension(p); } catch { }
                string nName = CountNamedComponents.Normalize(name);
                string nFile = CountNamedComponents.Normalize(file);
                bool isMatch = false;
                foreach (var k in normKeywords)
                {
                    if ((nName.Length > 0 && nName.Contains(k)) || (nFile.Length > 0 && nFile.Contains(k))) { isMatch = true; break; }
                }
                if (isMatch)
                {
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                    if (sup) suppressed++; else count++;
                }
                WalkChildren(c, normKeywords, ref count, ref suppressed);
            }
        }
    }
}
