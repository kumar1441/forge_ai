using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class MirrorResult
    {
        public string Source;   // component mirrored
        public string Plane;    // plane used
        public string Axis;     // X/Y/Z
        public bool Created;     // mirror created this run
        public bool AlreadyDone; // idempotent skip
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Mirror — reflect a component to the other side of a principal plane (demo #? "mirror this").
    /// Gauge picks the top-level component whose PART has exactly one instance (so it is not a patterned
    /// fastener) and the largest offset from a principal plane, so the mirror lands clearly on the far side.
    /// Idempotent via a Forge-Mirror-&lt;name&gt; feature tag. Verified INDEPENDENTLY by GroundTruth (reflected-twin).
    /// </summary>
    public static class Mirror
    {
        public static bool IsMirrorIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\bmirror\b");
        }

        // "mirror the whole assembly" (vs a single component)
        private static bool IsMirrorAll(string cmd) =>
            System.Text.RegularExpressions.Regex.IsMatch(cmd ?? "", @"\b(all|entire|everything|whole|complete|full)\b");

        public static async Task<MirrorResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new MirrorResult();
            var asm = model as AssemblyDoc;
            if (asm == null)
            {
                // test-loop wrong-route finding (mirror-spring, real LEAF SPRING part): the cloud is non-deterministic
                // between action=mirror (this handler, assembly-only) and action=mirror_feature for "need a left-hand
                // version of this part" wording — on a PART doc with no assembly, both must land on the same
                // whole-body-mirror capability instead of this handler flatly refusing. See MirrorFeature.RunWholePart.
                bool isPart = model != null && (int)model.GetType() == (int)swDocumentTypes_e.swDocPART;
                if (isPart && MirrorFeature.WantsWholePartMirror(intent))
                {
                    var mfr = await MirrorFeature.RunWholePart(app, model, intent, emit);
                    res.Created = mfr.Verified && !mfr.AlreadyDone;
                    res.AlreadyDone = mfr.AlreadyDone;
                    res.Error = mfr.Error;
                    res.Info = mfr.Info;
                    return res;
                }
                res.Error = "Open an assembly to mirror."; return res;
            }

            // "mirror the entire assembly" → mirror EVERY top-level component across a principal plane.
            if (IsMirrorAll((intent ?? "").ToLowerInvariant())) return await MirrorAll(asm, model, emit);

            // Idempotency FIRST, keyed to "has Forge already mirrored anything?" — NOT to re-selecting the same
            // source. Mirroring adds a second instance of the source, which makes it no longer "unique", so a
            // source-specific re-check would pick a DIFFERENT part on rerun and mirror endlessly (caught on z-stage).
            if (AnyForgeMirror(model))
            {
                res.AlreadyDone = true; res.Info = "Already mirrored.";
                await emit("Gauge", "reading the assembly", "run", null);
                await emit("Gauge", null, "done", "already mirrored by Forge — skipping");
                return res;
            }

            await emit("Gauge", "reading the assembly", "run", null);
            object[] comps = asm.GetComponents(true) as object[];

            // count instances per part path so we can prefer unique (non-patterned) components
            var pathCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                string p = null; try { p = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(p)) continue;
                pathCount[p] = pathCount.TryGetValue(p, out var n) ? n + 1 : 1;
            }

            // pick the NON-FASTENER component with the largest offset from a principal plane (so the mirror lands
            // clearly on the far side). Prefer a unique part; fall back to any non-fastener. Log every candidate.
            Component2 best = null; int bestAxis = -1; double bestOff = 0; bool bestUnique = false;
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                string p = null; try { p = c.GetPathName(); } catch { }
                int n = (p != null && pathCount.TryGetValue(p, out var cnt)) ? cnt : 1;
                double[] ctr = Centroid(c); if (ctr == null) continue;
                int ax0 = 0; double off0 = 0; for (int ax = 0; ax < 3; ax++) if (Math.Abs(ctr[ax]) > off0) { off0 = Math.Abs(ctr[ax]); ax0 = ax; }
                bool fast = LooksFastener(nm);
                await emit("Gauge", null, "done", "▸ " + nm + " x" + n + (fast ? " [fastener]" : "") + " off=" + (off0 * 1000).ToString("F0") + "mm@" + "XYZ"[ax0]);
                if (fast) continue;
                bool unique = n == 1;
                // prefer a unique part; among same-uniqueness prefer larger offset
                if (best == null || (unique && !bestUnique) || (unique == bestUnique && off0 > bestOff))
                { bestOff = off0; best = c; bestAxis = ax0; bestUnique = unique; }
            }
            if (best == null) { res.Error = "No non-fastener component to mirror."; await emit("Gauge", null, "fail", res.Error); return res; }

            string srcName = best.Name2;
            res.Source = srcName; res.Axis = "XYZ"[bestAxis].ToString();
            string planeName = bestAxis == 0 ? "Right Plane" : (bestAxis == 1 ? "Top Plane" : "Front Plane");
            res.Plane = planeName;
            await emit("Gauge", null, "done", "mirror " + srcName + (bestUnique ? " (unique part)" : "") + " — offset " + (bestOff * 1000).ToString("F0") + "mm from " + planeName + " (axis " + res.Axis + ")");

            string featName = "Forge-Mirror-" + Sanitize(srcName);

            await emit("Mirror", "mirroring " + srcName + " across " + planeName, "run", null);
            Feature planeFeat = SelectPlane(model, planeName);
            if (planeFeat == null) { res.Error = "Could not find " + planeName; await emit("Mirror", null, "fail", res.Error); return res; }

            object compArr = new Component2[] { best };
            object orientArr = new int[] { (int)swMirrorComponentOrientation_e.swMirrorComponentOrientation_None };
            object ret = null;
            try
            {
                ret = asm.MirrorComponents3(planeFeat, compArr, orientArr, false, null, false, null,
                    0, "", "", (int)swMirrorPartOptions_e.swMirrorPartOptions_ImportSolids, false, false, false);
            }
            catch (Exception ex) { res.Error = "MirrorComponents3 threw: " + ex.Message; await emit("Mirror", null, "fail", res.Error); return res; }

            model.ForceRebuild3(false);

            Feature mf = ret as Feature; if (mf == null) mf = FindNewestMirrorFeature(model);
            if (mf != null) { try { mf.Name = featName; } catch { } }
            res.Created = true;
            await emit("Mirror", null, "done", "mirrored instance created on the far side of " + planeName);

            await emit("Sentinel", "checking the assembly solves", "run", null);
            int wrong = 0; try { wrong = model.Extension.GetWhatsWrongCount(); } catch { }
            await emit("Sentinel", null, "done", wrong == 0 ? "rebuild clean" : wrong + " rebuild flags");

            res.Info = "Mirrored " + srcName + " across " + planeName + ".";
            return res;
        }

        // Mirror EVERY top-level component across the principal plane the assembly is most offset from,
        // so a full mirrored copy lands clearly on the far side. Idempotent via a ForgeMirrorAll feature tag.
        private static async Task<MirrorResult> MirrorAll(AssemblyDoc asm, IModelDoc2 model, Func<string, string, string, string, Task> emit)
        {
            var res = new MirrorResult();
            await emit("Gauge", "reading the assembly", "run", null);
            if (FeatureExists(model, "ForgeMirrorAll"))
            {
                res.AlreadyDone = true; res.Info = "The whole assembly is already mirrored.";
                await emit("Gauge", null, "done", "already mirrored the whole assembly — skipping");
                return res;
            }
            object[] comps = asm.GetComponents(true) as object[];
            var list = new List<Component2>();
            double[] lo = { 1e9, 1e9, 1e9 }, hi = { -1e9, -1e9, -1e9 };
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                list.Add(c);
                double[] b = null; try { b = c.GetBox(false, false) as double[]; } catch { }
                if (b != null && b.Length >= 6) { for (int k = 0; k < 3; k++) { if (b[k] < lo[k]) lo[k] = b[k]; if (b[k + 3] > hi[k]) hi[k] = b[k + 3]; } }
            }
            if (list.Count == 0) { res.Error = "No components to mirror."; await emit("Gauge", null, "fail", res.Error); return res; }

            double[] center = { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2 };
            int ax = 0; double best = -1; for (int k = 0; k < 3; k++) if (Math.Abs(center[k]) > best) { best = Math.Abs(center[k]); ax = k; }
            string planeName = ax == 0 ? "Right Plane" : (ax == 1 ? "Top Plane" : "Front Plane");
            res.Plane = planeName; res.Axis = "XYZ"[ax].ToString();
            await emit("Gauge", null, "done", "mirroring all " + list.Count + " components across " + planeName);

            Feature pf = SelectPlane(model, planeName);
            if (pf == null) { res.Error = "Could not find " + planeName; await emit("Mirror", null, "fail", res.Error); return res; }

            // test-loop false-success fix (the regression corpus): capture the pre-mirror name
            // set so a silent API no-op (e.g. lightweight components — MirrorComponents3 can't mirror a component
            // with no resolved bodies, same class as the ResolveAllLightWeightComponents landmine in
            // the test fixture generator) is CAUGHT instead of reported as success.
            var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in list) { string nm = null; try { nm = c.Name2; } catch { } if (!string.IsNullOrEmpty(nm)) before.Add(nm); }
            try { asm.ResolveAllLightWeightComponents(false); } catch { }

            await emit("Mirror", "mirroring the whole assembly across " + planeName, "run", null);
            object compArr = list.ToArray();
            int[] orient = new int[list.Count];
            for (int i = 0; i < orient.Length; i++) orient[i] = (int)swMirrorComponentOrientation_e.swMirrorComponentOrientation_None;
            object ret = null;
            try { ret = asm.MirrorComponents3(pf, compArr, (object)orient, false, null, false, null, 0, "", "", (int)swMirrorPartOptions_e.swMirrorPartOptions_ImportSolids, false, false, false); }
            catch (Exception ex) { res.Error = "MirrorComponents3 threw: " + ex.Message; await emit("Mirror", null, "fail", res.Error); return res; }
            model.ForceRebuild3(false);

            // Verify INDEPENDENTLY — count instances that are genuinely new (name absent pre-mirror). A no-exception
            // return from MirrorComponents3 is NOT proof of a change on this build; only a real new instance is.
            int added = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                if (!string.IsNullOrEmpty(nm) && !before.Contains(nm)) added++;
            }
            if (added == 0)
            {
                res.Error = "MirrorComponents3 reported no error but added zero new instances — refusing to claim success.";
                await emit("Mirror", null, "fail", res.Error);
                return res;
            }

            Feature mf = ret as Feature; if (mf == null) mf = FindNewestMirrorFeature(model);
            if (mf != null) { try { mf.Name = "ForgeMirrorAll"; } catch { } }
            res.Created = true;
            await emit("Mirror", null, "done", "mirrored " + added + " new instance(s) on the far side of " + planeName);

            await emit("Sentinel", "checking the assembly solves", "run", null);
            int wrong = 0; try { wrong = model.Extension.GetWhatsWrongCount(); } catch { }
            await emit("Sentinel", null, "done", wrong == 0 ? "rebuild clean" : wrong + " rebuild flags");

            res.Info = "Mirrored " + added + " component(s) across " + planeName + ".";
            return res;
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

        static string Sanitize(string s) => (s ?? "").Replace("@", "-at-").Replace("/", "_").Replace("\\", "_");

        static bool LooksFastener(string n)
        {
            if (string.IsNullOrEmpty(n)) return false; n = n.ToLowerInvariant();
            foreach (var h in new[] { "bolt", "screw", "nut", "washer", "hcs", "shcs", "stud", "vis", "boulon", "ecrou", "rondelle", "iso 40", "din 9" })
                if (n.Contains(h)) return true;
            return false;
        }

        static bool AnyForgeMirror(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string n = null; try { n = f.Name; } catch { }
                if (n != null && n.StartsWith("Forge-Mirror-", StringComparison.OrdinalIgnoreCase)) return true;
                f = f.GetNextFeature() as Feature;
            }
            return false;
        }

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
            // fallback: default planes appear first in the tree in order Front(0), Top(1), Right(2)
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
