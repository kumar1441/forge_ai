using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateAssemblyResult
    {
        public bool Created;
        public string TemplatePath;
        public string Title;
        public string UnitsSet;
        public string Error;
    }

    /// <summary>
    /// CreateAssembly — tool 229. Creates a NEW, blank assembly document from the user's configured default
    /// assembly template (swDefaultTemplateAssembly), falling back to the stock "assembly.asmdot" that ships with
    /// every SolidWorks install if the preference is unset. Same shape as CreatePart (tool 228)/CreateDrawing
    /// (tool 101) but for swDocASSEMBLY. If the request states units, reuses SetDocumentUnits.Run on the freshly
    /// created document — no new unit-setting API surface. Never saves (Forge never saves by default).
    /// </summary>
    public static class CreateAssembly
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(create|make|start|open|new)\b");
            bool obj = Regex.IsMatch(c, @"\b(new|blank|empty)\s+assembly\b|\bassembly\s+(document|file|template)\b");
            return verb && obj;
        }

        public static async Task<CreateAssemblyResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateAssemblyResult();
            await emit("Drafter", "finding the assembly template", "run", null);

            string template = null;
            try { template = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly); } catch { }
            if (string.IsNullOrEmpty(template) || !File.Exists(template))
            {
                string exeDir = null; try { exeDir = Path.GetDirectoryName(app.GetExecutablePath()); } catch { }
                string fallback = !string.IsNullOrEmpty(exeDir) ? Path.Combine(exeDir, @"..\data\templates\assembly.asmdot") : null;
                if (fallback != null && File.Exists(fallback)) template = fallback;
            }
            if (string.IsNullOrEmpty(template) || !File.Exists(template))
            {
                res.Error = "Couldn't find an assembly template on this install.";
                return res;
            }
            res.TemplatePath = template;
            await emit("Drafter", null, "done", Path.GetFileName(template));

            await emit("Sheeter", "creating the assembly", "run", null);
            try
            {
                var newDoc = app.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (newDoc == null) { res.Error = "NewDocument returned nothing — the template may be invalid."; return res; }

                int docType = 0;
                try { docType = newDoc.GetType(); } catch { }
                if (docType != (int)swDocumentTypes_e.swDocASSEMBLY) { res.Error = "The new document isn't an assembly."; return res; }

                try { res.Title = newDoc.GetTitle(); } catch { }
                res.Created = true;
                await emit("Sheeter", null, "done", res.Title);

                string units = (intent ?? "").ToLowerInvariant();
                if (Regex.IsMatch(units, @"\b(mm|millimet(?:er|re)s?|cm|centimet(?:er|re)s?|met(?:er|re)s?|inch(?:es)?|in|ft|feet|foot)\b"))
                {
                    var ur = await SetDocumentUnits.Run(app, newDoc, intent, emit);
                    if (ur.Verified) res.UnitsSet = ur.TargetLabel;
                }
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }
    }
}
