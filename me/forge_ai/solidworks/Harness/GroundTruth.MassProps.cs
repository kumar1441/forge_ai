using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the get_mass_properties (tool #22) READ handler. Shares NO code with GetMassProps.cs.
    ///
    /// The handler reads the whole-model figures via IModelDocExtension.CreateMassProperty; this GT re-derives volume,
    /// surface area and centre of mass by summing EACH solid body's own IBody2.GetMassProperties (a different API path).
    /// Agreement between the two on a PART is a genuine cross-check, not a mirror. (Part-only: independently summing an
    /// assembly's per-component masses in the correct frames is out of scope; the harness weighs a PART.)
    ///
    /// Because MeasureMassProps never rebuilds/edits/saves, an identical fingerprint on run1 and run2 proves the handler
    /// is read-only (the harness diffs the two blobs).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureMassProps(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            var part = model as PartDoc;
            if (part == null) { d["applicable"] = false; d["reason"] = "active doc is not a part (mass-props GT is part-only)"; return d; }
            d["applicable"] = true;

            object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            double vol = 0, area = 0, cxV = 0, cyV = 0, czV = 0;
            int n = 0;
            foreach (var o in bodies ?? new object[0])
            {
                var b = o as Body2; if (b == null) continue;
                double[] mp = null; try { mp = b.GetMassProperties(1.0) as double[]; } catch { }
                if (mp == null || mp.Length < 5) continue;
                // IBody2.GetMassProperties layout: [0..2]=centre of mass (m), [3]=volume (m^3), [4]=surface area (m^2)
                double bvol = mp[3];
                vol += bvol; area += mp[4];
                cxV += mp[0] * bvol; cyV += mp[1] * bvol; czV += mp[2] * bvol;   // volume-weighted centroid
                n++;
            }

            bool hasSolid = n > 0 && vol > 0;
            d["bodyCount"] = n;
            d["volumeMm3"] = vol * 1e9;
            d["surfaceAreaMm2"] = area * 1e6;
            if (vol > 0)
                d["comMm"] = new JObject { ["x"] = cxV / vol * 1000.0, ["y"] = cyV / vol * 1000.0, ["z"] = czV / vol * 1000.0 };
            d["hasSolid"] = hasSolid;

            // INDEPENDENT material-resolution check (own call, shares no code with GetMassProps.AnyMaterialAssigned):
            // a material NAME can be set but not linked to any database entry (Database comes back empty) — in that
            // case SolidWorks can't resolve a real density and mp.Density silently falls back to water (1000 kg/m3).
            // The handler must treat that as "no usable material", not report a fabricated mass.
            string matName = null, matDb = "";
            try { matName = part.GetMaterialPropertyName2("", out matDb); } catch { }
            d["materialResolved"] = !string.IsNullOrWhiteSpace(matName) && !string.IsNullOrWhiteSpace(matDb);

            int rb = 0; try { rb = model.Extension.GetWhatsWrongCount(); } catch { }
            d["rebuildErrors"] = rb;

            // read-only fingerprint: unchanged across run1/run2 iff the handler wrote nothing
            d["fingerprint"] = new JObject { ["volumeMm3"] = vol * 1e9, ["bodyCount"] = n, ["rebuildErrors"] = rb };
            return d;
        }
    }
}
