using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT for repair_balloon_references (tool 160). Shares no code with the handler: its own
        // GetFirstView()/GetNextView() walk to find the assembly view (a DIFFERENT walk than the handler's,
        // written separately here) then its own IView.IGetFirstNote()/INote.IGetNext() traversal counting live
        // balloons and any still-unattached ones.
        public static JObject MeasureRepairBalloonReferences(IModelDoc2 model)
        {
            var res = new JObject();
            res["hasAssemblyView"] = false;
            res["balloonCount"] = 0;
            res["orphanedCount"] = 0;
            res["itemTypesCovered"] = 0;

            var dd = model as DrawingDoc;
            if (dd == null) return res;

            View target = null;
            var v = dd.GetFirstView() as IView; bool first = true;
            while (v != null)
            {
                if (!first)
                {
                    string rm = null; try { rm = v.GetReferencedModelName(); } catch { }
                    if (!string.IsNullOrEmpty(rm) && rm.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase))
                    { target = v as View; break; }
                }
                first = false;
                v = v.GetNextView() as IView;
            }
            if (target == null) return res;
            res["hasAssemblyView"] = true;

            int count = 0, orphaned = 0;
            var seen = new HashSet<string>();
            var n = (target as IView).IGetFirstNote();
            while (n != null)
            {
                bool hasBalloon = false; try { hasBalloon = n.HasBalloon(); } catch { }
                if (hasBalloon)
                {
                    count++;
                    bool attached = true; try { attached = n.IsAttached(); } catch { }
                    if (!attached) orphaned++;
                    string txt = null; try { txt = n.GetBomBalloonText(false); } catch { }
                    if (!string.IsNullOrEmpty(txt)) seen.Add(txt.Trim());
                }
                n = n.IGetNext();
            }
            res["balloonCount"] = count;
            res["orphanedCount"] = orphaned;
            res["itemTypesCovered"] = seen.Count;
            return res;
        }
    }
}
