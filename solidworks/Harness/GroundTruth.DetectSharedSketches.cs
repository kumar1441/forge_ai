using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for detect_shared_sketches (tool 252) — shares NO code with DetectSharedSketches.cs.
    /// The handler counts, per SKETCH, the features that consume it (IFeature.GetChildren). This GT crosses the OTHER
    /// direction: it walks every real feature, asks IFeature.GetParents which sketch(es) feed it, and inverts that into
    /// a sketch->consumers map. Any sketch fed to >=2 distinct features is shared. Two opposite APIs must agree on the
    /// shared-sketch set, so a disagreement exposes a bad traversal, not a bad rule. Read-only. Known truth:
    ///   shared-sketch-block -> 1 shared sketch (the seed hole's sketch drives the Seed-Hole cut AND a boss), max 2 consumers
    ///   props-block (clean) -> 0 shared sketches (each sketch drives one feature)
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureDetectSharedSketches(IModelDoc2 model)
        {
            var d = new JObject();
            if (!(model is PartDoc)) { d["applicable"] = false; d["reason"] = "not a part"; return d; }
            d["applicable"] = true;

            // sketch name -> set of consuming feature names (inverted GetParents map)
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            int realFeatures = 0;

            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (!string.IsNullOrEmpty(tn) && IsConsumer(tn))
                {
                    string fname = null; try { fname = f.Name; } catch { }
                    realFeatures++;
                    object[] parents = null; try { parents = f.GetParents() as object[]; } catch { }
                    foreach (var o in parents ?? new object[0])
                    {
                        var pf = o as Feature; if (pf == null) continue;
                        string ptn = null; try { ptn = pf.GetTypeName2(); } catch { }
                        if (ptn != "ProfileFeature" && ptn != "3DProfileFeature") continue;
                        string sname = null; try { sname = pf.Name; } catch { }
                        if (string.IsNullOrEmpty(sname) || string.IsNullOrEmpty(fname)) continue;
                        if (!map.TryGetValue(sname, out var set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); map[sname] = set; }
                        set.Add(fname);
                    }
                }
                f = f.GetNextFeature() as Feature;
            }

            var shared = new JArray();
            int sharedCount = 0, maxConsumers = 0;
            foreach (var kv in map)
            {
                if (kv.Value.Count < 2) continue;
                sharedCount++;
                if (kv.Value.Count > maxConsumers) maxConsumers = kv.Value.Count;
                shared.Add(new JObject { ["sketch"] = kv.Key, ["consumers"] = kv.Value.Count });
            }

            d["realFeatures"] = realFeatures;
            d["sharedCount"] = sharedCount;
            d["maxConsumers"] = maxConsumers;
            d["expectedVerdict"] = sharedCount > 0 ? "shared" : "none";
            d["sharedSketches"] = shared;
            return d;
        }

        private static bool IsConsumer(string tn)
        {
            if (tn.EndsWith("Folder", StringComparison.OrdinalIgnoreCase)) return false;
            switch (tn)
            {
                case "ProfileFeature": case "3DProfileFeature":
                case "RefPlane": case "RefAxis": case "OriginProfileFeature": case "CoordSys": return false;
                default: return true;
            }
        }
    }
}
