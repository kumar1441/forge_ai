using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for capture_section (tool 235). Deliberately does NOT trust the handler's own
    /// PngWidth/PngHeight/Changed — re-derives both the before and after scratch paths from the SAME intent text
    /// (CaptureSection.ResolveOutputPath, a pure function, same shape as GroundTruth.CaptureViewport), independently
    /// re-decodes the AFTER file's BMP header itself (2026-08-01: switched from PNG/SaveAs to BMP/SaveBMP — see the
    /// GOTCHA in CaptureSection.cs), and independently re-compares the raw bytes of the before/after files (a
    /// separate byte-for-byte read, not the handler's own SHA-256 fields) to confirm the cut actually changed the
    /// rendered image rather than trusting the handler's self-reported "Changed" flag. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCaptureSection(IModelDoc2 model, string intent)
        {
            var mo = new JObject();
            string beforePath = CaptureSection.ResolveOutputPath(intent, "before");
            string afterPath = CaptureSection.ResolveOutputPath(intent, "after");
            mo["beforePath"] = beforePath;
            mo["afterPath"] = afterPath;

            bool beforeExists = File.Exists(beforePath);
            bool afterExists = File.Exists(afterPath);
            mo["beforeExists"] = beforeExists;
            mo["afterExists"] = afterExists;
            if (!afterExists) return mo;

            long afterLen = 0; try { afterLen = new FileInfo(afterPath).Length; } catch { }
            mo["afterBytes"] = afterLen;

            int w = -1, h = -1;
            try
            {
                using (var fs = File.OpenRead(afterPath))
                {
                    byte[] header = new byte[26];
                    int read = fs.Read(header, 0, 26);
                    if (read == 26 && header[0] == (byte)'B' && header[1] == (byte)'M')
                    {
                        w = header[18] | (header[19] << 8) | (header[20] << 16) | (header[21] << 24);
                        h = System.Math.Abs(header[22] | (header[23] << 8) | (header[24] << 16) | (header[25] << 24));
                    }
                }
            }
            catch { }
            mo["width"] = w;
            mo["height"] = h;

            bool changed = false;
            if (beforeExists)
            {
                try
                {
                    byte[] b1 = File.ReadAllBytes(beforePath);
                    byte[] b2 = File.ReadAllBytes(afterPath);
                    changed = b1.Length != b2.Length;
                    if (!changed)
                    {
                        for (int i = 0; i < b1.Length; i++) { if (b1[i] != b2[i]) { changed = true; break; } }
                    }
                }
                catch { }
            }
            mo["changedFromBaseline"] = changed;
            return mo;
        }
    }
}
