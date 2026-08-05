using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddAngleMateResult
    {
        public string MateName;
        public string CompA, CompB;
        public double AngleDeg;
        public double AppliedDeg = -1;   // read back off the mate feature-data
        public int MatesBefore, MatesAfter;
        public bool AlreadyDone;         // idempotency: an angle mate already ties these two components together
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 57 — add_angle_mate (WRITE). Adds ONE angle mate between a planar face on each of two named components,
    /// holding them at a stated angle. "add a 30 degree angle mate between the bolt and the plate". Resolves both
    /// components (ONE question on 0/ambiguous — Rule #2), parses the angle (degrees), picks the LARGEST planar face on
    /// each, calls AssemblyDoc.AddMate5 with the angle (in radians), tags it Forge-Angle-&lt;a&gt;-&lt;b&gt;, and
    /// verifies by an INDEPENDENT re-count AND a read-back of the APPLIED angle off the mate feature-data (SW may store
    /// the supplement, so both a and 180−a are accepted). Idempotent; undoable; Forge never saves.
    /// </summary>
    public static class AddAngleMate
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(add|create|make|insert|place|put)\b") &&
                   Regex.IsMatch(c, @"\bangle\b") && Regex.IsMatch(c, @"\bmate\b") &&
                   !Regex.IsMatch(c, @"\b(suppress|unsuppress|delete|remove|drop|info|details|describe|list|edit|change)\b");
        }

        public static async Task<AddAngleMateResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddAngleMateResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to add a mate."; return res; }

            double? adeg = ParseAngleDeg(intent);
            if (adeg == null) { res.Error = "What angle? e.g. \"add a 30 degree angle mate between the bolt and the plate\"."; await emit("Gauge", null, "fail", "no value"); return res; }
            res.AngleDeg = adeg.Value;

            var comps = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                comps.Add(c);
            }

            string fa, fb;
            ParseTwoTargets(intent, out fa, out fb);

            Component2 compA = null, compB = null;
            if (fa != null && fb != null)
            {
                compA = ResolveOne(comps, fa, out string ea); if (compA == null) { res.Error = ea + " (\"" + fa + "\")."; await emit("Gauge", null, "fail", "resolve A"); return res; }
                compB = ResolveOne(comps, fb, out string eb); if (compB == null) { res.Error = eb + " (\"" + fb + "\")."; await emit("Gauge", null, "fail", "resolve B"); return res; }
            }
            else if (comps.Count == 2) { compA = comps[0]; compB = comps[1]; }
            else { res.Error = "Which two components? e.g. \"add a 30 degree angle mate between the bolt and the plate\"."; await emit("Gauge", null, "fail", "need two targets"); return res; }

            try { res.CompA = compA.Name2; res.CompB = compB.Name2; } catch { }
            await emit("Gauge", "angle " + res.AngleDeg + "°: '" + res.CompA + "' <-> '" + res.CompB + "'", "run", null);

            var mates = CollectMates(model);
            res.MatesBefore = mates.Count;

            if (MateOfTypeExists(mates, compA, compB, swMateType_e.swMateANGLE))
            {
                res.AlreadyDone = true; res.Verified = true; res.MatesAfter = res.MatesBefore;
                res.Info = "An angle mate already ties '" + res.CompA + "' and '" + res.CompB + "' — nothing to do.";
                await emit("Sentinel", null, "done", "already an angle mate — nothing to do");
                return res;
            }

            Face2 faceA = LargestPlanarFace(compA);
            Face2 faceB = LargestPlanarFace(compB);
            if (faceA == null) { res.Error = "'" + res.CompA + "' has no planar face to mate."; await emit("Gauge", null, "fail", "no planar A"); return res; }
            if (faceB == null) { res.Error = "'" + res.CompB + "' has no planar face to mate."; await emit("Gauge", null, "fail", "no planar B"); return res; }

            await emit("Torque", "adding " + res.AngleDeg + "° angle mate", "run", null);
            model.ClearSelection2(true);
            var sd = ((SelectionMgr)model.SelectionManager).CreateSelectData();
            sd.Mark = 1;
            bool s1 = false, s2 = false;
            try { s1 = ((Entity)faceA).Select4(false, sd); } catch { }
            try { s2 = ((Entity)faceB).Select4(true, sd); } catch { }
            if (!s1 || !s2) { model.ClearSelection2(true); res.Error = "Couldn't select the planar faces (selA=" + s1 + ", selB=" + s2 + ") — unchanged."; await emit("Torque", null, "fail", "selection"); return res; }

            double aRad = res.AngleDeg * Math.PI / 180.0;
            int err = -999; object mate = null;
            try
            {
                mate = asm.AddMate5((int)swMateType_e.swMateANGLE, (int)swMateAlign_e.swMateAlignCLOSEST,
                    false, 0, 0, 0, 0, 0, aRad, 0, 0, false, false, 0, out err);
            }
            catch (Exception ex) { model.ClearSelection2(true); res.Error = "AddMate5 threw (" + ex.GetType().Name + ") — unchanged."; await emit("Torque", null, "fail", res.Error); return res; }
            model.ClearSelection2(true);

            if (mate != null && err == (int)swAddMateError_e.swAddMateError_OverDefinedAssembly)
            {
                try { ((Feature)mate).Select2(false, 0); model.EditDelete(); model.ClearSelection2(true); } catch { }
                try { model.ForceRebuild3(false); } catch { }
                res.MatesAfter = CollectMates(model).Count;
                res.Error = "An angle mate between '" + res.CompA + "' and '" + res.CompB + "' would over-define the assembly — not added.";
                await emit("Sentinel", null, "fail", "would over-define");
                return res;
            }

            string tag = "Forge-Angle-" + Sanitize(res.CompA) + "-" + Sanitize(res.CompB);
            if (mate != null) { try { ((Feature)mate).Name = tag; res.MateName = tag; } catch { } }
            try { model.ForceRebuild3(false); } catch { }

            await emit("Sentinel", "verifying", "run", null);
            res.MatesAfter = CollectMates(model).Count;
            if (mate != null) { try { var ad = ((Feature)mate).GetDefinition() as IAngleMateFeatureData; if (ad != null) res.AppliedDeg = Math.Round(ad.Angle * 180.0 / Math.PI, 4); } catch { } }

            bool countOk = res.MatesAfter == res.MatesBefore + 1;
            bool valueOk = res.AppliedDeg < 0 ||
                           Math.Abs(res.AppliedDeg - res.AngleDeg) <= 0.05 ||
                           Math.Abs(res.AppliedDeg - (180.0 - res.AngleDeg)) <= 0.05;   // SW may store the supplement
            res.Verified = countOk && valueOk;
            if (!res.Verified)
            {
                res.Error = !countOk
                    ? "Mate count didn't rise by 1 (" + res.MatesBefore + " -> " + res.MatesAfter + ", AddMate5 code=" + err + ")."
                    : "Applied angle " + res.AppliedDeg + "° != requested " + res.AngleDeg + "° (nor its supplement).";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            string vtxt = res.AppliedDeg >= 0 ? (" at " + res.AppliedDeg + "° (read back)") : "";
            await emit("Sentinel", null, "done", "angle mate added" + vtxt + " (" + res.MatesBefore + " -> " + res.MatesAfter + " mates)");
            res.Info = "Added a " + res.AngleDeg + "° angle mate between '" + res.CompA + "' and '" + res.CompB + "'" + vtxt + ". One Ctrl+Z removes it; Forge didn't save.";
            return res;
        }

        private static double? ParseAngleDeg(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var m = Regex.Match(intent, @"(\d+(?:\.\d+)?)\s*(deg\w*|°|\bdegrees?\b)", RegexOptions.IgnoreCase);
            if (m.Success) return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var m2 = Regex.Match(intent, @"(\d+(?:\.\d+)?)");   // a bare number, taken as degrees
            if (m2.Success) return double.Parse(m2.Groups[1].Value, CultureInfo.InvariantCulture);
            return null;
        }

        private static void ParseTwoTargets(string intent, out string a, out string b)
        {
            a = null; b = null;
            if (string.IsNullOrEmpty(intent)) return;
            var m = Regex.Match(intent, @"between\s+(?:the\s+)?(.+?)\s+and\s+(?:the\s+)?(.+?)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(intent, @"(?:mate|tie|align|join)\s+(?:the\s+)?(.+?)\s+(?:and|to|with)\s+(?:the\s+)?(.+?)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) return;
            a = Clean(m.Groups[1].Value); b = Clean(m.Groups[2].Value);
            if (a.Length == 0 || b.Length == 0) { a = null; b = null; }
        }

        private static string Clean(string s)
        {
            s = (s ?? "").Trim();
            s = Regex.Replace(s, @"\b(\d+(?:\.\d+)?)\s*(deg\w*|°|degrees?)?\b", "", RegexOptions.IgnoreCase).Trim();
            s = Regex.Replace(s, @"\b(angle|angled|mates?|together|please)\b", "", RegexOptions.IgnoreCase).Trim();
            s = Regex.Replace(s, @"^(the|a|an|of)\s+", "", RegexOptions.IgnoreCase).Trim();
            return s;
        }

        private static Component2 ResolveOne(List<Component2> comps, string frag, out string err)
        {
            err = null;
            var hits = new List<Component2>();
            foreach (var c in comps) { string n = null; try { n = c.Name2; } catch { } if (n != null && n.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(c); }
            if (hits.Count == 1) return hits[0];
            if (hits.Count == 0) { err = "No component matches"; return null; }
            var ns = new List<string>(); foreach (var c in hits) { try { ns.Add(c.Name2); } catch { } if (ns.Count >= 5) break; }
            err = hits.Count + " components match (" + string.Join(", ", ns.ToArray()) + "…) — which one?";
            return null;
        }

        private static bool MateOfTypeExists(List<Feature> mates, Component2 a, Component2 b, swMateType_e type)
        {
            string na = null, nb = null; try { na = a.Name2; } catch { } try { nb = b.Name2; } catch { }
            foreach (var f in mates)
            {
                Mate2 mate = null; try { mate = f.GetSpecificFeature2() as Mate2; } catch { }
                if (mate == null) continue;
                if ((swMateType_e)mate.Type != type) continue;
                bool hitA = false, hitB = false;
                int n = 0; try { n = mate.GetMateEntityCount(); } catch { }
                for (int i = 0; i < n; i++)
                {
                    Component2 rc = null; try { rc = (mate.MateEntity(i) as MateEntity2)?.ReferenceComponent as Component2; } catch { }
                    string rn = null; try { rn = rc != null ? rc.Name2 : null; } catch { }
                    if (rn == null) continue;
                    if (na != null && rn == na) hitA = true;
                    if (nb != null && rn == nb) hitB = true;
                }
                if (hitA && hitB) return true;
            }
            return false;
        }

        private static string Sanitize(string s) { return Regex.Replace(s ?? "x", @"[^A-Za-z0-9]", ""); }

        private static Face2 LargestPlanarFace(Component2 comp)
        {
            Face2 best = null; double bestA = -1;
            try
            {
                object bi;
                var bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                if (bodies == null) return null;
                foreach (var bo in bodies)
                {
                    var body = bo as Body2; if (body == null) continue;
                    var faces = body.GetFaces() as object[]; if (faces == null) continue;
                    foreach (var fo in faces)
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var surf = face.GetSurface() as Surface; if (surf == null) continue;
                        bool plane = false; try { plane = surf.IsPlane(); } catch { }
                        if (!plane) continue;
                        double area = 0; try { area = face.GetArea(); } catch { }
                        if (area > bestA) { bestA = area; best = face; }
                    }
                }
            }
            catch { }
            return best;
        }

        private static List<Feature> CollectMates(IModelDoc2 model)
        {
            var list = new List<Feature>();
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "MateGroup")
                    {
                        var s = f.GetFirstSubFeature() as Feature;
                        while (s != null) { list.Add(s); s = s.GetNextSubFeature() as Feature; }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return list;
        }
    }
}
