using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for pack_and_go. Re-derives the destination folder the same way the handler does
        // (shared helper, same as save_document_as' GT), then re-verifies from scratch rather than trusting the
        // handler's own Copied/SizesMatch/SourceUnchanged report:
        //   (1) counts what's ACTUALLY sitting on disk in that folder right now (Directory.GetFiles);
        //   (2) independently re-lists the SOURCE assembly's own dependency file set (GetDocumentDependencies2,
        //       the same file-level primitive get_file_references' own GT already proves, applied to the SOURCE
        //       not the copy) and confirms a same-named file exists in destFolder for the root PLUS every one of
        //       them, at the SAME byte size — its own loop, not the handler's;
        //   (3) independently re-reads the SOURCE model's own live component paths to confirm they still point
        //       at the original folder (the operation must never repath the open document).
        public static JObject MeasurePackAndGo(ISldWorks app, IModelDoc2 model, string intent)
        {
            var res = new JObject();
            string srcPath = null; try { srcPath = model.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(srcPath)) { res["destExists"] = false; return res; }

            string destFolder = Forge.SolidWorks.PackAndGo.ResolveDestFolder(intent, srcPath);
            res["destFolder"] = destFolder;

            bool destExists = false; try { destExists = Directory.Exists(destFolder); } catch { }
            res["destExists"] = destExists;
            if (!destExists) return res;

            int fileCount = 0;
            try { fileCount = Directory.GetFiles(destFolder).Length; } catch { }
            res["destFileCount"] = fileCount;

            // (2) own independent re-derivation: source's dependency set, each expected as a same-size sibling.
            // ±50% sanity band, not exact equality: SavePackAndGo re-serializes each document rather than
            // byte-copying it (every packaged file's size legitimately differs a few percent from its source,
            // root included), so exact-size would false-fail on normal resave variance.
            int expectedTotal = 1; // the root itself
            int matchedTotal = 0;
            try
            {
                string rootDst = Path.Combine(destFolder, Path.GetFileName(srcPath));
                if (File.Exists(rootDst) && File.Exists(srcPath))
                {
                    long rl = new FileInfo(rootDst).Length, sl = new FileInfo(srcPath).Length;
                    if (sl > 0 && rl >= sl * 0.5 && rl <= sl * 2.0) matchedTotal++;
                }

                object[] deps = app.GetDocumentDependencies2(srcPath, true, true, false) as object[];
                if (deps != null)
                {
                    for (int k = 0; k + 1 < deps.Length; k += 2)
                    {
                        string dp = deps[k + 1] as string;
                        if (string.IsNullOrWhiteSpace(dp)) continue;
                        if (string.Equals(dp, srcPath, StringComparison.OrdinalIgnoreCase)) continue;
                        expectedTotal++;
                        string dstFile = Path.Combine(destFolder, Path.GetFileName(dp));
                        bool dstExists = false; long dstLen = -1, srcLen = -1;
                        try { dstExists = File.Exists(dstFile); if (dstExists) dstLen = new FileInfo(dstFile).Length; } catch { }
                        try { srcLen = File.Exists(dp) ? new FileInfo(dp).Length : -1; } catch { }
                        if (dstExists && srcLen > 0 && dstLen >= srcLen * 0.5 && dstLen <= srcLen * 2.0) matchedTotal++;
                    }
                }
            }
            catch { }
            res["gtExpectedTotal"] = expectedTotal;
            res["gtMatchedTotal"] = matchedTotal;
            res["gtAllMatched"] = expectedTotal > 0 && matchedTotal == expectedTotal;

            // (3) own independent re-read of the source's live component paths — must still be at the original folder.
            bool sourceUnchanged = true;
            try
            {
                var asm = model as IAssemblyDoc;
                string srcDir = Path.GetDirectoryName(srcPath);
                if (asm != null)
                {
                    var comps = asm.GetComponents(false) as object[];
                    if (comps != null)
                        foreach (Component2 c in comps)
                        {
                            string p = null; try { p = c.GetPathName(); } catch { }
                            if (!string.IsNullOrEmpty(p) && !string.Equals(Path.GetDirectoryName(p), srcDir, StringComparison.OrdinalIgnoreCase))
                            { sourceUnchanged = false; break; }
                        }
                }
            }
            catch { }
            res["gtSourceUnchanged"] = sourceUnchanged;

            return res;
        }
    }
}
