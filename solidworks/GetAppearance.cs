using System;
using System.Globalization;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetAppearanceResult
    {
        public int R = -1, G = -1, B = -1;   // 0-255
        public bool HasColor;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — get_appearance (READ). Reads a part's base display colour (RGB 0-255) from MaterialPropertyValues. The
    /// read counterpart to apply_appearance ("what colour is this"). Read-only; the ground truth reads the same array
    /// and both must agree on the colour.
    /// </summary>
    public static class GetAppearance
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(colour|color|appearance|rgb)\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(what|which|read|get|show|is)\b") &&
                   !System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(set|change|make|apply|paint|to)\b");
        }

        public static async Task<GetAppearanceResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetAppearanceResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to read its colour."; return res; }

            await emit("Reader", "reading appearance", "run", null);
            double[] mv = null; try { mv = model.MaterialPropertyValues as double[]; } catch { }
            if (mv != null && mv.Length >= 3 && mv[0] >= 0)
            {
                res.R = (int)Math.Round(mv[0] * 255); res.G = (int)Math.Round(mv[1] * 255); res.B = (int)Math.Round(mv[2] * 255);
                res.HasColor = true;
            }

            await emit("Reader", null, "done", res.HasColor ? "RGB(" + res.R + "," + res.G + "," + res.B + ")" : "no explicit colour (inherited)");
            res.Info = res.HasColor
                ? "Base colour RGB(" + res.R + ", " + res.G + ", " + res.B + ")."
                : "No explicit part colour is set (it inherits the material/default appearance).";
            return res;
        }
    }
}
