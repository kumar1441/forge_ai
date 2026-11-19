using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the "mirror the machine, skip the hardware" handler (MirrorSkip.cs).
    ///
    /// Shares NOTHING with MirrorSkip.cs — its own kind classification, its own Toolbox/motor detection, its own
    /// component read. The harness compares the per-kind top-level counts from the BASELINE run (run0) against the
    /// post-run counts (run1): every EXCLUDED kind (hardware, motors, purchased) must be UNCHANGED (proof none of
    /// them were mirrored), while the includable structure count must have grown. Over-define + rebuild flags are
    /// re-counted here too, so a green result cannot be self-certified by the handler.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureMirrorSkip(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { mo["error"] = "not an assembly"; return mo; }
            try { model.ForceRebuild3(false); } catch { }

            // ForgeMirrorSkip feature present in the tree?
            bool featurePresent = false;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (string.Equals(nm, "ForgeMirrorSkip", StringComparison.OrdinalIgnoreCase)) { featurePresent = true; break; }
                f = f.GetNextFeature() as Feature;
            }
            mo["featurePresent"] = featurePresent;

            // per-kind top-level component tally + over-define, all re-read fresh here
            int total = 0, overDef = 0, included = 0, excluded = 0;
            int bolts = 0, nuts = 0, washers = 0, motors = 0, purchased = 0, other = 0;
            object[] top = asm.GetComponents(true) as object[];
            foreach (var o in top ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                total++;

                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) overDef++;

                string nm = null; try { nm = c.Name2; } catch { }
                string kind = MsKind(nm);
                bool isMotor = MsIsMotor(nm);
                bool isPurch = MsIsPurchased(c, nm);
                if (kind == "bolt") bolts++;
                else if (kind == "nut") nuts++;
                else if (kind == "washer") washers++;
                else if (isMotor) motors++;
                else if (isPurch) purchased++;
                else other++;

                // "excluded" here MUST use the SAME definition the handler is graded against: hardware OR motor OR purchased
                if (kind == "bolt" || kind == "nut" || kind == "washer" || isMotor || isPurch) excluded++;
                else included++;
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["topLevelComponents"] = total;
            mo["overDefinedComponents"] = overDef;
            mo["rebuildErrors"] = rebuild;
            mo["includedCount"] = included;   // structure eligible to mirror
            mo["excludedCount"] = excluded;   // hardware + motors + purchased (must never be mirrored)
            mo["byKind"] = new JObject
            {
                ["bolt"] = bolts,
                ["nut"] = nuts,
                ["washer"] = washers,
                ["motor"] = motors,
                ["purchased"] = purchased,
                ["other"] = other,
                ["included"] = included,
                ["excluded"] = excluded
            };
            return mo;
        }

        // ---- independent copies (NOT shared with MirrorSkip.cs / IntentLayer) ----
        private static string MsKind(string name)
        {
            if (string.IsNullOrEmpty(name)) return "other";
            var n = name.ToLowerInvariant();
            if (Ms(n, "nut", "ecrou", "dai_oc", "4032", "4034", "din 934")) return "nut";
            if (Ms(n, "washer", "rondelle", "dem_ven")) return "washer";
            if (Ms(n, "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud", "vis", "boulon", "bulong", "din 9", "iso 40", "iso 76", "iso 106", "b18")) return "bolt";
            return "other";
        }
        private static bool MsIsMotor(string n)
        {
            if (string.IsNullOrEmpty(n)) return false; n = n.ToLowerInvariant();
            foreach (var h in new[] { "motor", "servo", "stepper", "gearmotor", "actuator", "solenoid", "moteur" }) if (n.Contains(h)) return true;
            return false;
        }
        private static bool MsIsPurchased(Component2 c, string nm)
        {
            string p = null; try { p = c.GetPathName(); } catch { }
            if (!string.IsNullOrEmpty(p))
            {
                string pl = p.ToLowerInvariant();
                if (pl.Contains("toolbox") || pl.Contains("\\browser\\") || pl.Contains("solidworks data")) return true;
            }
            if (string.IsNullOrEmpty(nm)) return false; nm = nm.ToLowerInvariant();
            foreach (var h in new[] { "bearing", "dowel", "circlip", "retaining ring", "spring pin", "o-ring", "oring", "gasket", "seal", "bushing", "smc", "misumi", "mcmaster" }) if (nm.Contains(h)) return true;
            return false;
        }
        private static bool Ms(string n, params string[] xs) { foreach (var x in xs) if (n.Contains(x)) return true; return false; }
    }
}
