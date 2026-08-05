using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT dependency map — CROSSED against the handler on purpose. The handler asks a feature for its own
        // children (GetChildren) and its own parents (GetParents). This GT never asks the target anything: it walks
        // EVERY feature and inverts the other list — a feature is a child of the target if the target appears in its
        // PARENT list, and a parent of the target if the target appears in its CHILD list. Neither side can confirm
        // its own API, so a dead or lying relationship API shows up as a disagreement instead of a shared lie.
        //
        // Reported for the pattern-block's Seed-Hole (the fixture's one cut), whose KNOWN dependent is LPattern1.
        public static JObject MeasureListFeatureDependencies(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res["target"] = null; return res; }

            const string target = "Seed-Hole";
            var childNames = new JArray();
            var parentNames = new JArray();
            int scanned = 0;
            bool targetPresent = false;

            try
            {
                var all = new List<Feature>();
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (!string.IsNullOrEmpty(tn) && !IsScaffold(tn)) { all.Add(f); scanned++; }
                    f = f.GetNextFeature() as Feature;
                }

                foreach (var cand in all)
                {
                    string nm = null; try { nm = cand.Name; } catch { }
                    if (string.Equals(nm, target, StringComparison.OrdinalIgnoreCase)) { targetPresent = true; continue; }

                    if (Mentions(cand, target, false)) childNames.Add(nm);   // target is cand's PARENT → cand is a child
                    if (Mentions(cand, target, true)) parentNames.Add(nm);   // target is cand's CHILD  → cand is a parent
                }
            }
            catch { }

            res["target"] = target;
            res["targetPresent"] = targetPresent;
            res["scanned"] = scanned;
            res["children"] = childNames;
            res["parents"] = parentNames;
            res["childCount"] = childNames.Count;
            res["parentCount"] = parentNames.Count;
            return res;
        }

        private static bool Mentions(Feature f, string name, bool asChildren)
        {
            object[] arr = null;
            try { arr = (asChildren ? f.GetChildren() : f.GetParents()) as object[]; } catch { }
            foreach (var o in arr ?? new object[0])
            {
                var rf = o as Feature; if (rf == null) continue;
                string n = null; try { n = rf.Name; } catch { }
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
