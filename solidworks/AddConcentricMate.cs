using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddConcentricMateResult
    {
        public string MateName;
        public string CompA, CompB;
        public int MatesBefore, MatesAfter;
        public bool AlreadyDone;   // idempotency: a concentric mate already ties these two components together
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 55 — add_concentric_mate (WRITE). Adds ONE concentric mate between a cylindrical face on each of two
    /// named components. "add a concentric mate between the bolt and the plate". Resolves both components from the
    /// live tree (ONE question on 0/ambiguous — Rule #2), picks the SMALLEST cylindrical face on each (a bolt shank,
    /// a plate hole — the axis you actually want to align), calls AssemblyDoc.AddMate5 (the most-proven write API on
    /// this build), tags the mate Forge-Conc-&lt;a&gt;-&lt;b&gt;, and verifies by an INDEPENDENT re-count: the total
    /// mate count rose by exactly 1. Idempotent — if a concentric mate already references BOTH components it does
    /// nothing. Undoable (one Ctrl+Z); Forge never saves.
    /// LANDMINE: swAddMateError_NoError == 1 (not 0), and code 5 (over-defined) STILL creates the mate — so success
    /// is judged by the independent recount, never by the return code.
    /// </summary>
    public static class AddConcentricMate
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // NARROW + specific-first: an ADD verb + the word "concentric". Excludes the other mate verbs
            // (suppress/delete/info/list/edit) so it never shadows suppress_mate/delete_mate/get_mate_info, and the
            // "concentric" requirement keeps the fastener auto-mate pipeline ("mate all the bolts" -> AutoMate) clear.
            return Regex.IsMatch(c, @"\b(add|create|make|insert|place|put)\b") &&
                   Regex.IsMatch(c, @"\bconcentric\b") &&
                   !Regex.IsMatch(c, @"\b(suppress|unsuppress|delete|remove|drop|info|details|describe|list|edit|change)\b");
        }

        public static async Task<AddConcentricMateResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddConcentricMateResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to add a mate."; return res; }

            // ---- resolve the two target components ----
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
            else if (comps.Count == 2) { compA = comps[0]; compB = comps[1]; }   // unambiguous: exactly two parts
            else { res.Error = "Which two components? e.g. \"add a concentric mate between the bolt and the plate\"."; await emit("Gauge", null, "fail", "need two targets"); return res; }

            try { res.CompA = compA.Name2; res.CompB = compB.Name2; } catch { }
            await emit("Gauge", "concentric: '" + res.CompA + "' <-> '" + res.CompB + "'", "run", null);

            var mates = CollectMates(model);
            res.MatesBefore = mates.Count;

            // ---- idempotency: a concentric mate already ties these two together? (independent of naming) ----
            if (ConcentricMateExists(mates, compA, compB))
            {
                res.AlreadyDone = true; res.Verified = true; res.MatesAfter = res.MatesBefore;
                res.Info = "A concentric mate already ties '" + res.CompA + "' and '" + res.CompB + "' — nothing to do.";
                await emit("Sentinel", null, "done", "already concentric — nothing to do");
                return res;
            }

            Face2 faceA = SmallestCylFace(compA);
            Face2 faceB = SmallestCylFace(compB);
            if (faceA == null) { res.Error = "'" + res.CompA + "' has no cylindrical face to mate concentrically."; await emit("Gauge", null, "fail", "no cyl A"); return res; }
            if (faceB == null) { res.Error = "'" + res.CompB + "' has no cylindrical face to mate concentrically."; await emit("Gauge", null, "fail", "no cyl B"); return res; }

            await emit("Torque", "adding concentric mate", "run", null);
            model.ClearSelection2(true);
            var sd = ((SelectionMgr)model.SelectionManager).CreateSelectData();
            sd.Mark = 1;
            bool s1 = false, s2 = false;
            try { s1 = ((Entity)faceA).Select4(false, sd); } catch { }
            try { s2 = ((Entity)faceB).Select4(true, sd); } catch { }
            if (!s1 || !s2) { model.ClearSelection2(true); res.Error = "Couldn't select the cylindrical faces (selA=" + s1 + ", selB=" + s2 + ") — unchanged."; await emit("Torque", null, "fail", "selection"); return res; }

            int err = -999; object mate = null;
            try
            {
                mate = asm.AddMate5((int)swMateType_e.swMateCONCENTRIC, (int)swMateAlign_e.swMateAlignCLOSEST,
                    false, 0, 0, 0, 0, 0, 0, 0, 0, false, false, 0, out err);
            }
            catch (Exception ex) { model.ClearSelection2(true); res.Error = "AddMate5 threw (" + ex.GetType().Name + ") — unchanged."; await emit("Torque", null, "fail", res.Error); return res; }
            model.ClearSelection2(true);

            // code 5 (over-defined) still CREATES the mate; if it over-defines, remove it and report honestly.
            if (mate != null && err == (int)swAddMateError_e.swAddMateError_OverDefinedAssembly)
            {
                try { ((Feature)mate).Select2(false, 0); model.EditDelete(); model.ClearSelection2(true); } catch { }
                try { model.ForceRebuild3(false); } catch { }
                res.MatesAfter = CollectMates(model).Count;
                res.Error = "A concentric mate between '" + res.CompA + "' and '" + res.CompB + "' would over-define the assembly — not added.";
                await emit("Sentinel", null, "fail", "would over-define");
                return res;
            }

            string tag = "Forge-Conc-" + Sanitize(res.CompA) + "-" + Sanitize(res.CompB);
            if (mate != null) { try { ((Feature)mate).Name = tag; res.MateName = tag; } catch { } }
            try { model.ForceRebuild3(false); } catch { }

            // ---- Sentinel: independent re-count (fail closed — never trust err) ----
            await emit("Sentinel", "verifying", "run", null);
            res.MatesAfter = CollectMates(model).Count;
            res.Verified = res.MatesAfter == res.MatesBefore + 1;
            if (!res.Verified)
            {
                res.Error = "Mate count didn't rise by 1 (" + res.MatesBefore + " -> " + res.MatesAfter + ", AddMate5 code=" + err + ") — the mate wasn't created.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "concentric mate added (" + res.MatesBefore + " -> " + res.MatesAfter + " mates)");
            res.Info = "Added a concentric mate between '" + res.CompA + "' and '" + res.CompB + "' (" + res.MatesBefore + " -> " + res.MatesAfter + " mates). One Ctrl+Z removes it; Forge didn't save.";
            return res;
        }

        // "between A and B" / "A and B" / "A to B" -> two component-name fragments (null,null if none found).
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

        // strip trailing filler ("concentric", "mate", "together", articles) from a parsed fragment
        private static string Clean(string s)
        {
            s = (s ?? "").Trim();
            s = Regex.Replace(s, @"\b(concentrically|concentric|mates?|together|please)\b", "", RegexOptions.IgnoreCase).Trim();
            s = Regex.Replace(s, @"^(the|a|an)\s+", "", RegexOptions.IgnoreCase).Trim();
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

        private static bool ConcentricMateExists(List<Feature> mates, Component2 a, Component2 b)
        {
            string na = null, nb = null; try { na = a.Name2; } catch { } try { nb = b.Name2; } catch { }
            foreach (var f in mates)
            {
                Mate2 mate = null; try { mate = f.GetSpecificFeature2() as Mate2; } catch { }
                if (mate == null) continue;
                if ((swMateType_e)mate.Type != swMateType_e.swMateCONCENTRIC) continue;
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

        // --- geometry helpers (independent of the test fixture generator's private copies) ---
        private static Face2 SmallestCylFace(Component2 comp)
        {
            Face2 best = null; double bestR = double.MaxValue;
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
                        bool cyl = false; try { cyl = surf.IsCylinder(); } catch { }
                        if (!cyl) continue;
                        double r = double.MaxValue;
                        try { var p = surf.CylinderParams as double[]; if (p != null && p.Length >= 7) r = p[6]; } catch { }
                        if (r < bestR) { bestR = r; best = face; }
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
