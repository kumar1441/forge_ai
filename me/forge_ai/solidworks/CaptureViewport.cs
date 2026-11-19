using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CaptureViewportResult
    {
        public bool Success;
        public string ViewName;      // resolved standard view, e.g. "isometric"
        public string FilePath;      // written bitmap, a Forge scratch temp file (never the user's document)
        public long FileBytes = -1;
        public int PngWidth = -1;    // decoded from the BMP's own header — independent of "file exists"
        public int PngHeight = -1;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CaptureViewport (tool 234 "capture_viewport") — the LLM's EYES. A pure READ/export: renders the current
    /// part/assembly to an image so a caller can visually confirm what was actually built, without touching the model.
    ///
    /// Approach: resolve a named standard view from the text (isometric/front/top/right/left/back/bottom, default
    /// isometric — the natural first look), ShowNamedView2 + ViewZoomtofit2 (ViewZoomtofit2 already proven live via
    /// DrawingGenerator.cs), then IModelDoc2.SaveBMP(path, width, height) at an explicit fixed resolution.
    ///
    /// GOTCHA (2026-08-01, found live on this box): this used to export via IModelDocExtension.SaveAs(...,".png",
    /// ...), which renders at the SW APPLICATION WINDOW's on-screen pixel size, not a requested size. This box's
    /// virtual display is only 640x480, and with the Forge task pane docked the graphics viewport was squeezed to
    /// an ~80x212 sliver — a blank gray placeholder, not a real render, silently. This was NEVER caught by the
    /// regression bar because the assertion only checked width>0/height>0 (both true for the tiny placeholder too)
    /// — found while investigating a genuinely different bug in the sibling capture_section handler, whose extra
    /// before/after byte-diff assertion is what actually surfaced it. Growing ISldWorks.FrameWidth/FrameHeight
    /// doesn't help (the OS clamps window size to ~the virtual display bounds). Fix: SaveBMP takes EXPLICIT pixel
    /// dimensions and renders off-screen at that size regardless of the actual window/display size — confirmed
    /// live (1024x768 request -> a real, correctly-sized, non-blank 24bpp bitmap).
    ///
    /// Verification (fail closed, Rule #6): a `true` SaveBMP return is not trusted (same lesson as SaveDocumentAs
    /// and the STEP-reopen landmine — don't re-open the file in SW, read it back independently as bytes). Decode
    /// the written file's own BMP header (bytes 18-25: width/height, little-endian) and require both > 0 — a
    /// genuinely different signal than "file exists with nonzero length," so a truncated/stub write still fails.
    /// No model state changes and nothing is saved — this tool has nothing to undo.
    /// </summary>
    public static class CaptureViewport
    {
        public const int CaptureWidth = 1024;
        public const int CaptureHeight = 768;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\bsection\b")) return false;   // capture_section (tool 235) owns section-cut captures
            bool verb = Regex.IsMatch(c, @"\b(screenshot|snapshot)\b")
                || Regex.IsMatch(c, @"\bcapture\b.{0,20}\b(view|viewport|picture|image|screen)\b")
                || Regex.IsMatch(c, @"\btake\s+a\s+(picture|photo|snapshot|screenshot)\b")
                || Regex.IsMatch(c, @"\bshow\s+me\s+(a|an|the)\s+(picture|image|view|render)\b");
            return verb;
        }

        public static async Task<CaptureViewportResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CaptureViewportResult();
            if (model == null) { res.Error = "Open a part or assembly to capture a viewport image."; return res; }

            string viewName = ParseViewName(intent);
            res.ViewName = viewName;
            string outPath = ResolveOutputPath(intent);
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }   // fresh write every run — never trust a stale file from a prior session

            await emit("Lens", "framing the " + viewName + " view", "run", null);
            try
            {
                model.ClearSelection2(true);
                model.ShowNamedView2("*" + StdViewLabel(viewName), (int)StdViewId(viewName));
                model.ViewZoomtofit2();
            }
            catch (Exception ex) { res.Error = "Couldn't set the " + viewName + " view: " + ex.Message; return res; }

            await emit("Lens", null, "done", viewName + " view framed");
            await emit("Shutter", "rendering to a bitmap", "run", null);

            bool ok = false;
            try
            {
                ok = model.SaveBMP(outPath, CaptureWidth, CaptureHeight);
            }
            catch (Exception ex) { res.Error = "Viewport capture failed: " + ex.Message; return res; }

            if (!ok || !File.Exists(outPath))
            {
                res.Diag = "SaveBMP returned " + ok;
                res.Error = "SolidWorks couldn't render the viewport to an image — may be dead headless on this build.";
                await emit("Shutter", null, "fail", res.Error);
                return res;
            }

            long len = 0; try { len = new FileInfo(outPath).Length; } catch { }
            res.FilePath = outPath;
            res.FileBytes = len;

            int w, h;
            bool validBmp = TryReadBmpSize(outPath, out w, out h);
            res.PngWidth = w; res.PngHeight = h;
            res.Diag = "bytes=" + len + " validBmp=" + validBmp + " w=" + w + " h=" + h;

            if (!validBmp || w <= 0 || h <= 0)
            {
                try { File.Delete(outPath); } catch { }
                res.Error = "Wrote a file but it's not a real bitmap (w=" + w + ", h=" + h + ", " + len + " bytes) — rendering may have failed silently.";
                await emit("Shutter", null, "fail", "no valid bitmap produced");
                return res;
            }

            res.Success = true;
            await emit("Shutter", null, "done", w + "x" + h + " bitmap, " + len + " bytes");
            res.Info = "Captured the " + viewName + " view: " + w + "x" + h + " bitmap (" + len + " bytes) at " + outPath + ". Nothing in the model changed; Forge didn't save.";
            return res;
        }

        // Deterministic (pure function of intent text) — a scratch temp path the independent GT can re-derive
        // and check on disk itself, the same shape as SaveDocumentAs.ResolveOutputPath. Never near the user's file.
        public static string ResolveOutputPath(string intent)
        {
            return Path.Combine(Path.GetTempPath(), "forge-capture-" + ParseViewName(intent) + ".bmp");
        }

        private static string ParseViewName(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\bfront\b")) return "front";
            if (Regex.IsMatch(c, @"\bback\b|\brear\b")) return "back";
            if (Regex.IsMatch(c, @"\bleft\b")) return "left";
            if (Regex.IsMatch(c, @"\bright\b")) return "right";
            if (Regex.IsMatch(c, @"\btop\b")) return "top";
            if (Regex.IsMatch(c, @"\bbottom\b")) return "bottom";
            return "isometric";
        }

        private static string StdViewLabel(string viewName)
        {
            switch (viewName)
            {
                case "front": return "Front";
                case "back": return "Back";
                case "left": return "Left";
                case "right": return "Right";
                case "top": return "Top";
                case "bottom": return "Bottom";
                default: return "Isometric";
            }
        }

        private static swStandardViews_e StdViewId(string viewName)
        {
            switch (viewName)
            {
                case "front": return swStandardViews_e.swFrontView;
                case "back": return swStandardViews_e.swBackView;
                case "left": return swStandardViews_e.swLeftView;
                case "right": return swStandardViews_e.swRightView;
                case "top": return swStandardViews_e.swTopView;
                case "bottom": return swStandardViews_e.swBottomView;
                default: return swStandardViews_e.swIsometricView;
            }
        }

        // Decode ONLY the BITMAPFILEHEADER magic ('BM') + BITMAPINFOHEADER width/height (bytes 18-25, little-endian)
        // — enough to independently prove "a real image landed here," no third-party imaging library needed.
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
                    height = Math.Abs(header[22] | (header[23] << 8) | (header[24] << 16) | (header[25] << 24));
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
