using System;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetPartNumberResult
    {
        public string PartNumber;
        public string SourceProperty;   // which custom property held it
        public bool Found;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — get_part_number (READ). Reads a part's part-number custom property (tries the common names: PartNo,
    /// PartNumber, Part Number, Number, PN, DrawingNo). The BOM/release question "what's the part number". Read-only.
    /// </summary>
    public static class GetPartNumber
    {
        private static readonly string[] Keys = { "PartNo", "PartNumber", "Part Number", "Part No", "Number", "PN", "DrawingNo", "Drawing Number" };

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(part number|part no|part-number|partno|drawing number|\bpn\b)\b") &&
                   !System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(set|change|assign|make|renumber|auto)\b");
        }

        public static async Task<GetPartNumberResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetPartNumberResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to read its part number."; return res; }

            await emit("Reader", "reading part number", "run", null);
            string activeCfg = ""; try { activeCfg = ((Configuration)model.GetActiveConfiguration()).Name; } catch { }
            foreach (var scope in new[] { "", activeCfg })
            {
                CustomPropertyManager cpm = null;
                try { cpm = model.Extension.get_CustomPropertyManager(scope); } catch { }
                if (cpm == null) continue;
                foreach (var k in Keys)
                {
                    string val = null, resolved = null;
                    try { cpm.Get4(k, false, out val, out resolved); } catch { }
                    string v = resolved ?? val;
                    if (!string.IsNullOrWhiteSpace(v)) { res.PartNumber = v.Trim(); res.SourceProperty = k; res.Found = true; break; }
                }
                if (res.Found) break;
            }

            await emit("Reader", null, "done", res.Found ? "part number: " + res.PartNumber : "no part number property");
            res.Info = res.Found
                ? "Part number: " + res.PartNumber + " (from the '" + res.SourceProperty + "' property)."
                : "No part-number property found on this part.";
            return res;
        }
    }
}
