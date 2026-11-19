using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT ghost-reference census — shares NO code with DetectGhostReferences. Same dead-API constraint
        // (mate READS only work by tree traversal on this build), so the traversal shape is necessarily similar, but
        // the CLASSIFICATION is inverted: the handler builds a set of suppressed component names and asks each mate
        // whether it touches one; this walks each mate's entities and asks each referenced COMPONENT whether it is
        // suppressed, straight from the live Component2. Neither reads the other's answer.
        public static JObject MeasureDetectGhostReferences(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["totalMates"] = -1; return res; }

            int totalMates = 0, ghost = 0, liveGhost = 0, suppressedComps = 0;
            var pairs = new JArray();
            try
            {
                foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                    if (sup) suppressedComps++;
                }

                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "MateGroup")
                    {
                        var s = f.GetFirstSubFeature() as Feature;
                        while (s != null)
                        {
                            totalMates++;
                            bool mateSup = false; try { mateSup = s.IsSuppressed(); } catch { }
                            bool touchesDead = false;
                            try
                            {
                                var mate = s.GetSpecificFeature2() as Mate2;
                                if (mate != null)
                                {
                                    int n = 0; try { n = mate.GetMateEntityCount(); } catch { }
                                    for (int i = 0; i < n; i++)
                                    {
                                        var me = mate.MateEntity(i) as MateEntity2;
                                        var comp = me == null ? null : me.ReferenceComponent as Component2;
                                        if (comp == null) continue;
                                        bool sup = false; try { sup = comp.IsSuppressed(); } catch { }   // ask the COMPONENT, not a name set
                                        if (!sup) continue;
                                        touchesDead = true;
                                        string mn = null, cn = null;
                                        try { mn = s.Name; } catch { }
                                        try { cn = comp.Name2; } catch { }
                                        pairs.Add(mn + "|" + cn);
                                    }
                                }
                            }
                            catch { }
                            if (touchesDead) { ghost++; if (!mateSup) liveGhost++; }
                            s = s.GetNextSubFeature() as Feature;
                        }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            res["totalMates"] = totalMates;
            res["suppressedComponents"] = suppressedComps;
            res["ghostMates"] = ghost;
            res["liveGhostMates"] = liveGhost;
            res["pairs"] = pairs;
            return res;
        }
    }
}
