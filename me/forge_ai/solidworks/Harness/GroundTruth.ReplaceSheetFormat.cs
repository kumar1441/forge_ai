using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for replace_sheet_format — re-resolves the sheet via GetSheetNames()/get_Sheet(name)
        // (a genuinely different lookup path than the handler's own GetCurrentSheet()) and re-reads
        // GetSheetFormatName() itself.
        public static JObject MeasureReplaceSheetFormat(IModelDoc2 model)
        {
            var res = new JObject();
            string sheetName = null;
            string formatName = null;
            try
            {
                var dd = model as DrawingDoc;
                var names = dd?.GetSheetNames() as object[];
                if (names != null && names.Length > 0)
                {
                    sheetName = (string)names[0];
                    var sheet = dd.get_Sheet(sheetName) as ISheet;
                    if (sheet != null)
                    {
                        formatName = sheet.GetSheetFormatName();
                        // SetTemplateName is the live write path (SetSheetFormatName no-ops on this build's
                        // default-template sheets, which carry no named format at all) — fall back to
                        // GetTemplateName() exactly like the handler's own after-read does.
                        if (string.IsNullOrEmpty(formatName)) formatName = sheet.GetTemplateName();
                    }
                }
            }
            catch { }
            res["sheetName"] = sheetName;
            res["sheetFormatName"] = formatName;
            return res;
        }
    }
}
