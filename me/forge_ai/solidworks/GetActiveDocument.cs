using System;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetActiveDocumentResult
    {
        public string DocType;      // "part" | "assembly" | "drawing"
        public string Title;
        public string Path;
        public string ActiveConfig;
        public int ConfigCount;
        public string Units;        // length unit of the document
        public int TopLevelComponents = -1;   // assemblies only
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 1 — get_active_document (READ). The foundational context read: what's open — part / assembly / drawing,
    /// its title + path, the active configuration (and how many configs), the document's length units, and (for an
    /// assembly) the top-level component count. Every other tool assumes this context; this surfaces it. Read-only.
    /// </summary>
    public static class GetActiveDocument
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(active document|current document|what('?s| is) open|what document is open|what am i looking at|document info|doc info|what file|which document|current model|what is this document)\b");
        }

        public static async Task<GetActiveDocumentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetActiveDocumentResult();
            if (model == null) { res.Error = "No document is open."; return res; }

            await emit("Reader", "reading document context", "run", null);
            int t = (int)model.GetType();
            res.DocType = t == (int)swDocumentTypes_e.swDocPART ? "part"
                        : t == (int)swDocumentTypes_e.swDocASSEMBLY ? "assembly"
                        : t == (int)swDocumentTypes_e.swDocDRAWING ? "drawing" : "unknown";
            try { res.Title = model.GetTitle(); } catch { }
            try { res.Path = model.GetPathName(); } catch { }
            try { res.ActiveConfig = ((Configuration)model.GetActiveConfiguration()).Name; } catch { }
            try { var cfgs = model.GetConfigurationNames() as string[]; res.ConfigCount = cfgs?.Length ?? 0; } catch { }
            res.Units = UnitName(model);
            if (t == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                try { var comps = (model as AssemblyDoc).GetComponents(true) as object[]; res.TopLevelComponents = comps?.Length ?? 0; } catch { }
            }

            await emit("Reader", null, "done",
                res.DocType + " · config '" + (res.ActiveConfig ?? "?") + "'" +
                (res.TopLevelComponents >= 0 ? " · " + res.TopLevelComponents + " components" : "") + " · " + res.Units);

            res.Info = "Active " + res.DocType + " '" + (res.Title ?? "?") + "', config '" + (res.ActiveConfig ?? "?") +
                       "' (" + res.ConfigCount + " config" + (res.ConfigCount == 1 ? "" : "s") + "), units " + res.Units +
                       (res.TopLevelComponents >= 0 ? ", " + res.TopLevelComponents + " top-level components" : "") + ".";
            return res;
        }

        private static string UnitName(IModelDoc2 model)
        {
            try
            {
                int u = model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
                switch ((swLengthUnit_e)u)
                {
                    case swLengthUnit_e.swMM: return "mm";
                    case swLengthUnit_e.swCM: return "cm";
                    case swLengthUnit_e.swMETER: return "m";
                    case swLengthUnit_e.swINCHES: return "in";
                    case swLengthUnit_e.swFEET: return "ft";
                    case swLengthUnit_e.swFEETINCHES: return "ft-in";
                    case swLengthUnit_e.swMIL: return "mil";
                    case swLengthUnit_e.swUIN: return "µin";
                    default: return "unit#" + u;
                }
            }
            catch { return "?"; }
        }
    }
}
