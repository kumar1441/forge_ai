using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for capture_viewport (tool 234). Deliberately does NOT trust the handler's own
    /// FilePath/PngWidth/PngHeight — re-derives the expected output path from the SAME intent text
    /// (CaptureViewport.ResolveOutputPath, a pure function, not the handler's result object, same shape as
    /// GroundTruth.SaveDocumentAs), then independently re-reads and re-decodes the file's own BMP header itself
    /// (2026-08-01: switched from PNG/SaveAs to BMP/SaveBMP — see the GOTCHA in CaptureViewport.cs). Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCaptureViewport(IModelDoc2 model, string intent)
        {
            var mo = new JObject();
            string expected = CaptureViewport.ResolveOutputPath(intent);
            mo["expectedPath"] = expected;

            bool exists = File.Exists(expected);
            mo["exists"] = exists;
            if (!exists) return mo;

            long len = 0; try { len = new FileInfo(expected).Length; } catch { }
            mo["bytes"] = len;

            int w = -1, h = -1;
            try
            {
                using (var fs = File.OpenRead(expected))
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
            return mo;
        }
    }
}
