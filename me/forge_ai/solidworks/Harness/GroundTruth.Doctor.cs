using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the Doctor (assembly-doctor) handler. Deliberately shares NOTHING with
    /// Doctor.cs — it re-implements broken-reference, missing-material and rebuild-error counting from its own
    /// traversal so the harness can cross-check the handler's report against a second, unrelated measurement.
    ///
    /// Also carries a read-only fingerprint (component / mate / feature counts): because MeasureDoctor never
    /// rebuilds, edits, or saves, an identical fingerprint on run1 and run2 is what proves the handler is
    /// read-only (the harness diffs the two GroundTruth blobs).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureDoctor(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { d["error"] = "active doc is not an assembly"; return d; }

            object[] all = asm.GetComponents(false) as object[];
            object[] top = asm.GetComponents(true) as object[];

            int active = 0, suppressed = 0, broken = 0;
            int missingMat = 0, solidParts = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var brokenNames = new JArray();
            var missingNames = new JArray();

            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) { suppressed++; continue; }
                active++;

                // ---- broken reference (own read): resolved, non-virtual, but file missing on disk or doc won't load ----
                bool virt = false; try { virt = c.IsVirtual; } catch { }
                string path = null; try { path = c.GetPathName(); } catch { }
                if (!virt && !string.IsNullOrEmpty(path))
                {
                    bool missing = false; try { missing = !File.Exists(path); } catch { }
                    bool loadFail = false; try { loadFail = c.GetModelDoc2() == null; } catch { loadFail = true; }
                    if (missing || loadFail)
                    {
                        broken++;
                        if (brokenNames.Count < 10) { string nm = null; try { nm = c.Name2; } catch { } brokenNames.Add(nm); }
                    }
                }

                // ---- missing material (own read, unique parts only): solid part with an empty material name ----
                if (string.IsNullOrEmpty(path) || seen.Contains(path)) continue; seen.Add(path);
                PartDoc pd = null; try { pd = c.GetModelDoc2() as PartDoc; } catch { }
                if (pd == null) continue;
                object[] bodies = null; try { bodies = pd.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[]; } catch { }
                if (bodies == null || bodies.Length == 0) continue;
                solidParts++;
                string db = ""; string mat = null; try { mat = pd.GetMaterialPropertyName2("", out db); } catch { }
                if (string.IsNullOrWhiteSpace(mat))
                {
                    missingMat++;
                    if (missingNames.Count < 10) { string nm = null; try { nm = c.Name2; } catch { } missingNames.Add(nm); }
                }
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }

            // ---- read-only fingerprint: unchanged across run1/run2 iff the handler wrote nothing ----
            int mateFeatures = CountMateFeaturesLocal(model);
            int featCount = CountFeaturesLocal(model);

            d["activeComponents"] = active;
            d["suppressedComponents"] = suppressed;
            d["topLevelComponents"] = top == null ? 0 : top.Length;
            d["brokenRefs"] = broken;
            d["brokenRefNames"] = brokenNames;
            d["solidPartsChecked"] = solidParts;
            d["missingMaterials"] = missingMat;
            d["missingMaterialNames"] = missingNames;
            d["rebuildErrors"] = rebuild;
            d["fingerprint"] = new JObject
            {
                ["activeComponents"] = active,
                ["mateFeatures"] = mateFeatures,
                ["featureCount"] = featCount,
                ["rebuildErrors"] = rebuild
            };
            return d;
        }

        // own feature-tree walks (not the mating-oriented WalkMates above — kept separate so a change there
        // can't silently move this count)
        private static int CountMateFeaturesLocal(IModelDoc2 model)
        {
            int n = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "MateGroup")
                    {
                        var s = f.GetFirstSubFeature() as Feature;
                        while (s != null) { n++; s = s.GetNextSubFeature() as Feature; }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return n;
        }

        private static int CountFeaturesLocal(IModelDoc2 model)
        {
            int n = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null) { n++; f = f.GetNextFeature() as Feature; }
            }
            catch { }
            return n;
        }
    }
}
