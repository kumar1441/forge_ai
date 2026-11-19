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
        // INDEPENDENT broken-mate census — shares no code with RepairMate.cs. Direction inverted from the handler:
        // this builds the missing-file COMPONENT set FIRST straight from Component2.GetPathName()+File.Exists (same
        // primitive DetectGhostReferences' GroundTruth twin already uses for IsSuppressed, just a different per-
        // component predicate), THEN walks each mate's entities asking whether it touches one of those components —
        // the handler does the reverse (walk each mate first, check its entities' paths inline). Neither reads the
        // other's answer.
        public static JObject MeasureRepairMate(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["totalMates"] = -1; return res; }

            var missingComps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    string cp = null; try { cp = c.GetPathName(); } catch { }
                    if (string.IsNullOrEmpty(cp)) continue;
                    bool exists = false; try { exists = File.Exists(cp); } catch { }
                    if (!exists) missingComps.Add(c.Name2);
                }
            }
            catch { }
            res["missingFileComponents"] = missingComps.Count;

            int totalMates = 0, broken = 0;
            var names = new JArray();
            try
            {
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
                            bool touchesMissing = false;
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
                                        string cn = null; try { cn = comp == null ? null : comp.Name2; } catch { }
                                        if (cn != null && missingComps.Contains(cn)) { touchesMissing = true; break; }
                                    }
                                }
                            }
                            catch { }
                            if (touchesMissing) { broken++; string mn = null; try { mn = s.Name; } catch { } names.Add(mn); }
                            s = s.GetNextSubFeature() as Feature;
                        }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            res["totalMates"] = totalMates;
            res["brokenMates"] = broken;
            res["brokenMateNames"] = names;
            return res;
        }
    }
}
