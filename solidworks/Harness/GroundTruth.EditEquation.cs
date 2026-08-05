using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the edit_equation handler. Shares NO code with EditEquation.cs.
    ///
    /// Editing an equation/global variable changes its VALUE, so the harness asserts:
    ///   1. the named equation now reads the expected NewValue (test config passes `equationName` + `equationTo`)
    ///   2. equationCount UNCHANGED   (an edit is not an add/delete)
    ///   3. rebuildErrors == 0
    /// and the rerun is idempotent (run2 == run1). This GT re-reads every equation's name→value from its OWN
    /// IEquationMgr traversal (the handler's read used the same manager, but the assertion cross-checks the specific
    /// named value the test asked for, computed here independently).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureEditEquation(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            int count = 0;
            var byName = new JObject();
            try
            {
                var eqMgr = model.GetEquationMgr();
                if (eqMgr != null)
                {
                    count = eqMgr.GetCount();
                    for (int i = 0; i < count; i++)
                    {
                        string eq = null; try { eq = eqMgr.get_Equation(i); } catch { }
                        var m = eq != null ? Regex.Match(eq, "^\\s*\"([^\"]+)\"") : Match.Empty;
                        if (!m.Success) continue;
                        double val = double.NaN; try { val = eqMgr.get_Value(i); } catch { }
                        byName[m.Groups[1].Value.Trim()] = val;
                    }
                }
            }
            catch (Exception ex) { mo["error"] = ex.GetType().Name + ": " + ex.Message; }

            int rb = 0; try { rb = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["equationCount"] = count;
            mo["byName"] = byName;               // { "thickness": 3.0, "D1@Sketch1": 10.0, ... } — the grader checks its target here
            mo["rebuildErrors"] = rb;
            mo["hasEquations"] = count > 0;
            mo["fingerprint"] = new JObject { ["equationCount"] = count, ["rebuildErrors"] = rb };
            return mo;
        }
    }
}
