using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for rebuild_document (tool 95). Shares no code with RebuildDocument.cs. After its own
    /// forced rebuild it reports GetWhatsWrongCount (so the harness can prove clean-vs-flagged discrimination across two
    /// fixtures — a clean block rebuilds to 0, redwave3 stays flagged) plus an independent raw feature-tree count so a
    /// rebuild that silently altered structure would be caught (rebuild must change nothing across run0/run1/run2).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureRebuildDocument(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }
            try { model.ForceRebuild3(false); } catch { }

            int ww = 0; try { ww = model.Extension.GetWhatsWrongCount(); } catch { }

            int featCount = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null) { featCount++; f = f.GetNextFeature() as Feature; }
            }
            catch { }

            mo["whatsWrongCount"] = ww;
            mo["clean"] = ww == 0;
            mo["featureCount"] = featCount;
            return mo;
        }
    }
}
