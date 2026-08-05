using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for handle_rollback_bar (tool 240) — shares NO code with HandleRollbackBar.cs.
    /// Two independent signals prove the handler is right AND that the rolled-back state is REAL (not a flag lie):
    ///   (1) its own IFeature.IsRolledBack walk (rolledBackCount) — the feature-flag view;
    ///   (2) a GEOMETRY view — count small-radius cylindrical faces (bores) in the solid body. A rolled-back hole cut
    ///       leaves its bore UN-cut, so the rollback fixture (Seed-Hole built, Hole-2 rolled back) has exactly 1 bore,
    ///       vs 2 if the tree were fully built. Geometry that disagrees with the flag would expose a false "rolled back".
    /// Plus the per-name states of Seed-Hole (must be ABOVE the bar) and Hole-2 (must be BELOW it) for the fixture.
    /// Read-only. Known truth (rollback fixture): rolledBackCount 1, cylBores 1, seedHoleRolledBack false, hole2RolledBack true.
    /// Clean part: rolledBackCount 0, barIsSet false.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureRollbackBar(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            int rolledBack = 0, total = 0;
            bool seedRolled = false, hole2Rolled = false, sawSeed = false, sawHole2 = false;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (string.IsNullOrEmpty(tn) || tn.IndexOf("Folder", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        total++;
                        bool r = false; try { r = f.IsRolledBack(); } catch { }
                        if (r) rolledBack++;
                        string nm = null; try { nm = f.Name; } catch { }
                        if (string.Equals(nm, "Seed-Hole", StringComparison.OrdinalIgnoreCase)) { sawSeed = true; seedRolled = r; }
                        if (string.Equals(nm, "Hole-2", StringComparison.OrdinalIgnoreCase)) { sawHole2 = true; hole2Rolled = r; }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            // GEOMETRY signal: count small-radius cylindrical faces (bores) in the solid body. Independent of the
            // feature flags — proves a rolled-back cut really left its geometry un-built.
            int cylBores = 0;
            try
            {
                var bodies = ((PartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var s = face.GetSurface() as Surface; if (s == null || !s.IsCylinder()) continue;
                        double[] cp = s.CylinderParams as double[]; if (cp == null || cp.Length < 7) continue;
                        if (cp[6] < 0.02) cylBores++;   // radius < 20mm => a bore, not a large outer cylinder
                    }
                }
            }
            catch { }

            mo["rolledBackCount"] = rolledBack;
            mo["totalFeatures"] = total;
            mo["barIsSet"] = rolledBack > 0;
            mo["cylBores"] = cylBores;
            mo["sawSeedHole"] = sawSeed;
            mo["sawHole2"] = sawHole2;
            mo["seedHoleRolledBack"] = seedRolled;
            mo["hole2RolledBack"] = hole2Rolled;
            return mo;
        }
    }
}
