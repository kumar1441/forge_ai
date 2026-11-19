using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class XformRow { public string Name; public double X, Y, Z; public bool Rotated; }

    public class GetComponentTransformResult
    {
        public int Count;
        public List<XformRow> Rows = new List<XformRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 25 — get_component_transform (READ). Each component's world position (mm) + whether it carries a rotation.
    /// Answers spatial questions ("where is the bracket", "is anything off-origin"). Reads Component2.Transform2 and
    /// pulls the translation from the transform's array (indices 9,10,11) and the rotation from the 3×3 block.
    /// Read-only. The ground truth derives position a DIFFERENT way (transforms the origin point), so a mis-read shows.
    /// </summary>
    public static class GetComponentTransform
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(transform|position|positions|where is|located|location|coordinates|placement|off[- ]?origin)\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(component|components|part|parts|it|bracket|assembly)\b");
        }

        public static async Task<GetComponentTransformResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetComponentTransformResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to read component positions."; return res; }

            await emit("Locator", "reading component transforms", "run", null);
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                var xf = c.Transform2 as MathTransform; if (xf == null) continue;
                var a = xf.ArrayData as double[]; if (a == null || a.Length < 12) continue;
                var r = new XformRow();
                try { r.Name = c.Name2; } catch { }
                r.X = a[9] * 1000.0; r.Y = a[10] * 1000.0; r.Z = a[11] * 1000.0;   // translation (m → mm)
                // rotation present if the 3×3 block isn't (near) identity
                r.Rotated = Math.Abs(a[0] - 1) > 1e-6 || Math.Abs(a[4] - 1) > 1e-6 || Math.Abs(a[8] - 1) > 1e-6 ||
                            Math.Abs(a[1]) > 1e-6 || Math.Abs(a[2]) > 1e-6 || Math.Abs(a[3]) > 1e-6 ||
                            Math.Abs(a[5]) > 1e-6 || Math.Abs(a[6]) > 1e-6 || Math.Abs(a[7]) > 1e-6;
                res.Rows.Add(r);
                res.Count++;
            }

            await emit("Locator", null, "done", res.Count + " component transform" + (res.Count == 1 ? "" : "s"));
            if (res.Count == 0) { res.Error = "No components to locate."; return res; }

            var sb = new StringBuilder(res.Count + " component" + (res.Count == 1 ? "" : "s") + " located:");
            int shown = 0;
            foreach (var r in res.Rows)
            {
                if (shown++ >= 20) { sb.Append("\n… (" + (res.Count - 20) + " more)"); break; }
                sb.Append("\n• " + r.Name + " @ (" + F(r.X) + ", " + F(r.Y) + ", " + F(r.Z) + ") mm" + (r.Rotated ? " · rotated" : ""));
            }
            res.Info = sb.ToString();
            return res;
        }

        private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
