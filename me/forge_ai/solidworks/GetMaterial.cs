using System;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetMaterialResult
    {
        public string Material;   // "" / null → not specified
        public string Database;
        public bool HasMaterial;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — get_material (READ). Reads the assigned material of a part (name + database), the read counterpart to
    /// set_material. "what's this made of", "what material is assigned", "does it have a material". Read-only.
    /// </summary>
    public static class GetMaterial
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\bmaterial\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(what|which|read|get|show|assigned|made of|does it have|current)\b") &&
                   !System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(set|change|make|apply|assign|to)\b") &&   // those are set_material
                   !System.Text.RegularExpressions.Regex.IsMatch(c, @"\bdensit(y|ies)\b");   // that's get_material_density
        }

        public static async Task<GetMaterialResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetMaterialResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to read its material."; return res; }

            await emit("Reader", "reading material", "run", null);
            string db = null, mat = null;
            try { mat = ((PartDoc)model).GetMaterialPropertyName2("", out db); } catch { }
            res.Material = mat; res.Database = db;
            res.HasMaterial = !string.IsNullOrWhiteSpace(mat);

            await emit("Reader", null, "done", res.HasMaterial ? mat : "no material assigned");
            res.Info = res.HasMaterial
                ? "Material: " + mat + (string.IsNullOrEmpty(db) ? "" : " (" + db + ")") + "."
                : "No material is assigned to this part.";
            return res;
        }
    }
}
