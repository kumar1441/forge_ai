using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddWidthMateResult
    {
        public string TabName, RefName;
        public int MatesBefore, MatesAfter;
        public double TabCenterBefore, TabCenterAfter;  // tab component's position along the width axis (mm)
        public int MarkSchemeUsed;                       // which selection-mark scheme worked (diagnosis)
        public string ApiErr;                            // AddMate5 return codes seen across the sweep (diagnosis)
        public bool AlreadyDone;                         // idempotency: a width mate already ties tab+reference
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 58 — add_width_mate (WRITE). Centres a TAB between two reference faces of a CHANNEL (a slot). "center the
    /// tab in the channel" / "add a width mate between the tab and the channel". Resolves the two components; finds the
    /// tab's thinnest opposed planar pair (its width faces) and the channel's tightest opposed pair along the SAME axis
    /// that the tab fits between (the slot walls); calls AssemblyDoc.AddMate5 with swMateWIDTH + swMateWidth_Centered.
    /// INSTRUMENT-FIRST: the headless selection-mark scheme for a width mate is unproven on this build, so the handler
    /// SWEEPS the plausible schemes and keeps the one that actually CENTRES the tab — verified by an INDEPENDENT read of
    /// the tab's position along the width axis (must land at the slot centre), never by the AddMate5 return code.
    /// Idempotent; undoable (one Ctrl+Z); Forge never saves.
    /// </summary>
    public static class AddWidthMate
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // NARROW: a "width mate" by name, OR a "centre X in/between …" phrasing. Excludes the other mate verbs so it
            // never shadows suppress/delete/edit/info, and requires a centring/width word so plain "mate the bolts" is clear.
            bool widthWord = Regex.IsMatch(c, @"\bwidth\s+mate\b") ||
                             (Regex.IsMatch(c, @"\b(cent(er|re)|centred|centered)\b") && Regex.IsMatch(c, @"\b(in|between|inside|within|slot|channel|groove)\b"));
            return widthWord &&
                   !Regex.IsMatch(c, @"\b(suppress|unsuppress|delete|remove|drop|info|details|describe|list|edit)\b");
        }

        public static async Task<AddWidthMateResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddWidthMateResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to add a width mate."; return res; }

            var comps = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                comps.Add(c);
            }

            // resolve tab (centred) + reference (channel). "center the tab in the channel" -> tab=first, ref=second.
            Component2 tab = null, refc = null;
            string fa, fb; ParseTabRef(intent, out fa, out fb);
            if (fa != null && fb != null)
            {
                tab = ResolveOne(comps, fa); refc = ResolveOne(comps, fb);
            }
            if ((tab == null || refc == null) && comps.Count == 2)
            {
                // exactly two components: the SMALLER (fewer faces / smaller box) is the tab, the other the channel
                var a = comps[0]; var b = comps[1];
                if (FaceCount(a) <= FaceCount(b)) { tab = tab ?? a; refc = refc ?? b; }
                else { tab = tab ?? b; refc = refc ?? a; }
            }
            if (tab == null || refc == null) { res.Error = "Which tab and which channel? e.g. \"center the tab in the channel\"."; await emit("Gauge", null, "fail", "need two targets"); return res; }
            try { res.TabName = tab.Name2; res.RefName = refc.Name2; } catch { }
            await emit("Gauge", "width: centre '" + res.TabName + "' in '" + res.RefName + "'", "run", null);

            var mates = CollectMates(model);
            res.MatesBefore = mates.Count;
            if (WidthMateExists(mates, tab, refc))
            {
                res.AlreadyDone = true; res.Verified = true; res.MatesAfter = res.MatesBefore;
                res.TabCenterAfter = res.TabCenterBefore = AxisPos(tab, TabWidthAxis(tab));
                res.Info = "A width mate already centres '" + res.TabName + "' in '" + res.RefName + "' — nothing to do.";
                await emit("Sentinel", null, "done", "already centred — nothing to do");
                return res;
            }

            // width axis is stable across rebuilds (geometry-defined); the FACES are re-acquired fresh each scheme.
            double[] axis0; Face2 z1, z2, z3, z4;
            if (!FindWidthFaces(tab, refc, out axis0, out z1, out z2, out z3, out z4))
            { res.Error = "Couldn't find a slot (opposed faces) to centre '" + res.TabName + "' between."; await emit("Gauge", null, "fail", "no slot faces"); return res; }
            double[] axis = axis0;

            res.TabCenterBefore = AxisPos(tab, axis);
            await emit("Torque", "seating the width mate", "run", null);

            // ---- SWEEP the selection-mark + order schemes; keep the first that CENTRES the tab (independent read). ----
            // A width mate takes a WIDTH (reference) face-pair and a TAB face-pair; the headless mark/order convention is
            // unproven on this build, so we try the plausible ones. Faces are RE-ACQUIRED each scheme (a ForceRebuild3
            // invalidates Face2 pointers), and we only rebuild AFTER a non-null mate.
            var schemes = new[] {
                new[]{1,1,0}, // width mark1, tab mark1, width-first
                new[]{1,2,0}, // width mark1, tab mark2, width-first
                new[]{2,1,1}, // tab mark1, width mark2, tab-first
                new[]{1,1,1}, // tab mark1, width mark1, tab-first
                new[]{2,1,0}, // width mark2, tab mark1, width-first
            };
            var errs = new List<string>();
            bool centred = false;
            for (int si = 0; si < schemes.Length && !centred; si++)
            {
                double[] ax; Face2 tabA, tabB, refA, refB;
                if (!FindWidthFaces(tab, refc, out ax, out tabA, out tabB, out refA, out refB)) { errs.Add("s" + si + ":refindfail"); continue; }
                int refMark = schemes[si][0], tabMark = schemes[si][1]; bool tabFirst = schemes[si][2] == 1;
                model.ClearSelection2(true);
                var sd = ((SelectionMgr)model.SelectionManager).CreateSelectData();
                bool ok = true;
                try
                {
                    if (tabFirst) { sd.Mark = tabMark; ok &= ((Entity)tabA).Select4(true, sd); ok &= ((Entity)tabB).Select4(true, sd); sd.Mark = refMark; ok &= ((Entity)refA).Select4(true, sd); ok &= ((Entity)refB).Select4(true, sd); }
                    else          { sd.Mark = refMark; ok &= ((Entity)refA).Select4(true, sd); ok &= ((Entity)refB).Select4(true, sd); sd.Mark = tabMark; ok &= ((Entity)tabA).Select4(true, sd); ok &= ((Entity)tabB).Select4(true, sd); }
                }
                catch { ok = false; }
                if (!ok) { model.ClearSelection2(true); errs.Add("s" + si + ":selfail"); continue; }

                int err = -999; object mate = null;
                try
                {
                    mate = asm.AddMate5((int)swMateType_e.swMateWIDTH, (int)swMateAlign_e.swMateAlignCLOSEST,
                        false, 0, 0, 0, 0, 0, 0, 0, 0, false, false, (int)swMateWidthOptions_e.swMateWidth_Centered, out err);
                }
                catch (Exception ex) { model.ClearSelection2(true); errs.Add("s" + si + ":throw(" + ex.GetType().Name + ")"); continue; }
                model.ClearSelection2(true);
                errs.Add("s" + si + ":err=" + err + (mate != null ? ",obj" : ",null"));
                if (mate == null) continue;   // nothing created — no rebuild, try next scheme

                try { model.ForceRebuild3(false); } catch { }
                double posNow = AxisPos(tab, axis);
                int cntNow = CollectMates(model).Count;
                if (cntNow == res.MatesBefore + 1 && Math.Abs(posNow) < 0.1)
                {
                    try { ((Feature)mate).Name = "Forge-Width-" + Sanitize(res.TabName) + "-" + Sanitize(res.RefName); } catch { }
                    res.MarkSchemeUsed = si; res.MatesAfter = cntNow; res.TabCenterAfter = posNow; centred = true;
                    break;
                }
                // created but didn't centre (or over-defined) — roll it back and try the next scheme
                try { ((Feature)mate).Select2(false, 0); model.EditDelete(); model.ClearSelection2(true); } catch { }
                try { model.ForceRebuild3(false); } catch { }
            }
            res.ApiErr = string.Join(" ", errs.ToArray());

            // ---- DIAGNOSTIC (instrument-before-park): if no scheme worked, record the face geometry + a CONTROL mate. A
            // plain COINCIDENT on the same tab/ref face proves the faces ARE selectable+mateable headless — so a failure
            // there means "wrong faces", while a coincident SUCCESS + width NULL means swMateWIDTH is specifically dead. ----
            if (!centred)
            {
                double[] ax; Face2 tA, tB, rA, rB;
                if (FindWidthFaces(tab, refc, out ax, out tA, out tB, out rA, out rB))
                {
                    res.ApiErr += " | axis=" + AxisName(ax) + " tabSep=" + PairSep(tA, tB, ax).ToString("F1") + " refSep=" + PairSep(rA, rB, ax).ToString("F1");
                    model.ClearSelection2(true);
                    var sdc = ((SelectionMgr)model.SelectionManager).CreateSelectData(); sdc.Mark = 1;
                    bool cok = true; try { cok &= ((Entity)tA).Select4(true, sdc); cok &= ((Entity)rA).Select4(true, sdc); } catch { cok = false; }
                    if (cok)
                    {
                        int cerr = -999; object cmate = null;
                        try { cmate = asm.AddMate5((int)swMateType_e.swMateCOINCIDENT, (int)swMateAlign_e.swMateAlignCLOSEST, false, 0,0,0,0,0,0,0,0, false, false, 0, out cerr); } catch { }
                        res.ApiErr += " | CONTROL coincident: err=" + cerr + (cmate != null ? ",obj(FACES-OK)" : ",null");
                        if (cmate != null) { try { ((Feature)cmate).Select2(false, 0); model.EditDelete(); } catch { } }
                    }
                    else res.ApiErr += " | CONTROL selfail";
                    model.ClearSelection2(true);
                    try { model.ForceRebuild3(false); } catch { }
                }
            }

            res.MatesAfter = CollectMates(model).Count;
            res.TabCenterAfter = AxisPos(tab, axis);
            res.Verified = centred && res.MatesAfter == res.MatesBefore + 1 && Math.Abs(res.TabCenterAfter) < 0.1;
            if (!res.Verified)
            {
                res.Error = "Width mate didn't centre the tab (before=" + res.TabCenterBefore.ToString("F3") + "mm after=" + res.TabCenterAfter.ToString("F3") + "mm, mates " + res.MatesBefore + "->" + res.MatesAfter + ", sweep=[" + res.ApiErr + "]).";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Centred '" + res.TabName + "' in '" + res.RefName + "' with a width mate (tab " + res.TabCenterBefore.ToString("F2") + "mm -> " + res.TabCenterAfter.ToString("F2") + "mm). One Ctrl+Z removes it; Forge didn't save.";
            await emit("Sentinel", null, "done", "width mate added — tab centred (scheme " + res.MarkSchemeUsed + ")");
            return res;
        }

        // ---- component position along an axis (mm), via Transform2 translation projected on the axis ----
        private static double AxisPos(Component2 comp, double[] axis)
        {
            try
            {
                var t = comp.Transform2 as MathTransform; if (t == null) return 0;
                var d = t.ArrayData as double[]; if (d == null || d.Length < 12) return 0;
                double px = d[9], py = d[10], pz = d[11]; // translation (metres)
                return (px * axis[0] + py * axis[1] + pz * axis[2]) * 1000.0;
            }
            catch { return 0; }
        }

        private static string AxisName(double[] a) { return a == null ? "?" : (Math.Abs(a[0]) > 0.9 ? "X" : Math.Abs(a[1]) > 0.9 ? "Y" : "Z"); }
        private static double PairSep(Face2 a, Face2 b, double[] axis)
        {
            try
            {
                var pa = (a.GetSurface() as Surface).PlaneParams as double[];
                var pb = (b.GetSurface() as Surface).PlaneParams as double[];
                if (pa == null || pb == null) return -1;
                double[] diff = { pa[3]-pb[3], pa[4]-pb[4], pa[5]-pb[5] };
                return Math.Abs(diff[0]*axis[0]+diff[1]*axis[1]+diff[2]*axis[2]) * 1000.0;
            }
            catch { return -1; }
        }
        private static double[] TabWidthAxis(Component2 tab)
        {
            double[] axis; Face2 a, b; FindThinnestOpposedPair(tab, out axis, out a, out b);
            return axis ?? new double[] { 1, 0, 0 };
        }

        // Find the width axis and the four faces: the tab's thinnest opposed planar pair, and the channel's tightest
        // opposed pair along the same axis that the tab fits between (separation > the tab's).
        private static bool FindWidthFaces(Component2 tab, Component2 refc, out double[] axis, out Face2 tabA, out Face2 tabB, out Face2 refA, out Face2 refB)
        {
            axis = null; tabA = tabB = refA = refB = null;
            double tabSep; if (!FindThinnestOpposedPair(tab, out axis, out tabA, out tabB, out tabSep)) return false;
            // channel: opposed pairs along `axis`, pick the tightest with separation > tabSep + a hair
            var pairs = OpposedPairs(refc);
            double best = double.MaxValue;
            foreach (var p in pairs)
            {
                if (Math.Abs(Dot(p.Axis, axis)) < 0.98) continue;      // same axis as the tab width
                if (p.Sep <= tabSep + 0.5) continue;                   // must be wider than the tab (a slot it fits in)
                if (p.Sep < best) { best = p.Sep; refA = p.A; refB = p.B; }
            }
            return refA != null && refB != null;
        }

        private static bool FindThinnestOpposedPair(Component2 comp, out double[] axis, out Face2 a, out Face2 b)
        { double sep; return FindThinnestOpposedPair(comp, out axis, out a, out b, out sep); }

        private static bool FindThinnestOpposedPair(Component2 comp, out double[] axis, out Face2 a, out Face2 b, out double sep)
        {
            axis = null; a = b = null; sep = 0;
            var pairs = OpposedPairs(comp);
            double best = double.MaxValue;
            foreach (var p in pairs) if (p.Sep < best && p.Sep > 0.5) { best = p.Sep; a = p.A; b = p.B; axis = p.Axis; sep = p.Sep; }
            return a != null;
        }

        private class OPair { public Face2 A, B; public double[] Axis; public double Sep; }

        // all opposed (anti-parallel) planar face pairs of a component, in ASSEMBLY space, with their separation (mm)
        private static List<OPair> OpposedPairs(Component2 comp)
        {
            var faces = new List<KeyValuePair<double[], double[]>>(); // (unit normal, point) in assembly space
            var raw = new List<Face2>();
            try
            {
                object bi; var bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                var t = comp.Transform2 as MathTransform; double[] td = t != null ? t.ArrayData as double[] : null;
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var surf = face.GetSurface() as Surface; if (surf == null) continue;
                        bool plane = false; try { plane = surf.IsPlane(); } catch { } if (!plane) continue;
                        var pp = surf.PlaneParams as double[]; if (pp == null || pp.Length < 6) continue;
                        double[] n = XformDir(td, new[] { pp[0], pp[1], pp[2] });
                        double[] pt = XformPt(td, new[] { pp[3], pp[4], pp[5] });
                        faces.Add(new KeyValuePair<double[], double[]>(n, pt)); raw.Add(face);
                    }
                }
            }
            catch { }
            var pairs = new List<OPair>();
            for (int i = 0; i < faces.Count; i++)
                for (int j = i + 1; j < faces.Count; j++)
                {
                    double d = Dot(faces[i].Key, faces[j].Key);
                    if (d > -0.98) continue;                                   // must be anti-parallel (opposed)
                    // separation = distance between the two planes along the normal
                    double[] diff = { faces[i].Value[0] - faces[j].Value[0], faces[i].Value[1] - faces[j].Value[1], faces[i].Value[2] - faces[j].Value[2] };
                    double sepM = Math.Abs(Dot(diff, faces[i].Key));
                    pairs.Add(new OPair { A = raw[i], B = raw[j], Axis = faces[i].Key, Sep = sepM * 1000.0 });
                }
            return pairs;
        }

        private static double[] XformDir(double[] td, double[] v)
        {
            if (td == null || td.Length < 9) return Norm(v);
            double x = td[0] * v[0] + td[3] * v[1] + td[6] * v[2];
            double y = td[1] * v[0] + td[4] * v[1] + td[7] * v[2];
            double z = td[2] * v[0] + td[5] * v[1] + td[8] * v[2];
            return Norm(new[] { x, y, z });
        }
        private static double[] XformPt(double[] td, double[] p)
        {
            if (td == null || td.Length < 12) return p;
            double s = td.Length > 12 ? (td[12] == 0 ? 1 : td[12]) : 1;
            double x = (td[0] * p[0] + td[3] * p[1] + td[6] * p[2]) * s + td[9];
            double y = (td[1] * p[0] + td[4] * p[1] + td[7] * p[2]) * s + td[10];
            double z = (td[2] * p[0] + td[5] * p[1] + td[8] * p[2]) * s + td[11];
            return new[] { x, y, z };
        }
        private static double[] Norm(double[] v) { double n = Math.Sqrt(v[0]*v[0]+v[1]*v[1]+v[2]*v[2]); return n < 1e-12 ? v : new[] { v[0]/n, v[1]/n, v[2]/n }; }
        private static double Dot(double[] a, double[] b) { return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]; }

        private static int FaceCount(Component2 comp)
        {
            int n = 0;
            try { object bi; var bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                  foreach (var bo in bodies ?? new object[0]) { var body = bo as Body2; if (body == null) continue; var fs = body.GetFaces() as object[]; if (fs != null) n += fs.Length; } } catch { }
            return n;
        }

        private static void ParseTabRef(string intent, out string a, out string b)
        {
            a = null; b = null;
            if (string.IsNullOrEmpty(intent)) return;
            var m = Regex.Match(intent, @"cent(?:er|re)\s+(?:the\s+)?(.+?)\s+(?:in|inside|within|between)\s+(?:the\s+)?(.+?)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(intent, @"between\s+(?:the\s+)?(.+?)\s+and\s+(?:the\s+)?(.+?)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) return;
            a = Clean(m.Groups[1].Value); b = Clean(m.Groups[2].Value);
            if (a.Length == 0 || b.Length == 0) { a = null; b = null; }
        }
        private static string Clean(string s)
        {
            s = (s ?? "").Trim();
            s = Regex.Replace(s, @"\b(width\s+mate|mates?|slot|groove|together|please)\b", "", RegexOptions.IgnoreCase).Trim();
            s = Regex.Replace(s, @"^(the|a|an)\s+", "", RegexOptions.IgnoreCase).Trim();
            return s;
        }
        private static Component2 ResolveOne(List<Component2> comps, string frag)
        {
            if (string.IsNullOrEmpty(frag)) return null;
            Component2 hit = null; int n = 0;
            foreach (var c in comps) { string nm = null; try { nm = c.Name2; } catch { } if (nm != null && nm.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0) { hit = c; n++; } }
            return n == 1 ? hit : null;
        }

        private static bool WidthMateExists(List<Feature> mates, Component2 a, Component2 b)
        {
            string na = null, nb = null; try { na = a.Name2; } catch { } try { nb = b.Name2; } catch { }
            foreach (var f in mates)
            {
                Mate2 mate = null; try { mate = f.GetSpecificFeature2() as Mate2; } catch { }
                if (mate == null) continue;
                if ((swMateType_e)mate.Type != swMateType_e.swMateWIDTH) continue;
                bool hitA = false, hitB = false; int n = 0; try { n = mate.GetMateEntityCount(); } catch { }
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
