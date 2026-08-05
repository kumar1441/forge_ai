using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SaveBodiesAsPartsResult
    {
        public int TotalBodies;
        public int Created;       // new files written this run
        public int AlreadyThere;  // target files that already existed with a matching body volume (idempotent skip)
        public List<string> Paths = new List<string>();
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 166 — save_bodies_as_parts (WRITE). Saves every solid body of a multibody part out to its own SLDPRT
    /// file, one body per file. "save each body as a separate part" / "split the bodies out into parts". Copies
    /// each body (Body2.Copy(), the proven idiom CompareBodies.cs already uses for the exact same body list) into a
    /// fresh part document via IPartDoc.CreateFeatureFromBody3, carries the source part's active-configuration
    /// material across (SetMaterialPropertyName2 — best-effort, non-fatal if the source has none), and SaveAs's it
    /// next to the source under a "forge-bodies" sub-folder (ImportFile.cs's materialized-file naming convention).
    /// Deterministic index-based naming makes a rerun idempotent: a target file that already exists AND whose OWN
    /// re-opened body volume matches the source body's volume is skipped, never rewritten. Verified by an
    /// INDEPENDENT re-open of every resulting file (never the handler's own SaveAs return) — exactly one solid body
    /// whose volume is within 0.1% of the source body it came from. Forge never touches the SOURCE document; only
    /// new sibling files are written, and the source is left the active document when Run() returns.
    /// </summary>
    public static class SaveBodiesAsParts
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // NARROW + specific-first: requires a save/split/export-ish verb WITH BOTH a body noun and a
            // part/file noun, so a plain "save/export the part" (no body noun) and CompareBodies' plain
            // "compare the bodies" (no part/file noun) are never shadowed.
            bool verb = Regex.IsMatch(c, @"\b(save|split|export|separate|break\s*out)\w*\b");
            bool obj = Regex.IsMatch(c, @"\bbod(y|ies)\b") && Regex.IsMatch(c, @"\bparts?\b|\bfiles?\b");
            return verb && obj;
        }

        public static async Task<SaveBodiesAsPartsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SaveBodiesAsPartsResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) with multiple bodies to save them out."; return res; }

            await emit("Sentinel", "reading the solid bodies", "run", null);
            string sourcePath = null; try { sourcePath = model.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(sourcePath)) { res.Error = "The source part has never been saved — no folder to write the body files next to."; return res; }
            string sourceDir = Path.GetDirectoryName(sourcePath);
            string sourceBase = Path.GetFileNameWithoutExtension(sourcePath);
            string outDir = Path.Combine(sourceDir, "forge-bodies");
            Directory.CreateDirectory(outDir);
            string sourceTitle = null; try { sourceTitle = model.GetTitle(); } catch { }

            var bodies = new List<Body2>();
            foreach (var o in (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]) ?? new object[0])
            { var b = o as Body2; if (b != null) bodies.Add(b); }
            res.TotalBodies = bodies.Count;
            if (res.TotalBodies == 0) { res.Error = "This part has no solid bodies to save."; await emit("Sentinel", null, "fail", "no bodies"); return res; }
            if (res.TotalBodies == 1) { res.Error = "This part has only one body — nothing to split out."; await emit("Sentinel", null, "fail", "single body"); return res; }

            string template = ResolvePartTemplate(app);
            if (template == null) { res.Error = "Couldn't find a part template on this install."; return res; }

            string matDb = null, matName = null;
            try { matName = part.GetMaterialPropertyName2("", out matDb); } catch { }

            // Existing output files, matched to bodies BY VOLUME (not position): Body2 enumeration order isn't
            // guaranteed stable across separate SolidWorks sessions, so a position/name-based idempotency check
            // would mint a fresh file every session even though the geometry was already saved. Each existing file
            // can be claimed by at most one body.
            var existingFiles = Directory.Exists(outDir) ? new List<string>(Directory.GetFiles(outDir, sourceBase + "-*.SLDPRT")) : new List<string>();
            var claimed = new HashSet<string>();

            await emit("Scribe", "saving " + res.TotalBodies + " bodies to separate parts", "run", null);
            int verifiedOk = 0;
            for (int i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                double vol, area; MassOf(body, out vol, out area);

                // ---- idempotency: an existing output file already covers this body's volume ----
                string existing = null;
                foreach (var f in existingFiles)
                {
                    if (claimed.Contains(f)) continue;
                    if (VolumeMatches(app, f, vol)) { existing = f; break; }
                }
                if (existing != null) { claimed.Add(existing); res.Paths.Add(existing); res.AlreadyThere++; verifiedOk++; continue; }

                string bodyName = null; try { bodyName = body.Name; } catch { }
                string safe = SanitizeName(bodyName ?? ("Body" + (i + 1)));
                string targetPath = Path.Combine(outDir, sourceBase + "-" + (i + 1) + "-" + safe + ".SLDPRT");
                int dupe = 1; while (File.Exists(targetPath) || claimed.Contains(targetPath)) { targetPath = Path.Combine(outDir, sourceBase + "-" + (i + 1) + "-" + safe + "-" + (++dupe) + ".SLDPRT"); }
                res.Paths.Add(targetPath);
                claimed.Add(targetPath);

                string err = SaveOneBody(app, body, template, matName, matDb, targetPath);
                if (err != null)
                {
                    if (sourceTitle != null) { int errA = 0; try { app.ActivateDoc3(sourceTitle, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errA); } catch { } }
                    res.Error = "Body " + (i + 1) + " (" + (bodyName ?? "unnamed") + "): " + err;
                    await emit("Scribe", null, "fail", res.Error);
                    return res;
                }
                res.Created++;

                // ---- Sentinel: INDEPENDENT re-open of the file just written, never the SaveAs return ----
                if (VolumeMatches(app, targetPath, vol)) verifiedOk++;
            }

            if (sourceTitle != null) { int errA2 = 0; try { app.ActivateDoc3(sourceTitle, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errA2); } catch { } }

            res.Verified = verifiedOk == res.Paths.Count;
            if (!res.Verified)
            {
                res.Error = "Only " + verifiedOk + "/" + res.Paths.Count + " saved bodies verified independently — a file may be missing or the geometry mismatched.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = res.TotalBodies + " bodies -> " + res.TotalBodies + " part files in \"" + outDir + "\" (" + res.Created + " written, " + res.AlreadyThere + " already there). Source untouched, never saved.";
            await emit("Sentinel", null, "done", res.Created + " written, " + res.AlreadyThere + " already there");
            return res;
        }

        // copies ONE body into a fresh part doc, carries material across (best-effort), SaveAs's it, closes it.
        private static string SaveOneBody(ISldWorks app, Body2 body, string template, string matName, string matDb, string targetPath)
        {
            Body2 copy = null; try { copy = body.Copy() as Body2; } catch { }
            if (copy == null) return "Body2.Copy() failed";

            IModelDoc2 newDoc = null;
            try
            {
                newDoc = app.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (newDoc == null) return "NewDocument returned nothing";
                var newPart = newDoc as PartDoc;
                if (newPart == null) return "the new document isn't a part";

                object fe = null;
                try { fe = newPart.CreateFeatureFromBody3(copy, false, (int)swCreateFeatureBodyOpts_e.swCreateFeatureBodyCheck); } catch (Exception ex) { return "CreateFeatureFromBody3 threw (" + ex.GetType().Name + ")"; }
                if (fe == null) return "CreateFeatureFromBody3 returned nothing — the body didn't land";
                newDoc.ForceRebuild3(false);

                if (!string.IsNullOrEmpty(matName))
                { try { ((PartDoc)newDoc).SetMaterialPropertyName2("", matDb, matName); } catch { } }

                int se = 0, sw = 0;
                bool saved = newDoc.Extension.SaveAs(targetPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref se, ref sw);
                if (!saved) return "SaveAs failed (err " + se + ")";
                return null;
            }
            finally { try { if (newDoc != null) app.CloseDoc(newDoc.GetTitle()); } catch { } }
        }

        // INDEPENDENT re-open + recount: exactly 1 solid body, volume within 0.1% of expectedVol.
        private static bool VolumeMatches(ISldWorks app, string path, double expectedVol)
        {
            if (!File.Exists(path)) return false;
            IModelDoc2 doc = null;
            try
            {
                int e = 0, w = 0;
                doc = app.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref e, ref w) as IModelDoc2;
                var pd = doc as PartDoc; if (pd == null) return false;
                var arr = (pd.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]) ?? new object[0];
                if (arr.Length != 1) return false;
                var b = arr[0] as Body2; if (b == null) return false;
                double vol, area; MassOf(b, out vol, out area);
                if (expectedVol <= 0) return vol <= 1e-12;
                return Math.Abs(vol - expectedVol) / expectedVol < 0.001;
            }
            catch { return false; }
            finally { try { if (doc != null) app.CloseDoc(doc.GetTitle()); } catch { } }
        }

        private static void MassOf(Body2 body, out double vol, out double area)
        {
            vol = 0; area = 0;
            try
            {
                var mp = body.GetMassProperties(0) as double[];   // [3]=volume, [4]=surface area (SI)
                if (mp != null && mp.Length >= 5) { vol = mp[3]; area = mp[4]; }
            }
            catch { }
        }

        private static string ResolvePartTemplate(ISldWorks app)
        {
            string template = null;
            try { template = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart); } catch { }
            if (string.IsNullOrEmpty(template) || !File.Exists(template))
            {
                string exeDir = null; try { exeDir = Path.GetDirectoryName(app.GetExecutablePath()); } catch { }
                string fallback = !string.IsNullOrEmpty(exeDir) ? Path.Combine(exeDir, @"..\data\templates\part.prtdot") : null;
                if (fallback != null && File.Exists(fallback)) template = fallback;
            }
            return (!string.IsNullOrEmpty(template) && File.Exists(template)) ? template : null;
        }

        private static string SanitizeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
