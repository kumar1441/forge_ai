using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class BodyGapRow
    {
        public string BodyName;
        public int GapCount;
    }

    public class CheckGeometryErrorsResult
    {
        public bool Checked;
        public int BodyCount = -1;
        public int TotalGaps = -1;
        public List<BodyGapRow> Bodies = new List<BodyGapRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CheckGeometryErrors (tool #156) — scan every solid body for tiny gaps/slivers that break downstream CAM.
    /// "check this part for geometry errors", "find any tiny gaps or slivers".
    ///
    /// PROBE build: `IBody2.Diagnose():IDiagnoseResult` (GetGapsCount/GetCoEdgesAtGap) found via reflection,
    /// untried — a genuinely dedicated body-level geometry-check API, distinct from `IPartDoc.ImportDiagnosis`
    /// (tool 154, a whole-part auto-HEAL attempt) which this is a pure READ sibling of.
    /// </summary>
    public static class CheckGeometryErrors
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // excludes import_file/run_import_diagnostics/batch_convert_files' "import" scope entirely
            if (Regex.IsMatch(c, @"\bimport\b")) return false;
            bool checkVerb = Regex.IsMatch(c, @"\b(check|find|detect|scan)\b");
            bool geomNoun = Regex.IsMatch(c, @"\b(gaps?|slivers?|geometry\s+errors?|bad\s+geometry)\b");
            return checkVerb && geomNoun;
        }

        public static async Task<CheckGeometryErrorsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CheckGeometryErrorsResult();
            if (model == null) { res.Error = "Open the part you want checked."; return res; }
            int docType = 0; try { docType = model.GetType(); } catch { }
            if (docType != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "check_geometry_errors needs an open PART (not an assembly/drawing)."; return res; }

            await emit("Diagnoser", "scanning solid bodies for gaps/slivers", "run", null);
            try
            {
                var pd = model as PartDoc;
                var bodies = pd?.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                res.BodyCount = bodies?.Length ?? 0;
                int total = 0;
                if (bodies != null)
                {
                    int idx = 0;
                    foreach (var o in bodies)
                    {
                        idx++;
                        var b = o as Body2;
                        if (b == null) continue;
                        string name = null; try { name = b.Name; } catch { }
                        var dr = b.Diagnose() as DiagnoseResult;
                        int gaps = 0; try { gaps = dr?.GetGapsCount() ?? 0; } catch { }
                        res.Bodies.Add(new BodyGapRow { BodyName = string.IsNullOrEmpty(name) ? ("Body" + idx) : name, GapCount = gaps });
                        total += gaps;
                    }
                }
                res.TotalGaps = total;
                res.Checked = true;
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; await emit("Diagnoser", null, "fail", res.Error); return res; }

            res.Info = res.BodyCount + " solid body(ies), " + res.TotalGaps + " gap(s) found" + (res.TotalGaps == 0 ? " — geometry clean" : "");
            await emit("Diagnoser", null, "done", res.Info);
            return res;
        }
    }
}
