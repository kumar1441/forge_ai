using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for create_drawing. A brand-new drawing has no path yet (Forge never saves), so unlike
        // every other doc-lifecycle GT this one can't look it up by path — the best available independent signal
        // is the SolidWorks session's own notion of the active document, re-read fresh here rather than trusting
        // the handler's own Title/SheetWidthM/SheetHeightM report.
        public static JObject MeasureCreateDrawing(ISldWorks app)
        {
            var res = new JObject();
            IModelDoc2 active = null;
            try { active = app.IActiveDoc2 as IModelDoc2; } catch { }
            if (active == null) { res["isDrawing"] = false; return res; }

            bool isDrawing = false;
            try { isDrawing = (int)active.GetType() == (int)swDocumentTypes_e.swDocDRAWING; } catch { }
            res["isDrawing"] = isDrawing;
            if (!isDrawing) return res;

            try { res["title"] = active.GetTitle(); } catch { }
            var drw = active as IDrawingDoc;
            var sheet = drw?.GetCurrentSheet() as ISheet;
            res["hasSheet"] = sheet != null;
            if (sheet != null)
            {
                double w = 0, h = 0;
                try { sheet.GetSize(ref w, ref h); } catch { }
                res["sheetWidthM"] = w;
                res["sheetHeightM"] = h;
            }
            return res;
        }
    }
}
