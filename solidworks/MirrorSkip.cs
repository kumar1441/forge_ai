using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class MirrorSkipResult
    {
        public string Plane;         // principal plane used
        public string Axis;          // X/Y/Z
        public int Total;            // top-level components considered
        public int Included;         // components mirrored
        public int Excluded;         // components skipped (hardware/motors/purchased)
        public int Added;            // NEW instances that actually appeared post-rebuild (verified)
        public int Matched;          // of Added, how many sit at a reflected-twin position
        public bool Created;         // this run created the mirror
        public bool AlreadyDone;     // idempotent skip
        public int RebuildErrors;
        public string PreviewLine;   // "mirroring 112 of 150, excluding 38"
        public string Info;
        public string Error;
    }

    /// <summary>
    /// MirrorSkip — demo #1 "Mirror the machine, skip the hardware". Mirrors ONLY the load-bearing structure
    /// across a principal plane while EXCLUDING hardware/fasteners, motors, and purchased/Toolbox parts — the
    /// parts a symmetric build re-uses from the original side rather than reflecting. The WOW is the honest
    /// preview count ("mirroring 112 of 150, excluding 38") the pipeline surfaces before the write.
    ///
    /// Include/exclude is built from IntentLayer.ClassifyKind (hardware) + motor keywords + Toolbox/purchased
    /// detection. Idempotent via a ForgeMirrorSkip feature tag. Verified INDEPENDENTLY by
    /// GroundTruth.MeasureMirrorSkip (which shares no code with this handler).
    /// </summary>
    public static class MirrorSkip
    {
        // exclusion modifier on a mirror command — "mirror everything EXCEPT the hardware", "skip the fasteners".
        public static bool IsSkipIntent(IntentOperation op, string intent)
        {
            string scope = op?.Scope ?? "";
            string blob = ((intent ?? "") + " " + scope).ToLowerInvariant();
            if (!Regex.IsMatch(blob, @"\b(except|exclude|excluding|excludes|skip|skipping|without|but not|leave out|leaving out|minus)\b")) return false;
            return Regex.IsMatch(blob, @"\b(hardware|fastener|fasteners|bolt|bolts|nut|nuts|washer|washers|screw|screws|motor|motors|purchased|toolbox|standard part|standard parts|off.the.shelf|cots)\b");
        }

        // one-line preview the pipeline shows BEFORE the destructive write (Rule #3). Synchronous, read-only.
        public static string PreviewLine(IModelDoc2 model)
        {
            var asm = model as AssemblyDoc; if (asm == null) return null;
            int total, included, excluded; List<Component2> incl;
            Classify(asm, out total, out included, out excluded, out incl);
            if (included == 0) return "nothing to mirror — every one of the " + total + " components is hardware, a motor, or a purchased part";
            return "mirroring " + included + " of " + total + ", excluding " + excluded + " (hardware, motors, purchased)";
        }

        public static async Task<MirrorSkipResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new MirrorSkipResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open an assembly to mirror."; return res; }

            await emit("Gauge", "reading the assembly", "run", null);

            // Idempotency FIRST (Rule #5): the mirror is already in the tree -> add nothing on rerun.
            if (FeatureExists(model, "ForgeMirrorSkip"))
            {
                res.AlreadyDone = true;
                res.Info = "Already mirrored (structure only) — nothing to do.";
                res.PreviewLine = PreviewLine(model);
                await emit("Gauge", null, "done", "already mirrored by Forge (structure only) — skipping");
                return res;
            }

            int total, included, excluded; List<Component2> incl;
            Classify(asm, out total, out included, out excluded, out incl);
            res.Total = total; res.Included = included; res.Excluded = excluded;
            res.PreviewLine = "mirroring " + included + " of " + total + ", excluding " + excluded;
            if (included == 0)
            {
                res.Error = "Nothing to mirror — every component is hardware, a motor, or a purchased part.";
                await emit("Gauge", null, "fail", res.Error);
                return res;
            }
            await emit("Gauge", null, "done", res.PreviewLine + " — excluded = hardware + motors + purchased");

            // choose the principal plane the INCLUDED structure is most offset from, so the reflected copy lands clear.
            int ax = PrincipalAxis(incl);
            string planeName = ax == 0 ? "Right Plane" : (ax == 1 ? "Top Plane" : "Front Plane");
            res.Axis = "XYZ"[ax].ToString(); res.Plane = planeName;

            // capture the pre-mirror world: names present + centroids of the included parts (they don't move),
            // so we can identify the NEW instances and confirm each lands at the reflected position.
            var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inclCentroids = new List<double[]>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = SafeName(c); if (nm.Length > 0) before.Add(nm);
            }
            foreach (var c in incl) { double[] ctr = Centroid(c); if (ctr != null) inclCentroids.Add(ctr); }

            Feature pf = SelectPlane(model, planeName);
            if (pf == null) { res.Error = "Could not find " + planeName; await emit("Mirror", null, "fail", res.Error); return res; }

            await emit("Mirror", "mirroring " + included + " structural components across " + planeName + " (keeping mates)", "run", null);
            object compArr = incl.ToArray();
            int[] orient = new int[incl.Count];
            for (int i = 0; i < orient.Length; i++) orient[i] = (int)swMirrorComponentOrientation_e.swMirrorComponentOrientation_None;
            object ret = null;
            try
            {
                ret = asm.MirrorComponents3(pf, compArr, (object)orient, false, null, false, null,
                    0, "", "", (int)swMirrorPartOptions_e.swMirrorPartOptions_ImportSolids, false, false, false);
            }
            catch (Exception ex) { res.Error = "MirrorComponents3 threw: " + ex.Message; await emit("Mirror", null, "fail", res.Error); return res; }
            model.ForceRebuild3(false);

            Feature mf = ret as Feature; if (mf == null) mf = FindNewestMirrorFeature(model);
            if (mf != null) { try { mf.Name = "ForgeMirrorSkip"; } catch { } }
            res.Created = true;
            await emit("Mirror", null, "done", "mirrored " + included + " structural components on the far side of " + planeName);

            // ---- verify: count the NEW instances + confirm handedness (each sits at the reflected position) ----
            await emit("Sentinel", "verifying handedness and count", "run", null);
            var after = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                after.Add(c);
            }
            int added = 0, matched = 0;
            foreach (var c in after)
            {
                string nm = SafeName(c);
                if (nm.Length == 0 || before.Contains(nm)) continue;   // NEW instance only
                added++;
                double[] ic = Centroid(c); if (ic == null) continue;
                double bestErr = double.MaxValue;
                foreach (var src in inclCentroids)
                {
                    double[] r = new[] { src[0], src[1], src[2] }; r[ax] = -r[ax];   // reflect across the principal plane through origin
                    double d = Math.Sqrt(Sq(ic[0] - r[0]) + Sq(ic[1] - r[1]) + Sq(ic[2] - r[2]));
                    if (d < bestErr) bestErr = d;
                }
                if (bestErr <= 0.002) matched++;   // within 2mm of a reflected source
            }
            res.Added = added; res.Matched = matched;
            int wrong = 0; try { wrong = model.Extension.GetWhatsWrongCount(); } catch { }
            res.RebuildErrors = wrong;
            await emit("Sentinel", null, "done",
                added + " new instances, " + matched + " confirmed at reflected position — " + (wrong == 0 ? "rebuild clean" : wrong + " rebuild flags"));

            res.Info = "Mirrored " + included + " of " + total + " components across " + planeName +
                       ", excluding " + excluded + " (hardware, motors, purchased). Mates kept.";
            return res;
        }

        // ---- include/exclude the top-level components. Excluded = hardware/fasteners + motors + purchased/Toolbox. ----
        private static void Classify(AssemblyDoc asm, out int total, out int included, out int excluded, out List<Component2> include)
        {
            include = new List<Component2>();
            total = 0; excluded = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                total++;
                if (IsExcluded(c)) excluded++; else include.Add(c);
            }
            included = include.Count;
        }

        private static bool IsExcluded(Component2 c)
        {
            string nm = SafeName(c);
            string kind = IntentLayer.ClassifyKind(nm);           // Rule #8: same live classification the parser sees
            if (kind == "bolt" || kind == "nut" || kind == "washer") return true;   // hardware / fasteners
            if (LooksMotor(nm)) return true;                       // motors / drives
            if (IsPurchased(c, nm)) return true;                   // purchased / Toolbox / off-the-shelf
            return false;
        }

        private static bool LooksMotor(string n)
        {
            if (string.IsNullOrEmpty(n)) return false; n = n.ToLowerInvariant();
            foreach (var h in new[] { "motor", "servo", "stepper", "gearmotor", "actuator", "solenoid", "moteur" })
                if (n.Contains(h)) return true;
            return false;
        }

        // purchased / off-the-shelf: a Toolbox path is the strong signal; a short vocabulary of standard-component
        // words covers non-Toolbox purchased parts. Conservative on purpose — a false exclude drops a user's part.
        private static bool IsPurchased(Component2 c, string nm)
        {
            string p = null; try { p = c.GetPathName(); } catch { }
            if (!string.IsNullOrEmpty(p))
            {
                string pl = p.ToLowerInvariant();
                if (pl.Contains("toolbox") || pl.Contains("\\browser\\") || pl.Contains("solidworks data")) return true;
            }
            if (string.IsNullOrEmpty(nm)) return false; nm = nm.ToLowerInvariant();
            foreach (var h in new[] { "bearing", "dowel", "circlip", "retaining ring", "spring pin", "o-ring", "oring", "gasket", "seal", "bushing", "smc", "misumi", "mcmaster" })
                if (nm.Contains(h)) return true;
            return false;
        }

        private static int PrincipalAxis(List<Component2> comps)
        {
            double[] lo = { 1e9, 1e9, 1e9 }, hi = { -1e9, -1e9, -1e9 };
            foreach (var c in comps)
            {
                double[] b = null; try { b = c.GetBox(false, false) as double[]; } catch { }
                if (b != null && b.Length >= 6) { for (int k = 0; k < 3; k++) { if (b[k] < lo[k]) lo[k] = b[k]; if (b[k + 3] > hi[k]) hi[k] = b[k + 3]; } }
            }
            double[] center = { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2 };
            int ax = 0; double best = -1; for (int k = 0; k < 3; k++) if (Math.Abs(center[k]) > best) { best = Math.Abs(center[k]); ax = k; }
            return ax;
        }

        static bool FeatureExists(IModelDoc2 model, string name)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null) { string n = null; try { n = f.Name; } catch { } if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true; f = f.GetNextFeature() as Feature; }
            return false;
        }

        static double[] Centroid(Component2 c)
        {
            try
            {
                double[] b = c.GetBox(false, false) as double[];
                if (b == null || b.Length < 6) return null;
                return new[] { (b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2 };
            }
            catch { return null; }
        }
        static double Sq(double x) => x * x;
        static string SafeName(Component2 c) { try { return c.Name2 ?? ""; } catch { return ""; } }

        static Feature SelectPlane(IModelDoc2 model, string planeName)
        {
            var ext = model.Extension;
            string[] tries = planeName == "Right Plane" ? new[] { "Right Plane", "Right", "Droite", "Plan de droite" }
                : planeName == "Top Plane" ? new[] { "Top Plane", "Top", "Dessus", "Plan de dessus" }
                : new[] { "Front Plane", "Front", "Face", "Plan de face" };
            foreach (var t in tries)
            {
                bool ok = false; try { ok = ext.SelectByID2(t, "PLANE", 0, 0, 0, false, 0, null, 0); } catch { }
                if (ok)
                {
                    var sm = model.SelectionManager as SelectionMgr;
                    var feat = sm.GetSelectedObject6(1, -1) as Feature;
                    model.ClearSelection2(true);
                    if (feat != null) return feat;
                }
            }
            int idx = planeName == "Front Plane" ? 0 : (planeName == "Top Plane" ? 1 : 2);
            int seen = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "RefPlane") { if (seen == idx) return f; seen++; }
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        static Feature FindNewestMirrorFeature(IModelDoc2 model)
        {
            Feature last = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (!string.IsNullOrEmpty(tn) && tn.IndexOf("Mirror", StringComparison.OrdinalIgnoreCase) >= 0) last = f;
                f = f.GetNextFeature() as Feature;
            }
            return last;
        }
    }
}
