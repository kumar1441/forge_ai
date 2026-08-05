using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT tree structure for create_tree_folder. Publishes the RAW top-level walk (name|type) plus, for
        // every FOLDER entry, the names of its sub-features — and nothing else. It has no notion of which folder the
        // handler meant to build or what was supposed to go in it; the harness derives that in PowerShell by diffing
        // the run0 and run1 structures. That is what makes "the feature really MOVED" measurable: a foldered feature
        // must LEAVE the top-level walk and APPEAR under the folder, and a rename could never produce both.
        public static JObject MeasureCreateTreeFolder(IModelDoc2 model)
        {
            var res = new JObject();
            var top = new JArray();
            var folders = new JArray();
            var subs = new JArray();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res["top"] = top; res["folders"] = folders; res["subs"] = subs; return res; }
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    string nm = null; try { nm = f.Name; } catch { }
                    top.Add((nm ?? "") + "|" + (tn ?? ""));

                    // sub-features for EVERY entry, not just folders. This build inlines a feature's CONSUMED sketch
                    // into the flat walk as well (docs/SOLIDWORKS-GOTCHAS.md), so the harness needs the parent/child map to
                    // tell a real folder MEMBER from a consumed sketch that merely sits inside the folder's range.
                    var kids = new JArray();
                    try
                    {
                        var s = f.GetFirstSubFeature() as Feature;
                        while (s != null) { string sn = null; try { sn = s.Name; } catch { } if (!string.IsNullOrEmpty(sn)) kids.Add(sn); s = s.GetNextSubFeature() as Feature; }
                    }
                    catch { }
                    if (kids.Count > 0) subs.Add(new JObject { ["name"] = nm, ["children"] = kids });
                    if (!string.IsNullOrEmpty(tn) && tn.IndexOf("Folder", StringComparison.OrdinalIgnoreCase) >= 0)
                        folders.Add(new JObject { ["name"] = nm, ["type"] = tn, ["children"] = kids });
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["top"] = top;
            res["folders"] = folders;
            res["subs"] = subs;
            res["topCount"] = top.Count;
            return res;
        }
    }
}
