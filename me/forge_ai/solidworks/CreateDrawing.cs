using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateDrawingResult
    {
        public bool Created;
        public string TemplatePath;
        public string Title;
        public double SheetWidthM;
        public double SheetHeightM;
        public string Error;
    }

    /// <summary>
    /// CreateDrawing — tool 101. Creates a NEW, empty drawing document (template + sheet size only — no views;
    /// that's insert_standard_views, tool 102, a separate not-yet-built tool). Uses ISldWorks.NewDocument with
    /// the user's configured default drawing template (swDefaultTemplateDrawing), falling back to the stock
    /// "ansi.drwdot" that ships with every SolidWorks install if the preference is unset. The new document is
    /// never saved (Forge never saves by default — same as every other WRITE handler; the user gets an explicit
    /// save_document/save_document_as, tools 125/126, when they actually want it on disk).
    /// </summary>
    public static class CreateDrawing
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // drawing_package (its own tool) owns rebuild/export/pdf/dangling/dimension/package/batch phrasing;
            // flat_dxf owns the sheet-metal flat-pattern vocabulary. Neither is "make a new drawing".
            if (Regex.IsMatch(c, @"\b(package|pdf|export|rebuild|dangling|dimension|dimensions|batch)\b")) return false;
            if (Regex.IsMatch(c, @"\b(dxf|dwg|flat[- ]?pattern|laser|nest|sheet metal|sheetmetal)\b")) return false;
            bool drw = Regex.IsMatch(c, @"\b(drawing|drawings)\b");
            bool verb = Regex.IsMatch(c, @"\b(create|make|new|start|generate|open)\b");
            return drw && verb;
        }

        public static async Task<CreateDrawingResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateDrawingResult();
            await emit("Drafter", "finding the drawing template", "run", null);

            string template = null;
            try { template = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing); } catch { }
            if (string.IsNullOrEmpty(template) || !File.Exists(template))
            {
                string exeDir = null; try { exeDir = Path.GetDirectoryName(app.GetExecutablePath()); } catch { }
                string fallback = !string.IsNullOrEmpty(exeDir) ? Path.Combine(exeDir, @"..\data\templates\ansi.drwdot") : null;
                if (fallback != null && File.Exists(fallback)) template = fallback;
            }
            if (string.IsNullOrEmpty(template) || !File.Exists(template))
            {
                res.Error = "Couldn't find a drawing template on this install.";
                return res;
            }
            res.TemplatePath = template;
            await emit("Drafter", null, "done", Path.GetFileName(template));

            await emit("Sheeter", "creating the drawing", "run", null);
            try
            {
                var newDoc = app.NewDocument(template, (int)swDwgPaperSizes_e.swDwgPaperAsize, 0, 0) as IModelDoc2;
                if (newDoc == null) { res.Error = "NewDocument returned nothing — the template may be invalid."; return res; }

                var drw = newDoc as IDrawingDoc;
                if (drw == null) { res.Error = "The new document isn't a drawing."; return res; }

                try { res.Title = newDoc.GetTitle(); } catch { }
                var sheet = drw.GetCurrentSheet() as ISheet;
                if (sheet != null)
                {
                    double w = 0, h = 0;
                    try { sheet.GetSize(ref w, ref h); } catch { }
                    res.SheetWidthM = w;
                    res.SheetHeightM = h;
                }
                res.Created = true;
                await emit("Sheeter", null, "done", res.Title);
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }
    }
}
