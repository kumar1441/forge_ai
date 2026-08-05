using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GeometryDefeatureResult
    {
        public int SmallHolesFound;        // small internal (concave) cylindrical hole-faces detected before acting
        public int FilletsFound;           // small fillet faces detected (convex small cylinders + small tori)
        public int FacesRemoved;           // faces actually removed AND verified (rebuild clean + volume didn't drop)
        public double VolumeBeforeMm3 = -1; // solid volume before defeaturing (mm^3), independently measured
        public double VolumeAfterMm3 = -1;  // solid volume after defeaturing + rebuild (mm^3)
        public int RebuildErrors;          // GetWhatsWrongCount() post-rebuild (0 => clean)
        public string OutputFile;          // null when done in-place; else path of the SaveDeFeaturedFile copy
        public bool AlreadySimplified;     // idempotent: nothing left to remove
        public bool Verified;              // fail closed: true ONLY when geometry was independently confirmed simpler
        public string Info;                // verdict-first panel line
        public string Error;               // honest failure text (assembly handed in, no solid, nothing healed)
    }

    /// <summary>
    /// GeometryDefeature ("simplify a part for printing/FEA by removing detail") — a REAL geometry WRITE on a single
    /// PART. Where the feature-suppression Simplify only works on parts WITH a parametric feature tree (fillets / hole
    /// features to suppress), this operates on the B-rep GEOMETRY, so it also works on IMPORTED DUMB SOLIDS (STEP/IGES
    /// "Inkoop" parts) whose tree is just a BaseBody — extremely common in real assemblies, and a no-op for the old
    /// Simplify. Commands: "simplify this for printing", "remove the small holes", "defeature this imported part",
    /// "strip the detail for FEA".
    ///
    /// Approach (deliberate, documented):
    ///   Gauge — read the solid bodies; find every SMALL cylindrical hole-face (diameter below a bbox-derived threshold,
    ///           default ~8% of the smallest bbox span, overridable from the text e.g. "remove holes under 5mm") and
    ///           every small fillet face (small convex cylinder / small-minor-radius torus). Concavity of a cylinder is
    ///           decided from its outward normal vs the radial direction: normal toward the axis = a hole (material
    ///           outside); normal away = a convex round. Report the counts BEFORE acting (Rule #3 preview).
    ///   Mender — remove each detail face IN-PLACE via IFeatureManager.InsertDeleteFace with the Delete-and-Patch option
    ///           (option 1 of swDeleteFaceOptions_e — the surrounding surface extends to close the opening). PARTIAL
    ///           SUCCESS per face (Rule #4): each removal is its own try; a face that won't heal is rolled back and the
    ///           run continues. The topology changes each delete, so targets are RE-SCANNED fresh between removals and
    ///           failed ones are remembered by an axis+radius signature so they aren't retried forever.
    ///   FALLBACK — if the in-place API is unavailable on this build (throws) OR nothing healed, write a defeatured COPY
    ///           next to the original via IModelDocExtension.SaveDeFeaturedFile ("<part>_forge-simplified.SLDPRT"); the
    ///           original is never touched and the user still gets a genuinely simpler part.
    ///
    /// Robustness (the 12 rules): PART only — an assembly is refused with an honest message (Rule #2). IDEMPOTENT
    /// (Rule #5): a rerun finds no small holes left and reports "already simplified, nothing to remove." UNDO is sacred
    /// (Rule #7): every InsertDeleteFace is a tree feature undone by one Ctrl+Z, the fallback writes a NEW file, and
    /// Forge never saves the original — the user saves. FAIL CLOSED (Rule #6): every accepted removal is gated on the
    /// live geometry (rebuild clean AND the solid volume did NOT drop — a filled hole / sharpened fillet raises volume,
    /// while removing a boss/pin would drop it, so boss removals are auto-rejected), and after the run the result is
    /// INDEPENDENTLY re-measured (face count must fall by at least the number removed AND volume must rise). Verified
    /// reports what was MEASURED, never what was attempted — shipping a "simplified" part that is actually broken is the
    /// one unacceptable outcome.
    /// </summary>
    public static class GeometryDefeature
    {
        // IFeatureManager.EditDeleteFace(Refill): Refill=1 => delete the face(s) AND heal by extending the adjacent
        // faces over the opening; Refill=0 => plain delete, which leaves an open body.
        private const int DeleteAndPatch = 1;

        private const double DefaultFrac = 0.08;   // default small-detail threshold: diameter < 8% of the smallest bbox span
        private const double MinThrMm = 0.5;       // never treat sub-0.5mm as the whole threshold (numeric noise floor)

        public static bool IsDefeatureIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // Owns the GEOMETRY-simplification cues so it wins over feature-suppression Simplify for imported/dumb parts.
            // "defeature" is unambiguous; otherwise require an explicit remove-holes / strip-geometry / imported cue so a
            // plain "print prep / suppress the fillets" (parametric) still routes to Simplifier.
            if (Regex.IsMatch(c, @"\bde[- ]?feature(d|s|ing)?\b")) return true;
            if (Regex.IsMatch(c, @"\b(remove|delete|strip|get rid of|kill|fill)\b") &&
                Regex.IsMatch(c, @"\b(small |tiny |little )?holes?\b")) return true;
            if (Regex.IsMatch(c, @"\b(strip|remove|clean up)\b") && Regex.IsMatch(c, @"\b(detail|details|geometry|small stuff)\b")) return true;
            if (Regex.IsMatch(c, @"\b(simplif\w+|clean up|print[- ]?prep|fea)\b") &&
                Regex.IsMatch(c, @"\b(import(ed)?|dumb|step|iges|inkoop|purchased|bought|solid)\b")) return true;
            return false;
        }

        private class Target
        {
            public Face2 Face;
            public double DiaMm;   // hole diameter or 2×fillet-minor-radius (mm)
            public bool Hole;      // true = small concave cylinder (a hole); false = a fillet (convex cyl / small torus)
            public string Sig;     // stable axis+radius signature so a failed target is not retried across re-scans
        }

        public static async Task<GeometryDefeatureResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GeometryDefeatureResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Defeature works on a single part — open the .SLDPRT you want simplified, not an assembly (v1 is part-scoped)."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to defeature."; return res; }

            await emit("Gauge", "reading the solid bodies", "run", null);
            object[] bodies = SolidBodies(part);
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to defeature — this part has no solid geometry (a surface/sheet body or an empty doc has no faces to remove)."; return res; }

            double minSpanM = MinBboxSpanM(bodies);
            double thrDiaM = ThresholdDiaM(intent, minSpanM);
            var targets = ScanTargets(part, thrDiaM);
            int holes = targets.Count(t => t.Hole);
            int fillets = targets.Count - holes;
            res.SmallHolesFound = holes;
            res.FilletsFound = fillets;

            int faceBefore = FaceCount(part);
            res.VolumeBeforeMm3 = GetVolumeMm3(model);
            await emit("Gauge", null, "done",
                holes + " small hole" + (holes == 1 ? "" : "s") + " + " + fillets + " fillet" + (fillets == 1 ? "" : "s") +
                " found under ⌀" + (thrDiaM * 1000.0).ToString("0.0") + "mm  ·  " + faceBefore + " faces");

            // ---- IDEMPOTENT (Rule #5): nothing to remove → already simplified ----
            if (targets.Count == 0)
            {
                // pre-existing bug (found regression-sweeping, not itself a test-loop finding — see
                // cut-smooth-chain-live): the cloud can non-deterministically dispatch action=geometry_defeature
                // for a request that has NOTHING to do with holes/fillets at all (e.g. "cut the arm off at the
                // shoulder and smooth the surface where it was" — a body-part cut, a genuine capability gap, NOT
                // a defeature request). Zero targets found on THAT kind of request isn't "already simplified" —
                // it's a misroute that happened to scan clean. Cross-check against this handler's OWN narrow,
                // well-tested vocabulary (IsDefeatureIntent) before claiming a verified idempotent success: if the
                // raw intent doesn't even look like a defeature ask, this is honestly UNVERIFIED, not vacuously true.
                bool looksLikeDefeature = IsDefeatureIntent(intent);
                res.AlreadySimplified = looksLikeDefeature;
                res.Verified = looksLikeDefeature;
                res.VolumeAfterMm3 = res.VolumeBeforeMm3;
                res.Info = looksLikeDefeature
                    ? "Already simplified — no small holes or fillets under ⌀" + (thrDiaM * 1000.0).ToString("0.0") + "mm remain to remove."
                    : "No small holes or fillets under ⌀" + (thrDiaM * 1000.0).ToString("0.0") + "mm found, and this doesn't read as a " +
                      "defeature/simplify request in the first place — if you meant something else (removing a specific " +
                      "feature, cutting a body part), this handler can't do that. The part is unchanged.";
                await emit("Mender", null, "done", looksLikeDefeature ? "nothing to remove — already simplified" : "nothing to remove — and this wasn't a defeature request");
                return res;
            }

            // ---- preview line, then remove IN-PLACE, one detail at a time (Rule #3 / #4) ----
            await emit("Mender", "removing " + holes + " hole" + (holes == 1 ? "" : "s") + " + " + fillets + " fillet" + (fillets == 1 ? "" : "s") + " (delete-and-patch)", "run", null);

            var skip = new HashSet<string>();
            int removed = 0, holesRemoved = 0, filletsRemoved = 0;
            bool apiUsable = true;
            int guard = targets.Count * 3 + 8;   // re-scan loop bound (topology changes each removal)

            while (guard-- > 0)
            {
                Target cur = ScanTargets(part, thrDiaM).FirstOrDefault(t => !skip.Contains(t.Sig));
                if (cur == null) break;   // all remaining targets removed or skipped

                double vB = GetVolumeMm3(model);
                int wB = SafeWhatsWrong(model);

                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = ((Entity)cur.Face).Select4(false, null); } catch { }
                if (!sel) { skip.Add(cur.Sig); continue; }

                // On this 3DEXPERIENCE R2026x interop the in-place delete-face op is IFeatureManager.EditDeleteFace(Refill)
                // (returns bool; Refill=1 => delete-and-patch/heal), NOT InsertDeleteFace. It still inserts an undoable
                // Delete-Face tree feature — grab the newly-appended one (guarded by a feature-count delta) for rollback.
                Feature df = null; bool threw = false;
                int featsBefore = FeatureCountSafe(model);
                try { if (model.FeatureManager.EditDeleteFace(DeleteAndPatch) && FeatureCountSafe(model) > featsBefore) df = LastFeature(model); }
                catch { threw = true; }
                if (threw) { apiUsable = false; try { model.ClearSelection2(true); } catch { } break; }  // → fallback path
                try { model.ClearSelection2(true); } catch { }
                try { model.EditRebuild3(); } catch { }

                int wA = SafeWhatsWrong(model);
                double vA = GetVolumeMm3(model);

                // ACCEPT only if the heal is clean AND the volume did not drop. Filling a hole or sharpening a fillet
                // RAISES volume; removing a boss/pin would DROP it — so this per-face geometry gate (Rule #6) both proves
                // the heal worked and auto-rejects anything that would delete real material we should keep.
                bool volOk = vA > 0 && vB > 0 && vA > vB - Math.Max(1.0, vB * 1e-6);
                bool ok = df != null && wA <= wB && volOk;

                if (ok)
                {
                    removed++;
                    if (cur.Hole) holesRemoved++; else filletsRemoved++;
                    await emit(null, null, "done", "removed " + (cur.Hole ? "hole" : "fillet") + " ⌀" + cur.DiaMm.ToString("0.0") + "mm (" + removed + ")");
                }
                else
                {
                    // roll this one back so a failed heal never ships a broken face, then skip it (Rule #4 partial success)
                    if (df != null)
                    {
                        try { df.Select2(false, 0); model.EditDelete(); } catch { }
                        try { model.EditRebuild3(); } catch { }
                    }
                    skip.Add(cur.Sig);
                    await emit(null, null, "done", "skipped ⌀" + cur.DiaMm.ToString("0.0") + "mm — won't heal cleanly, left intact");
                }
            }

            res.FacesRemoved = removed;

            // ---- FALLBACK: in-place API unusable, or nothing healed in place → write a defeatured COPY ----
            if (removed == 0)
            {
                await Fallback(model, res, apiUsable, holes + fillets, emit);
                return res;
            }

            // ---- INDEPENDENT verification of the in-place result (Rule #6): re-count faces + re-measure volume ----
            await emit("Sentinel", "verifying the simpler solid (independent re-measure)", "run", null);
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            int faceAfter = FaceCount(part);
            res.VolumeAfterMm3 = GetVolumeMm3(model);

            bool faceDropped = faceAfter <= faceBefore - removed;   // each removal drops >=1 face (patch may merge more)
            bool volRose = res.VolumeAfterMm3 > 0 && res.VolumeBeforeMm3 > 0 &&
                           res.VolumeAfterMm3 > res.VolumeBeforeMm3 + Math.Max(1e-3, res.VolumeBeforeMm3 * 1e-9);
            res.Verified = res.RebuildErrors == 0 && removed > 0 && faceDropped && volRose;

            double dVol = res.VolumeAfterMm3 - res.VolumeBeforeMm3;
            await emit("Sentinel", null, res.Verified ? "done" : "fail",
                removed + " removed · faces " + faceBefore + "→" + faceAfter + " · volume +" + dVol.ToString("N0") + " mm³" +
                (res.RebuildErrors == 0 ? " · rebuild clean" : " · " + res.RebuildErrors + " rebuild error(s)"));

            res.Info = BuildInPlaceInfo(res, holesRemoved, filletsRemoved, holes + fillets, faceBefore, faceAfter);
            return res;
        }

        // ---- write a defeatured copy next to the original; the original is never overwritten ----
        private static async Task Fallback(IModelDoc2 model, GeometryDefeatureResult res, bool apiUsable, int detailCount, Func<string, string, string, string, Task> emit)
        {
            string src = null; try { src = model.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(src))
            {
                res.Error = (apiUsable
                    ? detailCount + " detail face(s) found but none would heal in place"
                    : "in-place face-delete isn't available on this build") +
                    ", and the part hasn't been saved to disk yet — save it once so Forge can write a defeatured copy beside it, then rerun.";
                return;
            }

            string dir = Path.GetDirectoryName(src);
            string outPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(src) + "_forge-simplified.SLDPRT");
            await emit("Mender", (apiUsable ? "in-place heal didn't take" : "in-place face-delete unavailable on this build") + " — writing a defeatured copy", "run", null);

            try { model.Extension.SaveDeFeaturedFile(outPath); }
            catch (Exception ex) { res.Error = "Couldn't write a defeatured copy (" + ex.GetType().Name + "). Your original is untouched."; return; }

            bool wrote = false; try { wrote = File.Exists(outPath) && new FileInfo(outPath).Length > 0; } catch { }
            if (!wrote) { res.Error = "SaveDeFeaturedFile produced no file — your original is untouched; the part may have nothing SolidWorks' defeature can strip."; return; }

            res.OutputFile = outPath;
            // HONEST: the copy exists, but v1 does NOT re-open + re-measure it, so we have NOT independently
            // confirmed it is actually simpler. Verified stays false (Character #1: no unverified success claim);
            // the failure corpus correctly harvests this as not_verified until v2 opens + face-counts the copy.
            res.Verified = false;
            res.Info = "Wrote a defeatured copy: " + outPath + " — a separate, simpler part (your original is untouched). " +
                       "NOT yet independently verified: open it to confirm the detail was stripped (v2 will re-measure the copy).";
            await emit("Mender", null, "done", "defeatured copy written: " + Path.GetFileName(outPath));
        }

        // ---- verdict-first, numbers not adjectives, only what was VERIFIED (Character #1/#2/#3) ----
        private static string BuildInPlaceInfo(GeometryDefeatureResult r, int holesRemoved, int filletsRemoved, int found, int faceBefore, int faceAfter)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(r.Verified ? "Simplified in place — " : "Partial simplify — ");
            sb.Append(r.FacesRemoved + " of " + found + " detail face" + (found == 1 ? "" : "s") + " removed (" +
                      holesRemoved + " hole" + (holesRemoved == 1 ? "" : "s") + ", " + filletsRemoved + " fillet" + (filletsRemoved == 1 ? "" : "s") + "). ");
            sb.Append("Faces " + faceBefore + "→" + faceAfter + ", volume " + r.VolumeBeforeMm3.ToString("N0") + "→" +
                      r.VolumeAfterMm3.ToString("N0") + " mm³, rebuild " + (r.RebuildErrors == 0 ? "clean" : r.RebuildErrors + " error(s)") + ".");
            int left = found - r.FacesRemoved;
            if (left > 0) sb.Append(" " + left + " left intact (wouldn't heal cleanly).");
            sb.Append(" One Ctrl+Z restores everything; Forge didn't save.");
            return sb.ToString();
        }

        // ================= geometry helpers =================

        private static object[] SolidBodies(PartDoc part)
        { try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { return null; } }

        // solid volume in mm^3 via the whole-doc mass-property engine (the ground truth sums per-body IBody2
        // GetMassProperties — a different path, so verification is a genuine cross-check, not the same math twice)
        private static double GetVolumeMm3(IModelDoc2 model)
        {
            try { var mp = model.Extension.CreateMassProperty(); return mp == null ? -1 : mp.Volume * 1e9; }
            catch { return -1; }
        }

        private static int FaceCount(PartDoc part)
        {
            int n = 0;
            foreach (var bo in SolidBodies(part) ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                if (faces != null) n += faces.Length;
            }
            return n;
        }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        private static int FeatureCountSafe(IModelDoc2 model)
        { try { return model.FeatureManager.GetFeatureCount(false); } catch { return -1; } }

        // the last feature in the tree — a delete-face op appends its feature at the rollback bar (tree end)
        private static Feature LastFeature(IModelDoc2 model)
        {
            Feature last = null;
            try { var f = model.FirstFeature() as Feature; while (f != null) { last = f; f = f.GetNextFeature() as Feature; } }
            catch { }
            return last;
        }

        // small-detail diameter threshold (m): an explicit "under 5mm" in the text wins; else 8% of the smallest bbox span,
        // clamped so it is never sub-0.5mm and never more than half the smallest span (a whole wall isn't "detail").
        private static double ThresholdDiaM(string intent, double minSpanM)
        {
            double mm = -1;
            string c = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v) && v > 0) mm = v;
            double thrM = mm > 0 ? mm / 1000.0 : DefaultFrac * minSpanM;
            double floor = MinThrMm / 1000.0;
            double ceil = minSpanM > 0 ? 0.5 * minSpanM : double.MaxValue;
            if (thrM < floor) thrM = floor;
            if (thrM > ceil) thrM = ceil;
            return thrM;
        }

        private static double MinBboxSpanM(object[] bodies)
        {
            double[] bb = null;
            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                double[] b = null; try { b = body.GetBodyBox() as double[]; } catch { }
                if (b == null || b.Length < 6) continue;
                if (bb == null) bb = new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
                else
                {
                    bb[0] = Math.Min(bb[0], b[0]); bb[1] = Math.Min(bb[1], b[1]); bb[2] = Math.Min(bb[2], b[2]);
                    bb[3] = Math.Max(bb[3], b[3]); bb[4] = Math.Max(bb[4], b[4]); bb[5] = Math.Max(bb[5], b[5]);
                }
            }
            if (bb == null) return 0;
            return Math.Min(bb[3] - bb[0], Math.Min(bb[4] - bb[1], bb[5] - bb[2]));
        }

        // find every small cylindrical hole-face (concave) and small fillet face (convex cylinder / small torus).
        // Re-reads the bodies fresh every call so it always sees the CURRENT topology after each in-place delete.
        private static List<Target> ScanTargets(PartDoc part, double thrDiaM)
        {
            var list = new List<Target>();
            foreach (var bo in SolidBodies(part) ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    if (s == null) continue;

                    bool isCyl = false, isTor = false;
                    try { isCyl = s.IsCylinder(); } catch { }
                    try { isTor = s.IsTorus(); } catch { }

                    if (isCyl)
                    {
                        double[] cp = null; try { cp = s.CylinderParams as double[]; } catch { }
                        if (cp == null || cp.Length < 7) continue;
                        double dia = cp[6] * 2.0;
                        if (dia <= 0 || dia > thrDiaM) continue;
                        bool concave = CylinderConcave(face, s, cp);
                        string sig = "C:" + R(cp[0]) + "," + R(cp[1]) + "," + R(cp[2]) + ";" + R(cp[6]);
                        list.Add(new Target { Face = face, DiaMm = dia * 1000.0, Hole = concave, Sig = sig });
                    }
                    else if (isTor)
                    {
                        double[] tp = null; try { tp = s.TorusParams as double[]; } catch { }
                        if (tp == null || tp.Length < 8) continue;
                        double minor = tp[7];               // fillet radius of a rounded edge/corner
                        if (minor <= 0 || minor * 2.0 > thrDiaM) continue;
                        string sig = "T:" + R(tp[0]) + "," + R(tp[1]) + "," + R(tp[2]) + ";" + R(tp[7]);
                        list.Add(new Target { Face = face, DiaMm = minor * 2.0 * 1000.0, Hole = false, Sig = sig });
                    }
                }
            }
            return list;
        }

        // concave (a hole) iff the face's OUTWARD normal points toward the cylinder axis (solid material is OUTSIDE the
        // cylinder). Convex (an external round/boss) iff it points away. Sampled at the face centroid.
        private static bool CylinderConcave(Face2 face, Surface s, double[] cp)
        {
            try
            {
                double[] box = face.GetBox() as double[];
                if (box == null || box.Length < 6) return true;   // unmeasurable → treat as a hole (attempt it; Rule #4 fail-closed to "loose")
                double[] center = { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };
                double[] P = face.GetClosestPointOn(center[0], center[1], center[2]) as double[];
                if (P == null || P.Length < 3) return true;

                double[] n = s.EvaluateAtPoint(P[0], P[1], P[2]) as double[];
                if (n == null || n.Length < 3) return true;
                double nl = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]); if (nl < 1e-9) return true;
                double[] nout = { n[0] / nl, n[1] / nl, n[2] / nl };
                bool reversed = false; try { reversed = face.FaceInSurfaceSense(); } catch { }
                if (reversed) { nout[0] = -nout[0]; nout[1] = -nout[1]; nout[2] = -nout[2]; }

                double[] O = { cp[0], cp[1], cp[2] };
                double[] a = { cp[3], cp[4], cp[5] };
                double al = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]); if (al < 1e-9) return true;
                a[0] /= al; a[1] /= al; a[2] /= al;
                double[] d = { P[0] - O[0], P[1] - O[1], P[2] - O[2] };
                double axial = d[0] * a[0] + d[1] * a[1] + d[2] * a[2];
                double[] w = { d[0] - axial * a[0], d[1] - axial * a[1], d[2] - axial * a[2] };
                double wl = Math.Sqrt(w[0] * w[0] + w[1] * w[1] + w[2] * w[2]); if (wl < 1e-9) return true;
                double radialDot = (nout[0] * w[0] + nout[1] * w[1] + nout[2] * w[2]) / wl;
                return radialDot < 0;   // normal points inward toward the axis → concave → a hole
            }
            catch { return true; }
        }

        private static string R(double m) => (m * 1e5).ToString("F0");   // 0.01mm-quantised signature component
    }
}
