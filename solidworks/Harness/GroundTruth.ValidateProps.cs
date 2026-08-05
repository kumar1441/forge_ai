using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the ValidateProps (tool #143) release-readiness handler. Deliberately shares
    /// NOTHING with ValidateProps.cs — it re-implements the missing-material, missing/duplicate part-number and
    /// no-weight counts from its own traversal (its own mass read, its own property scan) so the harness can
    /// cross-check the handler's report against a second, unrelated measurement.
    ///
    /// Also carries a read-only fingerprint (component / mate / feature counts): because MeasureValidateProps
    /// never rebuilds, edits, or saves, an identical fingerprint on run1 and run2 is what proves the handler is
    /// read-only (the harness diffs the two GroundTruth blobs).
    /// </summary>
    public static partial class GroundTruth
    {
        // own recognised part-number property names — intentionally a separate literal from the handler's list
        private static readonly string[] VpPartNoProps = { "PartNo", "PartNumber", "Part Number", "Part No", "Number", "DrawingNo", "Drawing Number" };

        public static JObject MeasureValidateProps(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { d["error"] = "active doc is not an assembly"; return d; }

            object[] all = asm.GetComponents(false) as object[];
            object[] top = asm.GetComponents(true) as object[];

            int active = 0, suppressed = 0;
            int uniqueParts = 0, solidParts = 0;
            int missingMat = 0, missingPn = 0, noWeight = 0;
            var missingMatNames = new JArray();
            var missingPnNames = new JArray();
            var noWeightNames = new JArray();
            var pnToPaths = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) { suppressed++; continue; }
                active++;

                string path = null; try { path = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path) || seen.Contains(path)) continue; seen.Add(path);
                var pd = c.GetModelDoc2() as PartDoc; if (pd == null) continue;
                var md = pd as IModelDoc2;
                uniqueParts++;
                string nm = null; try { nm = c.Name2; } catch { }
                if (string.IsNullOrEmpty(nm)) nm = Path.GetFileNameWithoutExtension(path);

                // ---- solid-body count (own read) ----
                object[] bodies = null; try { bodies = pd.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[]; } catch { }
                int solid = bodies == null ? 0 : bodies.Length;

                // ---- missing material (solid parts only) ----
                if (solid > 0)
                {
                    solidParts++;
                    string db = ""; string mat = null; try { mat = pd.GetMaterialPropertyName2("", out db); } catch { }
                    if (string.IsNullOrWhiteSpace(mat))
                    {
                        missingMat++;
                        if (missingMatNames.Count < 10) missingMatNames.Add(nm);
                    }
                }

                // ---- part number (explicit property; else filename fallback for collision grouping) ----
                string explicitPn = VpReadPartNumber(md);
                if (string.IsNullOrWhiteSpace(explicitPn))
                {
                    missingPn++;
                    if (missingPnNames.Count < 10) missingPnNames.Add(nm);
                }
                string effPn = !string.IsNullOrWhiteSpace(explicitPn) ? explicitPn.Trim() : Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(effPn))
                {
                    if (!pnToPaths.ContainsKey(effPn)) pnToPaths[effPn] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    pnToPaths[effPn].Add(path);
                }

                // ---- no computable weight (own mass read; fail closed) ----
                bool nw;
                if (solid == 0) nw = true;
                else
                {
                    double mass = -1; bool measured = false;
                    try { var mp = md.Extension.CreateMassProperty(); if (mp != null) { mass = mp.Mass; measured = true; } } catch { measured = false; }
                    nw = !measured || double.IsNaN(mass) || mass <= 0.0;
                }
                if (nw)
                {
                    noWeight++;
                    if (noWeightNames.Count < 10) noWeightNames.Add(nm);
                }
            }

            int dupPn = 0;
            var dupNames = new JArray();
            foreach (var kv in pnToPaths)
            {
                if (kv.Value.Count < 2) continue;
                dupPn++;
                if (dupNames.Count < 10) dupNames.Add(kv.Key);
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }
            int mateFeatures = CountMateFeaturesLocal(model);   // reuse the shared read-only fingerprint walks
            int featCount = CountFeaturesLocal(model);

            d["activeComponents"] = active;
            d["suppressedComponents"] = suppressed;
            d["topLevelComponents"] = top == null ? 0 : top.Length;
            d["uniqueParts"] = uniqueParts;
            d["solidPartsChecked"] = solidParts;
            d["missingMaterials"] = missingMat;
            d["missingMaterialNames"] = missingMatNames;
            d["missingPartNos"] = missingPn;
            d["missingPartNoNames"] = missingPnNames;
            d["duplicatePartNos"] = dupPn;
            d["duplicatePartNoValues"] = dupNames;
            d["noWeightParts"] = noWeight;
            d["noWeightNames"] = noWeightNames;
            d["totalIssues"] = missingMat + missingPn + dupPn + noWeight;
            d["fingerprint"] = new JObject
            {
                ["activeComponents"] = active,
                ["uniqueParts"] = uniqueParts,
                ["mateFeatures"] = mateFeatures,
                ["featureCount"] = featCount,
                ["rebuildErrors"] = rebuild
            };
            return d;
        }

        // own property scan — separate literal + separate walk from the handler's ReadPartNumber
        private static string VpReadPartNumber(IModelDoc2 md)
        {
            if (md == null) return null;
            string cfg = ""; try { cfg = ((Configuration)md.GetActiveConfiguration()).Name; } catch { }
            foreach (var scope in new[] { cfg, "" })
            {
                CustomPropertyManager cpm = null;
                try { cpm = md.Extension.CustomPropertyManager[scope ?? ""]; } catch { }
                if (cpm == null) continue;
                foreach (var prop in VpPartNoProps)
                {
                    string val = null, resolved = null;
                    try { cpm.Get4(prop, false, out val, out resolved); } catch { }
                    string v = !string.IsNullOrWhiteSpace(resolved) ? resolved : val;
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
            }
            return null;
        }
    }
}
