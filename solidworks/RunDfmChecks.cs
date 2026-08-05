using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DfmHoleFinding
    {
        public double DiaMm;
        public double DepthMm;
        public bool NonStandardSize;
        public bool DeepNarrow;
    }

    public class RunDfmChecksResult
    {
        public int HolesChecked;
        public int NonStandardSizeCount;
        public int DeepNarrowCount;
        public List<DfmHoleFinding> Findings = new List<DfmHoleFinding>();
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 180 — run_dfm_checks (READ, PART). Machinability screen on cylindrical hole features: flags hole
    /// diameters that don't match a standard metric drill-size table, and holes whose depth:diameter ratio exceeds
    /// a practical drilling aspect ratio (deep/narrow — tool deflection, chip evacuation, tool-access risk on a
    /// twist drill).
    ///
    /// Hole geometry reuses CountThroughHoles.cs's proven concave-cylindrical-face classification (Surface.IsCylinder
    /// + inward-normal concavity test against the face's own bbox centre) rather than re-deriving it from scratch —
    /// same primitive, same false-positive guards (sub-1.5mm thread/tooth noise excluded, and a bore wider than 60%
    /// of the part's own smallest overall span excluded as the part's main body cavity, not a drilled hole). Depth is
    /// the cylindrical face's own axial extent (project the face's bbox corners onto the bore axis, span = max-min) —
    /// exact for a single clean bore wall, whether the hole is through or blind.
    ///
    /// Independent cross-check (no new GroundTruth code): GroundTruth.MeasureGetFaces' cylindrical-face count
    /// (already merged into every PART ground-truth read as `faces.cylindrical`) independently re-derives the same
    /// face-type inventory via its OWN body/face traversal — agreement on hole count is a genuine cross-check, not a
    /// mirror of this handler's own math.
    ///
    /// KNOWN SCOPE LIMIT: this is a HOLE-focused subset of full DFM (the tool's broader brief also names internal
    /// sharp corners, deep/narrow POCKETS that aren't cylindrical bores, and general tool-access violations — none of
    /// those are implemented here; a general concave-edge/pocket geometry classifier is a much larger separate
    /// investigation, and this ships the honest hole-only subset rather than guessing at the rest). PART only — an
    /// assembly needs per-component resolution this v1 doesn't attempt, refused honestly.
    /// </summary>
    public static class RunDfmChecks
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\bdfm\b")) return true;
            if (Regex.IsMatch(c, @"\bmachinab(le|ility)\b")) return true;
            if (Regex.IsMatch(c, @"\bmanufactur(e|ing|ability)\b") && Regex.IsMatch(c, @"\b(check|checks|issues|problems|feasib\w*)\b")) return true;
            if (Regex.IsMatch(c, @"\b(non[- ]?standard|odd|unusual|weird|off[- ]?size)\b.{0,25}\bhole\b.{0,15}\bsizes?\b")) return true;
            if (Regex.IsMatch(c, @"\bdeep\b.{0,15}\bnarrow\b.{0,15}\b(hole|pocket)s?\b")) return true;
            if (Regex.IsMatch(c, @"\bhole\b.{0,15}\btoo\s+deep\b")) return true;
            return false;
        }

        private const double MinDiaM = 0.0015;          // below this: thread-root/knurl noise, not a real hole
        private const double DeepNarrowRatio = 4.0;      // depth:diameter beyond this is a practical drilling risk
        private const double SizeTolMm = 0.05;           // within this of a table entry counts as "standard"

        private static readonly double[] StandardMm = BuildStandardTable();
        private static double[] BuildStandardTable()
        {
            var list = new List<double>();
            for (double d = 1.0; d <= 20.0; d += 0.5) list.Add(d);
            for (double d = 21.0; d <= 50.0; d += 1.0) list.Add(d);
            return list.ToArray();
        }

        private class HoleCyl { public Face2 Face; public double R; public double[] AxisO; public double[] AxisD; }

        public static async Task<RunDfmChecksResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RunDfmChecksResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "DFM checks work on a single part — open the .SLDPRT you want screened, not an assembly."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to check."; return res; }

            await emit("Auditor", "screening hole diameters and depths for machinability", "run", null);

            object[] bodies = null;
            try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to check — this part has no solid geometry."; return res; }

            var holes = new List<HoleCyl>();
            double[] unionBox = null;
            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                try { unionBox = Union(unionBox, body.GetBodyBox() as double[]); } catch { }
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface surf = null; try { surf = face.GetSurface() as Surface; } catch { }
                    bool isCyl = false; try { isCyl = surf != null && surf.IsCylinder(); } catch { }
                    if (!isCyl) continue;
                    double[] cp = null; try { cp = surf.CylinderParams as double[]; } catch { }
                    if (cp == null || cp.Length < 7) continue;
                    if (!IsConcave(face, surf, cp)) continue;

                    double dl = Math.Sqrt(cp[3] * cp[3] + cp[4] * cp[4] + cp[5] * cp[5]);
                    if (dl < 1e-9) continue;
                    holes.Add(new HoleCyl { Face = face, R = cp[6], AxisO = new[] { cp[0], cp[1], cp[2] }, AxisD = new[] { cp[3] / dl, cp[4] / dl, cp[5] / dl } });
                }
            }

            if (unionBox != null && unionBox.Length >= 6)
            {
                double spanX = unionBox[3] - unionBox[0], spanY = unionBox[4] - unionBox[1], spanZ = unionBox[5] - unionBox[2];
                double minSpan = Math.Min(spanX, Math.Min(spanY, spanZ));
                double maxDiaM = 0.6 * minSpan;
                holes.RemoveAll(h => (h.R * 2.0) < MinDiaM || (h.R * 2.0) > maxDiaM);
            }
            if (holes.Count == 0)
            { res.Error = "No cylindrical hole faces found on this part to screen."; return res; }

            var sb = new StringBuilder();
            foreach (var h in holes)
            {
                double diaMm = h.R * 2000.0;
                double depthMm = HoleDepthMm(h.Face, h.AxisO, h.AxisD);
                if (depthMm <= 0) continue;   // unreadable axial extent — excluded rather than guessed

                bool nonStandard = !IsStandardSize(diaMm);
                bool deepNarrow = (depthMm / diaMm) > DeepNarrowRatio;
                res.HolesChecked++;
                if (nonStandard) res.NonStandardSizeCount++;
                if (deepNarrow) res.DeepNarrowCount++;
                res.Findings.Add(new DfmHoleFinding { DiaMm = diaMm, DepthMm = depthMm, NonStandardSize = nonStandard, DeepNarrow = deepNarrow });
            }

            if (res.HolesChecked == 0)
            { res.Error = "Found " + holes.Count + " cylindrical hole face(s) but couldn't read a clean axial depth on any of them."; return res; }

            res.Verified = true;
            int shown = 0;
            foreach (var f in res.Findings)
            {
                if (!f.NonStandardSize && !f.DeepNarrow) continue;
                if (shown++ >= 24) { sb.Append("\n… (more findings omitted)"); break; }
                sb.Append("\n• Ø" + f.DiaMm.ToString("0.##") + "mm x " + f.DepthMm.ToString("0.#") + "mm deep" +
                          (f.NonStandardSize ? " — NON-STANDARD size" : "") +
                          (f.DeepNarrow ? " — DEEP/NARROW (" + (f.DepthMm / f.DiaMm).ToString("0.#") + ":1)" : ""));
            }
            res.Info = res.HolesChecked + " hole(s) screened: " + res.NonStandardSizeCount + " non-standard size, " +
                       res.DeepNarrowCount + " deep/narrow" +
                       ((res.NonStandardSizeCount == 0 && res.DeepNarrowCount == 0) ? " — clean, no machinability flags." : "." + sb);
            await emit("Auditor", null, "done", res.NonStandardSizeCount + " non-standard, " + res.DeepNarrowCount + " deep/narrow of " + res.HolesChecked);
            return res;
        }

        private static bool IsStandardSize(double diaMm)
        {
            foreach (var s in StandardMm) if (Math.Abs(diaMm - s) <= SizeTolMm) return true;
            return false;
        }

        // axial extent of a cylindrical bore face = the hole's depth: project the face's own bbox corners onto the
        // bore axis and take the span (max-min). Exact for a clean bore wall regardless of through/blind.
        private static double HoleDepthMm(Face2 face, double[] axisO, double[] axisD)
        {
            try
            {
                double[] box = face.GetBox() as double[];
                if (box == null || box.Length < 6) return 0;
                double minT = double.MaxValue, maxT = double.MinValue;
                for (int cx = 0; cx < 2; cx++)
                for (int cy = 0; cy < 2; cy++)
                for (int cz = 0; cz < 2; cz++)
                {
                    double px = cx == 0 ? box[0] : box[3];
                    double py = cy == 0 ? box[1] : box[4];
                    double pz = cz == 0 ? box[2] : box[5];
                    double t = (px - axisO[0]) * axisD[0] + (py - axisO[1]) * axisD[1] + (pz - axisO[2]) * axisD[2];
                    if (t < minT) minT = t;
                    if (t > maxT) maxT = t;
                }
                return (maxT - minT) * 1000.0;
            }
            catch { return 0; }
        }

        private static double[] Union(double[] acc, double[] b)
        {
            if (b == null || b.Length < 6) return acc;
            if (acc == null) return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
            return new[]
            {
                Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
            };
        }

        // same inward-normal concavity test CountThroughHoles.cs proved live: a real hole's outward face normal
        // points BACK toward its own axis (empty bore is the cylinder's interior); a boss/rib/tooth's normal points
        // AWAY from its axis (solid material fills the cylinder).
        private static bool IsConcave(Face2 face, Surface surf, double[] cylParams)
        {
            try
            {
                double[] box = null; try { box = face.GetBox() as double[]; } catch { }
                if (box == null || box.Length < 6) return false;
                double[] center = { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };
                double[] p = null; try { p = face.GetClosestPointOn(center[0], center[1], center[2]) as double[]; } catch { }
                if (p == null || p.Length < 3) return false;

                double[] axisO = { cylParams[0], cylParams[1], cylParams[2] };
                double[] axisD = { cylParams[3], cylParams[4], cylParams[5] };
                double dl = Math.Sqrt(axisD[0] * axisD[0] + axisD[1] * axisD[1] + axisD[2] * axisD[2]);
                if (dl < 1e-9) return false;
                axisD = new[] { axisD[0] / dl, axisD[1] / dl, axisD[2] / dl };

                double[] v = { p[0] - axisO[0], p[1] - axisO[1], p[2] - axisO[2] };
                double along = v[0] * axisD[0] + v[1] * axisD[1] + v[2] * axisD[2];
                double[] radial = { v[0] - along * axisD[0], v[1] - along * axisD[1], v[2] - along * axisD[2] };
                double rl = Math.Sqrt(radial[0] * radial[0] + radial[1] * radial[1] + radial[2] * radial[2]);
                if (rl < 1e-9) return false;
                radial = new[] { radial[0] / rl, radial[1] / rl, radial[2] / rl };

                double[] n = null; try { n = surf.EvaluateAtPoint(p[0], p[1], p[2]) as double[]; } catch { }
                if (n == null || n.Length < 3) return false;
                double nl = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                if (nl < 1e-9) return false;
                double[] nu = { n[0] / nl, n[1] / nl, n[2] / nl };
                bool reversed = false; try { reversed = face.FaceInSurfaceSense(); } catch { }
                if (reversed) nu = new[] { -nu[0], -nu[1], -nu[2] };

                double dot = nu[0] * radial[0] + nu[1] * radial[1] + nu[2] * radial[2];
                return dot < 0;
            }
            catch { return false; }
        }
    }
}
