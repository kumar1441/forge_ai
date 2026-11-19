using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DeleteReplaceFaceResult
    {
        public string TargetLabel;
        public int FaceCountBefore = -1;
        public int FaceCountAfter = -1;
        public double VolumeBeforeMm3 = -1;
        public double VolumeAfterMm3 = -1;
        public int RebuildErrors = -1;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 227 — delete_replace_face (WRITE). "delete this face" / "remove the fillet face" / "swap the top face".
    /// Import-repair / advanced-edit tool: removes ONE explicitly-targeted face from the solid and heals the
    /// opening via IFeatureManager.EditDeleteFace(DeleteAndPatch) — the same in-place delete-and-heal API
    /// GeometryDefeature.cs already proved live (this build exposes it as EditDeleteFace, not the documented
    /// InsertDeleteFace). Distinct from GeometryDefeature (which bulk-scans and removes EVERY small hole/fillet
    /// automatically): this targets the ONE face the user described, on any face type/size — useful for a single
    /// explicit ask or for a face GeometryDefeature's small-detail heuristic wouldn't pick up.
    ///
    /// "Replace" is implemented as delete-and-heal to the surrounding surface — a true two-body face swap
    /// (InsertReplaceFace2) needs a second reference surface that can't be synthesized from a plain text command,
    /// so this is the honest, provable subset: the target face is gone and the neighbours patch cleanly, without
    /// claiming a swap-in surface it never had.
    ///
    /// Face targeting from text: a direction word (top/bottom/front/back/left/right) picks the planar face most
    /// aligned to it — one specific face, one honest try. "fillet"/"round"/"curve"/"radius"/"blend" (and the
    /// no-cue default) targets the class of small CYLINDER/TORUS faces (the same surface classes
    /// GeometryDefeature.cs's proven ScanTargets restricts itself to — a complex blend/spline face is excluded
    /// from candidacy entirely, it rarely heals). EditDeleteFace turns out to be genuinely unreliable PER FACE
    /// even within that well-classified set (confirmed live: a top-edge fillet band, then a freshly-drilled small
    /// hole, both had EditDeleteFace return false) — so for the undirected class, up to 6 smallest candidates are
    /// tried in order, stopping at the first that actually heals, the same partial-success discipline
    /// GeometryDefeature.cs already uses. "largest"/"smallest" pick by area across all faces.
    ///
    /// Verified by an INDEPENDENT re-count of the solid's own faces/volume/rebuild-errors after each attempt
    /// (never the API's own boolean return): face count must actually drop and the rebuild must stay clean. A
    /// heal that doesn't take is rolled back (Ctrl+Z equivalent) before the next candidate, so a failed attempt
    /// never ships broken geometry. Never saves.
    /// </summary>
    public static class DeleteReplaceFace
    {
        private const int DeleteAndPatch = 1;   // swDeleteFaceOptions_e.swDeleteFaceOptions_DeleteAndPatch — not exposed in this interop, per GeometryDefeature.cs precedent

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(delete|remove|swap|replace|get rid of|erase)\b")) return false;
            return Regex.IsMatch(c, @"\bfaces?\b");
        }

        private class Candidate { public Face2 Face; public double AreaMm2; public bool Planar; public bool CylOrTorus; public double[] Normal; }

        public static async Task<DeleteReplaceFaceResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DeleteReplaceFaceResult();
            var part = model as PartDoc;
            if (model == null || part == null) { res.Error = "Open a part to delete or replace a face on it."; return res; }

            await emit("Gauge", "reading the solid's faces", "run", null);
            var faces = CollectFaces(part);
            if (faces.Count == 0) { res.Error = "No solid body on this part — there's no face to delete or replace."; return res; }
            res.FaceCountBefore = faces.Count;
            res.VolumeBeforeMm3 = VolumeMm3(part);

            string label;
            List<Candidate> attempts = ResolveCandidates(faces, intent, out label);
            if (attempts.Count == 0) { res.Error = "Couldn't tell which face you mean — try \"delete the top face\" or \"remove the fillet face\"."; return res; }
            res.TargetLabel = label;

            // EditDeleteFace is genuinely unreliable per-face on this build even for a well-classified small
            // cylinder/torus face (GeometryDefeature.cs's own proven design tries MANY candidates and reports
            // partial success, not a single guaranteed heal) — so a directional/explicit single face gets exactly
            // one honest try, but the undirected "fillet/curved" default tries each candidate smallest-first,
            // stopping at the first that actually heals, same discipline.
            await emit("Mender", "removing the " + label, "run", null);
            string diag = "";
            foreach (var cand in attempts)
            {
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = ((Entity)cand.Face).Select4(false, null); } catch { }
                if (!sel) { diag += "select-failed "; continue; }

                int featsBefore = FeatureCount(model);
                Feature df = null; bool threw = false; bool apiReturn = false;
                try { apiReturn = model.FeatureManager.EditDeleteFace(DeleteAndPatch); if (apiReturn && FeatureCount(model) > featsBefore) df = LastFeature(model); }
                catch (Exception ex) { threw = true; diag += "EX(" + ex.GetType().Name + ") "; }
                try { model.ClearSelection2(true); } catch { }
                if (threw) { continue; }
                try { model.EditRebuild3(); } catch { }

                var facesAfter = CollectFaces(part);
                int faceCountAfter = facesAfter.Count;
                double volAfter = VolumeMm3(part);
                int rebuildErrors = -1; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

                bool healed = df != null && rebuildErrors == 0 && faceCountAfter < res.FaceCountBefore &&
                              volAfter > 0 && volAfter >= res.VolumeBeforeMm3 - Math.Max(1.0, res.VolumeBeforeMm3 * 1e-6);
                diag += "apiReturn=" + apiReturn + " df=" + (df != null) + " healed=" + healed + " | ";

                if (healed)
                {
                    res.FaceCountAfter = faceCountAfter;
                    res.VolumeAfterMm3 = volAfter;
                    res.RebuildErrors = rebuildErrors;
                    res.Verified = true;
                    res.Info = "Removed the " + label + " — " + res.FaceCountBefore + " -> " + res.FaceCountAfter + " faces, " +
                               Trim(res.VolumeBeforeMm3) + "mm³ -> " + Trim(res.VolumeAfterMm3) + "mm³, rebuild clean.";
                    await emit("Mender", null, "done", res.FaceCountBefore + " -> " + res.FaceCountAfter + " faces");
                    return res;
                }

                if (df != null) { try { df.Select2(false, 0); model.EditDelete(); model.EditRebuild3(); } catch { } }
            }

            res.Error = "The " + label + " won't heal cleanly on this build — tried " + attempts.Count + " candidate" + (attempts.Count == 1 ? "" : "s") +
                        ", left the part intact rather than ship a broken patch. (" + diag.Trim() + ")";
            await emit("Mender", null, "fail", res.Error);
            return res;
        }

        private static List<Candidate> CollectFaces(PartDoc part)
        {
            var list = new List<Candidate>();
            object[] bodies = null; try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            foreach (var bo in bodies ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    bool plane = false; try { plane = s != null && s.IsPlane(); } catch { }
                    // Only a clean CYLINDER or TORUS surface heals reliably via delete-and-patch (the same surface
                    // classes GeometryDefeature.cs's proven ScanTargets restricts itself to) — a complex blend/
                    // spline face is excluded from candidacy entirely rather than risked.
                    bool cylOrTorus = false;
                    if (!plane && s != null) { try { cylOrTorus = s.IsCylinder() || s.IsTorus(); } catch { } }
                    double area = 0; try { area = face.GetArea(); } catch { }
                    if (area <= 0) continue;
                    double[] n = null; try { n = plane ? (face.Normal as double[]) : null; } catch { }
                    list.Add(new Candidate { Face = face, AreaMm2 = area * 1e6, Planar = plane, CylOrTorus = cylOrTorus, Normal = n });
                }
            }
            return list;
        }

        private const int MaxCurvedAttempts = 6;   // how many smallest cylinder/torus candidates to try before giving up

        // Returns an ORDERED list of face attempts (not just one): a directional ask names ONE specific face, so it
        // gets exactly that single candidate (retrying a different face would misrepresent what got removed); the
        // undirected "fillet/curved" default is a CLASS of similar small features, so it offers up to
        // MaxCurvedAttempts smallest cylinder/torus candidates for the caller to try in order, stopping at the
        // first that actually heals — EditDeleteFace is genuinely unreliable per-face even within this
        // well-classified candidate set, so a single try is not enough to call the class "unhealable".
        private static List<Candidate> ResolveCandidates(List<Candidate> faces, string intent, out string label)
        {
            label = null;
            string c = (intent ?? "").ToLowerInvariant();
            bool wantCurved = Regex.IsMatch(c, @"\b(fillet|round|curve|curved|radius|blend)\b");
            bool wantLargest = Regex.IsMatch(c, @"\b(largest|biggest)\b");
            bool wantSmallest = Regex.IsMatch(c, @"\b(smallest|smaller)\b");
            string dir = Regex.IsMatch(c, @"\btop\b") ? "top" :
                         Regex.IsMatch(c, @"\bbottom\b") ? "bottom" :
                         Regex.IsMatch(c, @"\bfront\b") ? "front" :
                         Regex.IsMatch(c, @"\bback\b") ? "back" :
                         Regex.IsMatch(c, @"\bleft\b") ? "left" :
                         Regex.IsMatch(c, @"\bright\b") ? "right" : null;

            if (dir != null && !wantCurved)
            {
                double[] want = dir == "top" ? new[] { 0.0, 1.0, 0.0 } : dir == "bottom" ? new[] { 0.0, -1.0, 0.0 } :
                                 dir == "front" ? new[] { 0.0, 0.0, 1.0 } : dir == "back" ? new[] { 0.0, 0.0, -1.0 } :
                                 dir == "right" ? new[] { 1.0, 0.0, 0.0 } : new[] { -1.0, 0.0, 0.0 };
                Candidate best = null; double bestDot = -2;
                foreach (var f in faces.Where(f => f.Planar && f.Normal != null && f.Normal.Length >= 3))
                {
                    double dot = f.Normal[0] * want[0] + f.Normal[1] * want[1] + f.Normal[2] * want[2];
                    if (dot > bestDot) { bestDot = dot; best = f; }
                }
                if (best != null && bestDot > 0.5) { label = dir + " face"; return new List<Candidate> { best }; }
            }

            // Curved candidates are restricted to clean CYLINDER/TORUS surfaces only — a complex blend/spline face
            // rarely heals via delete-and-patch, so it's never offered as the "fillet/curved" default or cue match.
            var curved = faces.Where(f => f.CylOrTorus).OrderBy(f => f.AreaMm2).ToList();
            if (wantCurved && curved.Count > 0) { label = "fillet/curved face"; return curved.Take(MaxCurvedAttempts).ToList(); }

            if (wantLargest) { var b = faces.OrderByDescending(f => f.AreaMm2).FirstOrDefault(); if (b != null) { label = "largest face"; return new List<Candidate> { b }; } }
            if (wantSmallest)
            {
                if (curved.Count > 0) { label = "smallest face"; return curved.Take(MaxCurvedAttempts).ToList(); }
                var bp = faces.Where(f => f.Planar).OrderBy(f => f.AreaMm2).FirstOrDefault();
                if (bp != null) { label = "smallest face"; return new List<Candidate> { bp }; }
            }

            if (curved.Count > 0) { label = "fillet/curved face"; return curved.Take(MaxCurvedAttempts).ToList(); }
            var fallback = faces.Where(f => f.Planar).OrderBy(f => f.AreaMm2).FirstOrDefault();
            if (fallback != null) { label = "smallest face"; return new List<Candidate> { fallback }; }
            return new List<Candidate>();
        }

        private static double VolumeMm3(PartDoc part)
        {
            double vol = 0;
            object[] bodies = null; try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            foreach (var bo in bodies ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                try { var mp = body.GetMassProperties(0) as double[]; if (mp != null && mp.Length >= 4) vol += mp[3]; } catch { }
            }
            return vol * 1e9;
        }

        private static int FeatureCount(IModelDoc2 model)
        {
            int n = 0; var f = model.FirstFeature() as Feature;
            while (f != null) { n++; f = f.GetNextFeature() as Feature; }
            return n;
        }

        private static Feature LastFeature(IModelDoc2 model)
        {
            Feature last = null; var f = model.FirstFeature() as Feature;
            while (f != null) { last = f; f = f.GetNextFeature() as Feature; }
            return last;
        }

        private static string Trim(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
