using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for insert_new_part_in_context (tool 230). Deliberately does NOT trust the
    /// handler's own ComponentsBefore/ComponentsAfter/Success — re-derives the expected output path from the
    /// SAME intent text (InsertNewPartInContext.ResolveOutputPath, a pure function, same shape as
    /// GroundTruth.CaptureSection), independently re-counts the assembly's top-level components via a FRESH
    /// GetComponents(false) call, and independently checks the new file landed on disk with real bytes. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureInsertNewPartInContext(IModelDoc2 model, string intent)
        {
            var mo = new JObject();

            int componentCount = -1;
            try
            {
                var asm = model as AssemblyDoc;
                if (asm != null)
                {
                    var comps = asm.GetComponents(false) as object[];
                    componentCount = comps == null ? 0 : comps.Length;
                }
            }
            catch { }
            mo["componentCount"] = componentCount;

            // Independent re-derivation of the path the handler must have used THIS run: this Measure() call
            // always runs AFTER the handler's own insert, so (live count - 1) reconstructs the pre-insert count
            // the handler saw, without trusting the handler's own ComponentsBefore field.
            string outPath = InsertNewPartInContext.ResolveOutputPath(intent, componentCount - 1);
            mo["outputPath"] = outPath;

            bool fileExists = false;
            long fileBytes = 0;
            try { fileExists = File.Exists(outPath); if (fileExists) fileBytes = new FileInfo(outPath).Length; } catch { }
            mo["fileExists"] = fileExists;
            mo["fileBytes"] = fileBytes;

            return mo;
        }
    }
}
