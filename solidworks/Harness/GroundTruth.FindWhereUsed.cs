using System;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT candidate roster for find_where_used. Deliberately does NO reference resolution of its own — it
        // publishes the RAW folder listing (every .SLDASM/.SLDDRW next to the open document) and lets the harness
        // decide, in PowerShell, which of them SHOULD be parents from the generator's own naming convention. That makes
        // the check a known-truth one rather than two implementations of the same dependency query agreeing with each
        // other, and it makes FALSE POSITIVES visible: the folder holds several unrelated assemblies, and naming a
        // single wrong one fails immediately.
        public static JObject MeasureFindWhereUsed(IModelDoc2 model)
        {
            var res = new JObject();
            var asms = new JArray();
            var drws = new JArray();
            string target = null; try { target = model?.GetPathName(); } catch { }
            res["targetPath"] = target;
            if (string.IsNullOrWhiteSpace(target)) { res["assemblies"] = asms; res["drawings"] = drws; return res; }
            try
            {
                string folder = Path.GetDirectoryName(target);
                res["folder"] = folder;
                foreach (var p in Directory.GetFiles(folder, "*.SLDASM")) asms.Add(Path.GetFileName(p));
                foreach (var p in Directory.GetFiles(folder, "*.SLDDRW")) drws.Add(Path.GetFileName(p));
            }
            catch { }
            res["assemblies"] = asms;
            res["drawings"] = drws;
            res["candidateCount"] = asms.Count + drws.Count;
            return res;
        }
    }
}
