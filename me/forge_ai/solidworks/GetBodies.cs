using System;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetBodiesResult
    {
        public int SolidBodies = -1;
        public int SurfaceBodies = -1;
        public bool Multibody;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — list_bodies (READ). Counts a part's solid and surface bodies and flags multibody parts. Multibody status
    /// changes how many downstream tools behave (which body to cut, per-body mass, weldments), so surfacing it is a
    /// real support act. Read-only; own GetBodies2 read per type.
    /// </summary>
    public static class GetBodies
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // "surface" alone (not "surface bod(y|ies)") let "what's the total SURFACE AREA for the body" — a
            // get_mass_properties question that happens to contain the word "body" — get hijacked into a bare body
            // count (test-loop wrong-answer finding measure-surface-area). Require the surface trigger to actually be
            // about surface BODIES, not surface AREA.
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(bodies|body)\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(how many|count|list|multibody|multi-body|solid|surface\s*bod)\b");
        }

        public static async Task<GetBodiesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetBodiesResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to count its bodies."; return res; }

            await emit("Counter", "counting bodies", "run", null);
            var part = model as PartDoc;
            try { var s = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; res.SolidBodies = s?.Length ?? 0; } catch { res.SolidBodies = 0; }
            try { var f = part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[]; res.SurfaceBodies = f?.Length ?? 0; } catch { res.SurfaceBodies = 0; }
            res.Multibody = res.SolidBodies > 1;

            await emit("Counter", null, "done", res.SolidBodies + " solid" + (res.SolidBodies == 1 ? "" : "s") + (res.SurfaceBodies > 0 ? " · " + res.SurfaceBodies + " surface" : "") + (res.Multibody ? " · MULTIBODY" : ""));
            res.Info = res.SolidBodies + " solid bod" + (res.SolidBodies == 1 ? "y" : "ies") +
                       (res.SurfaceBodies > 0 ? ", " + res.SurfaceBodies + " surface bod" + (res.SurfaceBodies == 1 ? "y" : "ies") : "") +
                       (res.Multibody ? " — this is a MULTIBODY part." : (res.SolidBodies == 1 ? " — single-body part." : "."));
            return res;
        }
    }
}
