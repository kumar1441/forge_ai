using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT component-position measurement. Where the handler pulls translation from the transform ARRAY
        // slots (9,10,11), this derives each component's world position by TRANSFORMING the part origin point (0,0,0)
        // through the component transform via MathUtility — a different extraction that must land on the same point.
        // Returns count + a name→[x,y,z]mm map so the harness can match the handler's positions.
        public static JObject MeasureGetComponentTransform(ISldWorks app, IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["error"] = "not an assembly"; return res; }
            var mu = (MathUtility)app.GetMathUtility();

            int count = 0;
            var pos = new JObject();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                var xf = c.Transform2 as MathTransform; if (xf == null) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                var origin = mu.CreatePoint(new double[] { 0, 0, 0 }) as MathPoint;
                var world = origin.MultiplyTransform(xf) as MathPoint;
                var w = world.ArrayData as double[];
                if (w != null && w.Length >= 3 && nm != null && pos[nm] == null)
                    pos[nm] = new JArray(w[0] * 1000.0, w[1] * 1000.0, w[2] * 1000.0);
                count++;
            }
            res["count"] = count;
            res["positions"] = pos;
            return res;
        }
    }
}
