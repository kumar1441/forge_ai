using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CaptureSectionResult
    {
        public bool Success;
        public string Plane;         // resolved standard plane the cut ran through: "Front Plane" | "Top Plane" | "Right Plane"
        public string BeforePath;
        public string AfterPath;
        public long BeforeBytes = -1;
        public long AfterBytes = -1;
        public int PngWidth = -1;
        public int PngHeight = -1;
        public bool Changed;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CaptureSection (tool 235 "capture_section") — screenshot with a live section cut, so a caller can verify
    /// internal geometry (wall thickness, hole depths, rib placement) that an exterior view hides. Distinct from
    /// InsertSectionView (tool 104, DRAWING-only 2D section views on a sheet) and CaptureViewport (tool 234,
    /// which explicitly excludes "section" wording so this tool owns it) — this one cuts the live 3D graphics
    /// area of a PART or ASSEMBLY via IModelDoc2.ModelViewManager.CreateSectionView, confirmed live via redist
    /// DLL reflection (IModelViewManager.CreateSectionView(ISectionViewData) / CreateSectionViewData() /
    /// RemoveSectionView(), found alongside IFeatureManager.InsertLiveSectionPlane — a graphics-only cut, not a
    /// feature-tree write).
    ///
    /// Approach: resolve a standard plane from the text (front/top/right, default front — same plane vocabulary
    /// CaptureViewport already uses for its named views), frame isometric + capture a BMP baseline BEFORE
    /// cutting, then cut through the plane feature (model.FirstFeature() walk, same FindFeatureByName idiom every
    /// AddX handler in this repo uses) and capture a second BMP AFTER. Left cut afterward — same "no restore"
    /// precedent as CaptureViewport leaving the reoriented view (the whole point is to SEE the cut).
    ///
    /// GOTCHA (2026-08-01, found live on this box): this used to export via IModelDocExtension.SaveAs(...,".png",
    /// ...), which renders at the SW APPLICATION WINDOW's on-screen pixel size, not a requested size. This box's
    /// virtual display is only 640x480, and with the Forge task pane docked the graphics viewport was squeezed to
    /// an ~84x264 sliver — a blank gray placeholder, not a real render, silently. Growing ISldWorks.FrameWidth/
    /// FrameHeight doesn't help (the OS clamps window size to ~the virtual display bounds regardless of what's
    /// requested). Fix: IModelDoc2.SaveBMP(path, width, height) takes EXPLICIT pixel dimensions and renders
    /// off-screen at that size regardless of the actual window/display size — confirmed live (1024x768 request ->
    /// a real, correctly-sized, non-blank 24bpp bitmap). Switched both the before/after capture calls AND the
    /// output format (BMP header, not PNG IHDR) to this API.
    ///
    /// Verification (fail closed, Rule #6): a `true` CreateSectionView return is not trusted alone (same lesson
    /// as CaptureViewport/SaveDocumentAs — don't trust a return code, re-read independently). The AFTER BMP's own
    /// header must decode to a real width/height (same as CaptureViewport), AND the AFTER file's raw bytes
    /// must differ from the BEFORE baseline's raw bytes (SHA-256 compare) — if the cut silently no-ops, the
    /// rendered image is byte-identical to the pre-cut baseline and this catches it instead of reporting success
    /// on an unchanged picture. No model geometry changes and nothing is saved.
    /// </summary>
    public static class CaptureSection
    {
        public const int CaptureWidth = 1024;
        public const int CaptureHeight = 768;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(section|cutaway)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(screenshot|snapshot|picture|photo|capture|image)\b")
                || Regex.IsMatch(c, @"\bshow\s+me\b")
                || Regex.IsMatch(c, @"\bsee\s+(inside|through)\b");
            return verb;
        }

        public static async Task<CaptureSectionResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CaptureSectionResult();
            if (model == null) { res.Error = "Open a part or assembly to capture a section view."; return res; }

            int docType = 0; try { docType = model.GetType(); } catch { }
            if (docType == (int)swDocumentTypes_e.swDocDRAWING)
            { res.Error = "This is a drawing — use insert_section_view for a drawing sheet, not capture_section."; return res; }

            string planeName = ParsePlaneName(intent);
            res.Plane = planeName;

            var planeFeat = FindFeatureByName(model, planeName);
            if (planeFeat == null) { res.Error = "Couldn't find '" + planeName + "' in the feature tree to cut through."; return res; }

            string beforePath = ResolveOutputPath(intent, "before");
            string afterPath = ResolveOutputPath(intent, "after");
            try { if (File.Exists(beforePath)) File.Delete(beforePath); } catch { }
            try { if (File.Exists(afterPath)) File.Delete(afterPath); } catch { }

            await emit("Sectioner", "framing the isometric view", "run", null);
            try
            {
                model.ClearSelection2(true);
                model.ShowNamedView2("*Isometric", (int)swStandardViews_e.swIsometricView);
                model.ViewZoomtofit2();
            }
            catch (Exception ex) { res.Error = "Couldn't set the isometric view: " + ex.Message; return res; }

            bool ok0 = false;
            try
            {
                ok0 = model.SaveBMP(beforePath, CaptureWidth, CaptureHeight);
            }
            catch (Exception ex) { res.Error = "Baseline capture failed: " + ex.Message; return res; }
            if (!ok0 || !File.Exists(beforePath))
            { res.Error = "SolidWorks couldn't render the pre-cut baseline."; return res; }

            byte[] beforeHash = Sha256File(beforePath);
            res.BeforeBytes = SafeLength(beforePath);

            await emit("Sectioner", "cutting through the " + planeName, "run", null);
            ModelViewManager mvm = null;
            try { mvm = model.ModelViewManager; } catch { }
            if (mvm == null) { res.Error = "Couldn't access the model view manager."; return res; }

            bool ok1 = false;
            try
            {
                var svd = mvm.CreateSectionViewData();
                svd.FirstPlane = planeFeat;
                svd.FirstOffset = 0;
                svd.ShowSectionCap = true;
                ok1 = mvm.CreateSectionView(svd);
            }
            catch (Exception ex) { res.Error = "CreateSectionView failed: " + ex.Message; return res; }

            if (!ok1)
            {
                res.Diag = "CreateSectionView returned false";
                res.Error = "SolidWorks couldn't cut a section view — may be dead headless on this build.";
                await emit("Sectioner", null, "fail", res.Error);
                return res;
            }

            try { model.ViewZoomtofit2(); } catch { }

            await emit("Sectioner", "rendering the cut to a bitmap", "run", null);
            bool ok2 = false;
            try
            {
                ok2 = model.SaveBMP(afterPath, CaptureWidth, CaptureHeight);
            }
            catch (Exception ex) { res.Error = "Section capture failed: " + ex.Message; return res; }

            if (!ok2 || !File.Exists(afterPath))
            {
                res.Error = "SolidWorks couldn't render the sectioned viewport.";
                await emit("Sectioner", null, "fail", res.Error);
                return res;
            }

            res.AfterBytes = SafeLength(afterPath);
            int w, h;
            bool validBmp = TryReadBmpSize(afterPath, out w, out h);
            res.PngWidth = w; res.PngHeight = h;

            if (!validBmp || w <= 0 || h <= 0)
            {
                try { File.Delete(afterPath); } catch { }
                res.Error = "Wrote a file but it's not a real bitmap (w=" + w + ", h=" + h + ") — rendering may have failed silently.";
                await emit("Sectioner", null, "fail", "no valid bitmap produced");
                return res;
            }

            byte[] afterHash = Sha256File(afterPath);
            bool changed = !HashesEqual(beforeHash, afterHash);
            res.Changed = changed;
            res.Diag = "beforeBytes=" + res.BeforeBytes + " afterBytes=" + res.AfterBytes + " changed=" + changed;

            if (!changed)
            {
                res.Error = "The section cut produced no visible change — the image is byte-identical to the pre-cut baseline (silent no-op).";
                await emit("Sectioner", null, "fail", res.Error);
                return res;
            }

            res.Success = true;
            res.BeforePath = beforePath; res.AfterPath = afterPath;
            res.Info = "Cut a section through the " + planeName + " and captured it: " + w + "x" + h + " bitmap (" + res.AfterBytes + " bytes). " +
                "The section stays visible so you can inspect the internal geometry; Forge didn't save.";
            await emit("Sectioner", null, "done", res.Info);
            return res;
        }

        // Deterministic (pure function of intent text) — a scratch temp path the independent GT can re-derive
        // and check on disk itself, the same shape as CaptureViewport.ResolveOutputPath.
        public static string ResolveOutputPath(string intent, string tag)
        {
            string slug = ParsePlaneName(intent).Replace(" ", "").ToLowerInvariant();
            return Path.Combine(Path.GetTempPath(), "forge-section-" + tag + "-" + slug + ".bmp");
        }

        private static string ParsePlaneName(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\btop\b|\bhorizontal\b")) return "Top Plane";
            if (Regex.IsMatch(c, @"\bright\b|\bside\b")) return "Right Plane";
            return "Front Plane";
        }

        private static Feature FindFeatureByName(IModelDoc2 model, string name)
        {
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (string.Equals(nm, name, StringComparison.OrdinalIgnoreCase)) return f;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return null;
        }

        private static long SafeLength(string path)
        { try { return new FileInfo(path).Length; } catch { return -1; } }

        private static byte[] Sha256File(string path)
        {
            try { using (var sha = SHA256.Create()) using (var fs = File.OpenRead(path)) return sha.ComputeHash(fs); }
            catch { return null; }
        }

        private static bool HashesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // Decode ONLY the BITMAPFILEHEADER magic ('BM') + BITMAPINFOHEADER width/height (bytes 18-25, little-endian)
        // — same recipe GroundTruth.CaptureSection re-derives independently. Height in a BMP row order can be
        // negative (top-down); Math.Abs it, the sign doesn't matter for "is this a real, non-degenerate image".
        internal static bool TryReadBmpSize(string path, out int width, out int height)
        {
            width = -1; height = -1;
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    byte[] header = new byte[26];
                    int read = fs.Read(header, 0, 26);
                    if (read < 26) return false;
                    if (header[0] != (byte)'B' || header[1] != (byte)'M') return false;
                    width = header[18] | (header[19] << 8) | (header[20] << 16) | (header[21] << 24);
                    height = System.Math.Abs(header[22] | (header[23] << 8) | (header[24] << 16) | (header[25] << 24));
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
