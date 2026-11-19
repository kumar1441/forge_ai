using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for save_bodies_as_parts (tool 166). Re-counts the SOURCE part's own solid bodies with a
        // fresh GetBodies2 call (never the handler's cached list), re-lists "<folder>\forge-bodies" from disk, and
        // re-OPENS every file found there to confirm exactly one solid body whose volume matches one of the
        // source's own body volumes within 0.1% — the same independent-re-open idiom MeasureImportFile uses once,
        // applied per output file here.
        public static JObject MeasureSaveBodiesAsParts(ISldWorks app, IModelDoc2 model)
        {
            var res = new JObject();
            var part = model as PartDoc;
            string target = null; try { target = model?.GetPathName(); } catch { }
            res["sourcePath"] = target;

            var sourceVols = new List<double>();
            if (part != null)
            {
                foreach (var o in (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]) ?? new object[0])
                {
                    var b = o as Body2; if (b == null) continue;
                    double vol = 0;
                    try { var mp = b.GetMassProperties(0) as double[]; if (mp != null && mp.Length >= 4) vol = mp[3]; } catch { }
                    sourceVols.Add(vol);
                }
            }
            res["sourceBodyCount"] = sourceVols.Count;

            var files = new JArray();
            if (string.IsNullOrWhiteSpace(target))
            {
                res["outputFiles"] = files; res["outputFileCount"] = 0; res["matchedCount"] = 0;
                return res;
            }

            string outDir = Path.Combine(Path.GetDirectoryName(target), "forge-bodies");
            res["outputFolder"] = outDir;
            int matched = 0;
            if (Directory.Exists(outDir))
            {
                foreach (var p in Directory.GetFiles(outDir, "*.SLDPRT"))
                {
                    var fo = new JObject();
                    fo["name"] = Path.GetFileName(p);
                    long len = -1; try { len = new FileInfo(p).Length; } catch { }
                    fo["bytes"] = len;

                    int bodyCount = -1; double vol = -1;
                    IModelDoc2 doc = null;
                    try
                    {
                        int e = 0, w = 0;
                        doc = app.OpenDoc6(p, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref e, ref w) as IModelDoc2;
                        var pd = doc as PartDoc;
                        var bodies = (pd?.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]) ?? new object[0];
                        bodyCount = bodies.Length;
                        if (bodyCount == 1)
                        {
                            var b0 = bodies[0] as Body2;
                            try { var mp = b0?.GetMassProperties(0) as double[]; if (mp != null && mp.Length >= 4) vol = mp[3]; } catch { }
                        }
                    }
                    catch { }
                    finally { try { if (doc != null) app.CloseDoc(doc.GetTitle()); } catch { } }

                    fo["bodyCount"] = bodyCount;
                    fo["volume"] = vol;
                    bool oneBody = bodyCount == 1;
                    bool volOk = false;
                    foreach (var sv in sourceVols)
                    {
                        if (sv <= 0) { if (vol <= 1e-12) { volOk = true; break; } continue; }
                        if (Math.Abs(vol - sv) / sv < 0.001) { volOk = true; break; }
                    }
                    fo["matchesASourceBody"] = oneBody && volOk;
                    if (oneBody && volOk) matched++;
                    files.Add(fo);
                }
            }
            res["outputFiles"] = files;
            res["outputFileCount"] = files.Count;
            res["matchedCount"] = matched;
            return res;
        }
    }
}
