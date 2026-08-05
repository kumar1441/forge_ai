using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CheckPartSymmetryResult
    {
        public int SymmetryPlanes;      // principal centroidal planes the body mirrors onto itself about
        public bool Symmetric;          // >= 1 plane
        public List<string> Planes = new List<string>();
        public string Verdict;
        public int IntersectOk;         // instrumentation: how many of the 3 boolean tests actually ran
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 176 — check_part_symmetry (READ). Mirrors the part's solid body across each principal plane through its
    /// centroid and checksum-compares against the original by SOLID INTERSECTION VOLUME (reflected ∩ original ≈ full
    /// volume ⇒ symmetric about that plane). Answers "can this part be reused by mirroring, or is it handed (needs an
    /// opposite-hand file)?" and names the mirror plane(s). Read-only: it reflects a TEMP copy (IBody2.Copy →
    /// ApplyTransform) and never edits the model. Uses the same live temp-body-boolean path proven by compare_bodies.
    /// </summary>
    public static class CheckPartSymmetry
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(symmetr\w*|mirror\w*|handed|handedness|chiral|opposite.?hand|left.?hand|right.?hand)\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(part|body|geometry|this|it|check|is)\b") &&
                   !System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(component|components|assembly|bolts?|fastener|mate|feature)\b");   // mirror_components / mirror_feature
        }

        public static async Task<CheckPartSymmetryResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CheckPartSymmetryResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to check its symmetry."; return res; }

            Body2 body = null; double best = -1;
            foreach (var o in (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]) ?? new object[0])
            {
                var b = o as Body2; if (b == null) continue;
                double v, a; MassOf(b, out v, out a);
                if (v > best) { best = v; body = b; }
            }
            if (body == null) { res.Error = "This part has no solid body to check."; return res; }

            double vol, area; double[] c; MassAndCentroid(body, out vol, out area, out c);
            var mu = app.GetMathUtility() as MathUtility;
            if (mu == null || vol <= 0) { res.Error = "Could not read the body's mass properties."; return res; }

            await emit("Sentinel", "mirroring the body across each principal plane", "run", null);

            string[] planeName = { "YZ plane (normal X)", "XZ plane (normal Y)", "XY plane (normal Z)" };
            var ratios = new double[3];
            for (int k = 0; k < 3; k++)
            {
                double[] arr = ReflectArray(k, c[k]);
                var xf = mu.CreateTransform(arr) as MathTransform;
                double iv; int err; bool ran = ReflectAndIntersect(body, xf, out iv, out err);
                if (ran) res.IntersectOk++;
                double ratio = (ran && vol > 0) ? iv / vol : -1;
                ratios[k] = ratio;
                if (ratio > 0.99) { res.SymmetryPlanes++; res.Planes.Add(planeName[k]); }
            }

            res.Symmetric = res.SymmetryPlanes >= 1;
            res.Verdict = res.SymmetryPlanes == 0
                ? "handed (chiral) — no principal mirror plane; needs an opposite-hand file"
                : "symmetric about " + res.SymmetryPlanes + " principal plane" + (res.SymmetryPlanes == 1 ? "" : "s");
            res.Diag = "vol=" + (vol * 1e9).ToString("F0", CultureInfo.InvariantCulture) + "mm3 planes=" + res.SymmetryPlanes +
                       " ratios=[" + F(ratios[0]) + "," + F(ratios[1]) + "," + F(ratios[2]) + "] intersectOk=" + res.IntersectOk;

            await emit("Sentinel", null, "done", res.Verdict);

            var sb = new StringBuilder(res.Verdict + ".");
            if (res.SymmetryPlanes > 0) { sb.Append("\nMirror plane(s):"); foreach (var p in res.Planes) sb.Append("\n• " + p); }
            res.Info = sb.ToString();
            return res;
        }

        // reflection about plane through c along axis k: v' = R v + T, R = identity with the k diagonal = -1, T[k]=2c.
        private static double[] ReflectArray(int k, double ck)
        {
            var a = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
            a[k * 4] = -1.0;          // (k,k) diagonal
            a[9 + k] = 2.0 * ck;      // translation along axis k
            return a;
        }

        private static bool ReflectAndIntersect(Body2 body, MathTransform xf, out double vol, out int err)
        {
            vol = 0; err = 0;
            try
            {
                var refl = body.Copy() as Body2; if (refl == null) { err = -99; return false; }
                bool ok = refl.ApplyTransform(xf); if (!ok) { err = -97; return false; }
                var orig = body.Copy() as Body2; if (orig == null) { err = -96; return false; }
                object resObj = refl.Operations2((int)swBodyOperationType_e.SWBODYINTERSECT, orig, out err);
                if (err == (int)swBodyOperationError_e.swBodyOperationNoIntersect) { vol = 0; return true; }
                if (err != (int)swBodyOperationError_e.swBodyOperationNoError) return false;
                foreach (var o in (resObj as object[]) ?? new object[0])
                {
                    var rb = o as Body2; if (rb == null) continue;
                    double v, a; MassOf(rb, out v, out a); vol += v;
                }
                return true;
            }
            catch { err = -98; return false; }
        }

        private static void MassOf(Body2 body, out double vol, out double area)
        {
            vol = 0; area = 0;
            try { var mp = body.GetMassProperties(0) as double[]; if (mp != null && mp.Length >= 5) { vol = mp[3]; area = mp[4]; } }
            catch { }
        }

        private static void MassAndCentroid(Body2 body, out double vol, out double area, out double[] c)
        {
            vol = 0; area = 0; c = new double[] { 0, 0, 0 };
            try
            {
                var mp = body.GetMassProperties(0) as double[];
                if (mp != null && mp.Length >= 5) { c[0] = mp[0]; c[1] = mp[1]; c[2] = mp[2]; vol = mp[3]; area = mp[4]; }
            }
            catch { }
        }

        private static string F(double v) { return v < 0 ? "err" : v.ToString("F3", CultureInfo.InvariantCulture); }
    }
}
