using System;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetRefGeometryResult
    {
        public int Planes;
        public int Axes;
        public int Points;
        public int CoordSystems;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — list_reference_geometry (READ). Counts a part/assembly's reference geometry: reference planes, axes,
    /// points, and coordinate systems. Useful before creating more ("does it already have a mid-plane") and for
    /// understanding a model's construction scaffold. Read-only; own tree traversal by feature type.
    /// </summary>
    public static class GetRefGeometry
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(reference geometry|ref geometry|reference planes|reference axes|datum)\b") ||
                   (System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(how many|count|list)\b") &&
                    System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(reference plane|reference planes|ref plane|ref axis|reference ax|coordinate system)\b"));
        }

        public static async Task<GetRefGeometryResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetRefGeometryResult();
            if (model == null) { res.Error = "Open a part or assembly to list its reference geometry."; return res; }

            await emit("Reader", "reading reference geometry", "run", null);
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                switch (tn)
                {
                    case "RefPlane": res.Planes++; break;
                    case "RefAxis": res.Axes++; break;
                    case "RefPoint": res.Points++; break;
                    case "CoordSys": res.CoordSystems++; break;
                }
                f = f.GetNextFeature() as Feature;
            }

            await emit("Reader", null, "done", res.Planes + " planes · " + res.Axes + " axes · " + res.Points + " points · " + res.CoordSystems + " coord systems");
            res.Info = res.Planes + " reference plane" + (res.Planes == 1 ? "" : "s") + ", " + res.Axes + " ax" + (res.Axes == 1 ? "is" : "es") +
                       ", " + res.Points + " point" + (res.Points == 1 ? "" : "s") + ", " + res.CoordSystems + " coordinate system" + (res.CoordSystems == 1 ? "" : "s") + ".";
            return res;
        }
    }
}
