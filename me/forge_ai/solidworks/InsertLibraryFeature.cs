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
    public class InsertLibraryFeatureResult
    {
        public string LibraryFolder;
        public string LibFeatPath;
        public string TargetFace;      // description of the face selected (for diagnosis)
        public int FeatureCountBefore;
        public int FeatureCountAfter;
        public bool Verified;          // feature tree actually grew after the call
        public string Info;
        public string Error;
    }

    /// <summary>
    /// InsertLibraryFeature (tool #218) — insert a company-standard cutout/slot/mounting boss from SolidWorks'
    /// own Design Library onto the open part. Reflected FIRST (per the "reflect before building" rule for a new
    /// API surface): `IModelDoc2.InsertLibraryFeature(string):void` is the only member — it takes just the
    /// .sldlfp path and relies entirely on the CURRENT SELECTION for placement context (the same interactive
    /// recipe as File > Design Library > drag a feature onto a face). SolidWorks ships real .sldlfp files with
    /// every install (design library\features\...), so this needs no synthesized fixture — a genuine advantage
    /// over other PARKED tools that stalled on "no real .sldlfp to test with."
    ///
    /// RISK (documented, not glossed over): library features that ship as SolidWorks "Smart Features" can pop an
    /// interactive PropertyManager wizard for their configurable dimensions — the same class of headless hang
    /// already proven with `Isolator`'s visibility ops. This build's first attempt picks the SIMPLEST shipped
    /// feature (straight slot, one planar-face selection, no multi-point pattern context) specifically to
    /// minimize that risk, and is verified purely by feature-tree count (the call itself returns void — there is
    /// no success/failure signal from the API other than whether a new feature actually landed).
    ///
    /// The Library Features folder is resolved LIVE via `ISldWorks.GetUserPreferenceStringValue
    /// (swFileLocationsLibraryFeatures)` — SolidWorks' own configured File Locations setting — never a hardcoded
    /// version-specific path, so this survives a SolidWorks version upgrade unchanged.
    /// </summary>
    public static class InsertLibraryFeature
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!c.Contains("library feature") && !Regex.IsMatch(c, @"\bfrom the (design )?library\b.*\bfeature\b")) return false;
            return Regex.IsMatch(c, @"\b(insert|add|apply|place|drop|drag)\b");
        }

        // Resolves the folder(s) from SolidWorks' own File Locations setting first (never hardcoded); on this
        // build (R2026x unattended launch) that preference came back empty (proven empirically — instrumented,
        // not assumed), so this falls back to scanning the standard ProgramData install location, matched by
        // GLOB (any "SOLIDWORKS *" folder) rather than a hardcoded version number, so a version upgrade doesn't
        // break it. Then finds the first .sldlfp under whichever root(s) resolved whose name is mentioned in the
        // command; falls back to a known-simple default (straight slot — single planar-face selection, no
        // pattern/multi-point context) so a bare "insert a library feature" still acts instead of asking.
        private static string ResolveLibFeatPath(ISldWorks app, string cmd)
        {
            string raw = null;
            try { raw = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsLibraryFeatures); } catch { }
            var roots = (raw ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => Directory.Exists(s)).ToList();

            if (roots.Count == 0)
            {
                try
                {
                    const string programData = @"C:\ProgramData\SOLIDWORKS";
                    if (Directory.Exists(programData))
                    {
                        foreach (var verDir in Directory.GetDirectories(programData, "SOLIDWORKS *"))
                        {
                            string feat = Path.Combine(verDir, "design library", "features");
                            if (Directory.Exists(feat)) roots.Add(feat);
                        }
                    }
                }
                catch { }
            }
            if (roots.Count == 0) return null;

            var all = new List<string>();
            foreach (var r in roots)
            { try { all.AddRange(Directory.GetFiles(r, "*.sldlfp", SearchOption.AllDirectories)); } catch { } }
            if (all.Count == 0) return null;

            var named = all.Where(p => cmd.Contains(Path.GetFileNameWithoutExtension(p).ToLowerInvariant())).ToList();
            if (named.Count > 0) return named[0];

            // VARIANT (instrumented probe, 2026-07-30): prefer the METRIC slot over inch — this build's generated
            // fixtures are metric (mm), and an inch-based library feature silently no-op'd against one (no
            // exception, no new feature) on the first attempt; testing whether a unit-system match changes that.
            var metricSlot = all.FirstOrDefault(p => p.ToLowerInvariant().Contains(@"\metric\") &&
                Path.GetFileNameWithoutExtension(p).Equals("straight slot", StringComparison.OrdinalIgnoreCase));
            if (metricSlot != null) return metricSlot;
            var slot = all.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals("straight slot", StringComparison.OrdinalIgnoreCase));
            return slot ?? all[0];
        }

        // First planar face of the first solid body — the simplest possible placement context, no name/index
        // guessing required (avoids the selection-name-resolution fragility other handlers hit on complex trees).
        private static IFace2 FindPlanarFace(IModelDoc2 model)
        {
            try
            {
                var part = model as PartDoc;
                if (part == null) return null;
                object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                if (bodies == null || bodies.Length == 0) return null;
                var body = bodies[0] as Body2;
                object[] faces = body?.GetFaces() as object[];
                if (faces == null) return null;
                foreach (var fo in faces)
                {
                    var face = fo as IFace2;
                    var surf = face?.GetSurface() as ISurface;
                    if (surf != null && surf.IsPlane()) return face;
                }
            }
            catch { }
            return null;
        }

        public static async Task<InsertLibraryFeatureResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new InsertLibraryFeatureResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part — library features insert onto a part's face."; return res; }

            string cmd = (intent ?? "").ToLowerInvariant();

            await emit("Locator", "resolving the Design Library feature file", "run", null);
            string libPath = ResolveLibFeatPath(app, cmd);
            if (libPath == null)
            { res.Error = "Couldn't resolve any .sldlfp under SolidWorks' configured Library Features folder(s)."; return res; }
            res.LibFeatPath = libPath;
            res.LibraryFolder = Path.GetDirectoryName(libPath);
            await emit("Locator", null, "done", Path.GetFileNameWithoutExtension(libPath) + " (" + res.LibraryFolder + ")");

            await emit("Selector", "finding a face to place it on", "run", null);
            var face = FindPlanarFace(model);
            if (face == null)
            { res.Error = "Couldn't find a planar face on this part to place the library feature on."; return res; }
            bool selOk = false;
            try { selOk = ((Entity)face).Select4(false, null); } catch (Exception ex) { res.Error = "Face selection threw: " + ex.GetType().Name; return res; }
            // VARIANT (instrumented probe, 2026-07-30): a bare face selection alone did not place the feature (no
            // exception, no new feature — the same silent no-op class as InsertCombineFeature/InsertMoveFace).
            // Also append one edge of that face to the selection, matching the interactive "select a face AND an
            // edge for orientation" recipe some Design Library features expect.
            try
            {
                object[] edges = face.GetEdges() as object[];
                var edge0 = edges != null && edges.Length > 0 ? edges[0] as IEdge : null;
                if (edge0 != null) { ((Entity)edge0).Select4(true, null); res.TargetFace = "face + edge0"; }
            }
            catch { }
            if (!selOk)
            { res.Error = "Face selection returned false — can't place the library feature without a selection."; return res; }
            res.TargetFace = "first planar face, body 0";
            await emit("Selector", null, "done", "face selected");

            try { model.ForceRebuild3(false); } catch { }
            res.FeatureCountBefore = CountFeatures(model);

            await emit("Inserter", "inserting the library feature", "run", null);
            try
            {
                model.InsertLibraryFeature(libPath);
            }
            catch (Exception ex)
            {
                res.Error = "InsertLibraryFeature threw: " + ex.GetType().Name + ": " + ex.Message;
                await emit("Inserter", null, "fail", res.Error);
                return res;
            }

            res.FeatureCountAfter = CountFeatures(model);
            res.Verified = res.FeatureCountAfter > res.FeatureCountBefore;
            await emit("Inserter", null, res.Verified ? "done" : "fail",
                res.Verified ? "feature count " + res.FeatureCountBefore + " -> " + res.FeatureCountAfter : "no new feature landed");

            if (!res.Verified)
            { res.Error = "InsertLibraryFeature returned without throwing, but the feature tree didn't grow (before=" + res.FeatureCountBefore + ", after=" + res.FeatureCountAfter + ")."; return res; }

            res.Info = "Inserted \"" + Path.GetFileNameWithoutExtension(libPath) + "\" from the Design Library — feature count " +
                       res.FeatureCountBefore + " -> " + res.FeatureCountAfter + ".";
            return res;
        }

        private static int CountFeatures(IModelDoc2 model)
        {
            int n = 0;
            try
            {
                var f = model.FirstFeature() as IFeature;
                while (f != null) { n++; f = f.GetNextFeature() as IFeature; }
            }
            catch { }
            return n;
        }
    }
}
