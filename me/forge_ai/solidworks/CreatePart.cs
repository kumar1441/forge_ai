using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreatePartResult
    {
        public bool Created;
        public string TemplatePath;
        public string Title;
        public string UnitsSet;
        public string Error;
    }

    /// <summary>
    /// CreatePart — tool 228. Creates a NEW, blank part document from the user's configured default part template
    /// (swDefaultTemplatePart), falling back to the stock "part.prtdot" that ships with every SolidWorks install
    /// if the preference is unset. Same shape as CreateDrawing (tool 101) but for swDocPART. If the request states
    /// units ("create a new part in inches"), reuses SetDocumentUnits.Run on the freshly created document — no new
    /// unit-setting API surface. Never saves (Forge never saves by default).
    /// </summary>
    public static class CreatePart
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // insert_new_part_in_context (tool 230) owns explicit in-context/top-down/referencing/attached-to
            // wording — "create a new part in context on the top plane" is a top-down assembly op, not a
            // standalone document. Defense-in-depth alongside dispatch ordering, not just ordering alone.
            if (Regex.IsMatch(c, @"\bin[\s-]?context\b|\bin[\s-]?place\b|\btop[\s-]?down\b|\breferenc(e|ing)\b|\battached?\s+to\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(create|make|start|open|new)\b");
            bool obj = Regex.IsMatch(c, @"\b(new|blank|empty)\s+part\b|\bpart\s+(document|file|template)\b");
            return verb && obj;
        }

        public static async Task<CreatePartResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreatePartResult();
            await emit("Drafter", "finding the part template", "run", null);

            string template = null;
            try { template = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart); } catch { }
            if (string.IsNullOrEmpty(template) || !File.Exists(template))
            {
                string exeDir = null; try { exeDir = Path.GetDirectoryName(app.GetExecutablePath()); } catch { }
                string fallback = !string.IsNullOrEmpty(exeDir) ? Path.Combine(exeDir, @"..\data\templates\part.prtdot") : null;
                if (fallback != null && File.Exists(fallback)) template = fallback;
            }
            if (string.IsNullOrEmpty(template) || !File.Exists(template))
            {
                res.Error = "Couldn't find a part template on this install.";
                return res;
            }
            res.TemplatePath = template;
            await emit("Drafter", null, "done", Path.GetFileName(template));

            await emit("Sheeter", "creating the part", "run", null);
            try
            {
                var newDoc = app.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (newDoc == null) { res.Error = "NewDocument returned nothing — the template may be invalid."; return res; }

                int docType = 0;
                try { docType = newDoc.GetType(); } catch { }
                if (docType != (int)swDocumentTypes_e.swDocPART) { res.Error = "The new document isn't a part."; return res; }

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
