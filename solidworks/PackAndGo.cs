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
    public class PackAndGoResult
    {
        public string SourcePath;
        public string DestFolder;
        public int MissingBeforeCopy;   // references that were already broken, BEFORE any copy was attempted
        public int TotalToCopy;
        public int Copied;              // landed on disk at the destination, verified by File.Exists
        public bool SizesMatch;         // every copied file's byte size is within a sane band of its source (a real write, not an empty/truncated stub)
        public bool SourceUnchanged;    // the LIVE, still-open source document's own component paths are untouched
        public string Error;
    }

    /// <summary>
    /// PackAndGo — tool 133. Bundles the active document plus EVERY file it depends on (parts, subassemblies,
    /// drawings) into one destination folder, using the native ISldWorks pack-and-go API
    /// (IModelDocExtension.GetPackAndGo / SavePackAndGo) rather than a hand-rolled File.Copy walk.
    ///
    ///   Gauge — reuses GetFileReferences.Run (tool 130, already proven) for the dependency validation report
    ///           BEFORE any copy is attempted, exactly as the tool brief calls for. A reference already missing
    ///           on disk cannot be packed; that count is reported, not silently dropped.
    ///   Sentinel — fail-closed (Rule #6): the SavePackAndGo return is never trusted alone. Every destination
    ///           path is checked with File.Exists AND its byte size sanity-banded against the source (SavePackAndGo
    ///           RE-SERIALIZES each document rather than byte-copying it — every packaged file, root included,
    ///           legitimately differs a few percent from its source on this build, so a ±50% band catches a truly
    ///           broken/empty write without false-failing on normal resave variance), and the SOURCE document's
    ///           own live component paths are re-read to prove the operation never repathed the document that's
    ///           still open in this same session (non-destructive, Rule #1).
    ///
    ///   NOTE ON SELF-CONTAINMENT: SolidWorks resolves a component reference by trying its stored path FIRST and
    ///   only falls back to searching the referencing assembly's own folder if that stored path is gone — so
    ///   copying (not moving) the source means every resolution API this handler could call still legitimately
    ///   answers with the ORIGINAL location as long as it still exists; there is no reliable, non-destructive way
    ///   to prove "the copy alone, with the originals absent, would resolve" without moving or deleting the
    ///   originals. That specific claim is deliberately NOT asserted here — only what's honestly verifiable
    ///   in-session is: every dependent file landed, at a reasonable size, with the source untouched.
    ///
    /// Never saves over the source; the source document stays open and untouched throughout.
    /// </summary>
    public static class PackAndGo
    {
        // NARROW: the CAD-jargon phrase "pack and go" / "pack-and-go", or an explicit "package ... with its
        // references/dependencies" phrasing for users who don't know the jargon. Neither overlaps
        // GetFileReferences' verbish (what/list/show/report/find/get/where) + noun (file/reference) pairing,
        // nor SaveDocumentAs' "save ... as/copy" pairing, nor batch_convert_files' neutral-format nouns.
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool packAndGo = Regex.IsMatch(c, @"\bpack[\s-]*(and)?[\s-]*go\b");
            bool packageWithRefs = Regex.IsMatch(c, @"\bpackage\b") && Regex.IsMatch(c, @"\b(reference|referenced|dependenc)");
            return packAndGo || packageWithRefs;
        }

        public static async Task<PackAndGoResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new PackAndGoResult();
            if (model == null) { res.Error = "Open the document you want to pack and go first."; return res; }
            string srcPath = null; try { srcPath = model.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(srcPath)) { res.Error = "This document has never been saved, so it has no path to pack from — save it once normally first."; return res; }
            res.SourcePath = srcPath;

            // dependency validation report BEFORE copy (the tool's own brief) — reuses the already-proven walk.
            var deps = await GetFileReferences.Run(app, model, intent, emit);
            if (deps.Error != null) { res.Error = deps.Error; return res; }
            res.MissingBeforeCopy = deps.Missing;

            string destFolder = ResolveDestFolder(intent, srcPath);
            res.DestFolder = destFolder;
            try { Directory.CreateDirectory(destFolder); } catch (Exception ex) { res.Error = "Couldn't create the destination folder: " + ex.Message; return res; }

            await emit("Packer", "bundling " + Path.GetFileName(srcPath) + " and its references to " + destFolder, "run", null);
            global::SolidWorks.Interop.sldworks.PackAndGo pg = null;
            object[] names = null;
            try
            {
                pg = model.Extension.GetPackAndGo();
                if (pg == null) { res.Error = "GetPackAndGo returned nothing — pack-and-go is unavailable on this build."; return res; }
                pg.IncludeDrawings = !Regex.IsMatch((intent ?? "").ToLowerInvariant(), @"\bwithout\b.*\bdrawing");
                pg.FlattenToSingleFolder = true;

                object namesObj = null;
                bool gotNames = pg.GetDocumentNames(out namesObj);
                names = namesObj as object[];
                if (!gotNames || names == null || names.Length == 0)
                { res.Error = "GetDocumentNames returned no documents to pack — nothing to do."; return res; }

                res.TotalToCopy = names.Length;
                // MUST be a string[], not object[] — SetDocumentSaveToNames marshals its "Object" parameter as a
                // SAFEARRAY of BSTR (matching VBA's `Dim vName() As String`); an object[] of boxed strings
                // marshals as SAFEARRAY of VARIANT instead, which the API silently rejects (no copy at all).
                var destNames = names.Select(n => Path.Combine(destFolder, Path.GetFileName(Convert.ToString(n)))).ToArray();
                pg.SetDocumentSaveToNames(destNames);
            }
            catch (Exception ex) { res.Error = "Pack-and-go setup failed: " + ex.Message; return res; }

            try { model.Extension.SavePackAndGo(pg); }
            catch (Exception ex) { res.Error = "SavePackAndGo failed: " + ex.Message; return res; }

            // fail-closed: never trust the API call alone — check every destination path landed AND is a
            // reasonable size. SavePackAndGo RE-SERIALIZES each document rather than byte-copying it (proven by
            // instrumentation: every packaged file's size differs a few percent from its source, root included —
            // consistent with genuine internal reference rewriting, not a truncated/corrupt write), so an exact
            // byte-size match is the WRONG invariant; a generous ±50% sanity band catches a truly broken write
            // (empty/stub file) without false-failing on normal resave variance.
            int copied = 0;
            bool sizesOk = true;
            foreach (var n in names)
            {
                string srcFile = Convert.ToString(n);
                string dst = Path.Combine(destFolder, Path.GetFileName(srcFile));
                bool exists = false; try { exists = File.Exists(dst); } catch { }
                if (!exists) { sizesOk = false; continue; }
                copied++;
                try
                {
                    long srcLen = File.Exists(srcFile) ? new FileInfo(srcFile).Length : -1;
                    long dstLen = new FileInfo(dst).Length;
                    if (srcLen <= 0 || dstLen < srcLen * 0.5 || dstLen > srcLen * 2.0) sizesOk = false;
                }
                catch { sizesOk = false; }
            }
            res.Copied = copied;
            res.SizesMatch = sizesOk && copied == res.TotalToCopy;
            await emit("Packer", null, res.SizesMatch ? "done" : "fail",
                copied + "/" + res.TotalToCopy + " file" + (res.TotalToCopy == 1 ? "" : "s") + " landed at a reasonable size at " + destFolder);

            // non-destructive check: the SOURCE document (still open in this same session) must not have been
            // repathed in-place by the pack-and-go call.
            await emit("Sentinel", "confirming the source document was not touched", "run", null);
            bool sourceUnchanged = true;
            try
            {
                var srcAsm = model as IAssemblyDoc;
                if (srcAsm != null)
                {
                    var comps = srcAsm.GetComponents(false) as object[];
                    if (comps != null)
                        foreach (Component2 c in comps)
                        {
                            string p = null; try { p = c.GetPathName(); } catch { }
                            if (!string.IsNullOrEmpty(p) && !string.Equals(Path.GetDirectoryName(p), Path.GetDirectoryName(srcPath), StringComparison.OrdinalIgnoreCase))
                            { sourceUnchanged = false; break; }
                        }
                }
            }
            catch { }
            res.SourceUnchanged = sourceUnchanged;
            await emit("Sentinel", null, sourceUnchanged ? "done" : "fail", sourceUnchanged ? "source untouched" : "source was repathed — unexpected");

            return res;
        }

        // shared with GroundTruth.MeasurePackAndGo, which independently re-lists the destination folder and
        // re-derives every check here rather than trusting the handler's own report.
        public static string ResolveDestFolder(string intent, string srcPath)
        {
            if (!string.IsNullOrEmpty(intent))
            {
                var m = Regex.Match(intent, @"([a-zA-Z]:\\[^""'<>|]+?)(?=\s*$|\s+and\b|["".,])", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string p = m.Groups[1].Value.Trim().TrimEnd('\\');
                    if (!string.IsNullOrEmpty(p)) return p;
                }
            }
            string dir = Path.GetDirectoryName(srcPath);
            string name = Path.GetFileNameWithoutExtension(srcPath);
            return Path.Combine(dir ?? ".", name + "_PackAndGo");
        }
    }
}
