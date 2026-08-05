using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class EquationInfo { public string Name; public double Value; public bool IsGlobal; public string Raw; }

    public class ListEquationsResult
    {
        public int Count;
        public int GlobalCount;
        public List<EquationInfo> Equations = new List<EquationInfo>();
        public string Info;          // human-readable summary line
        public string Error;
    }

    /// <summary>
    /// ListEquations (tool #9 list_equations) — a READ-ONLY report of every EQUATION and GLOBAL VARIABLE on a PART.
    /// "list the equations", "what globals does this part have", "show the variables", "what drives this part". Completes
    /// the equation family (add / edit / delete / LIST). It writes NOTHING — pure inspection, so it never rebuilds or
    /// changes the model.
    ///
    /// Reads via the IEquationMgr path (GetCount / get_Equation / get_Value / get_GlobalVariable) — the quoted left-hand
    /// side is the name, get_Value the current numeric value, get_GlobalVariable whether it's a standalone global (vs an
    /// equation driving a named dimension). Honest on an empty part ("no equations / global variables").
    /// </summary>
    public static class ListEquations
    {
        public static bool IsListEquationsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool readVerb = Regex.IsMatch(c, @"\b(list|show|what|which|see|display|tell me)\b");
            bool eqWord = Regex.IsMatch(c, @"\b(equation|equations|global|globals|global variable|global variables|variable|variables|var|vars)\b");
            // "what drives this part" is a natural way to ask for the parametric drivers
            bool drivers = Regex.IsMatch(c, @"\b(driv(e|es|ers|ing)|parametric)\b");
            return (readVerb && eqWord) || (drivers && eqWord);
        }

        public static async Task<ListEquationsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ListEquationsResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Listing equations works on a single part — open the .SLDPRT."; return res; }

            var eqMgr = model.GetEquationMgr();
            if (eqMgr == null) { res.Error = "This part has no equation manager."; return res; }

            await emit("Reader", "reading equations and global variables", "run", null);
            int count = 0; try { count = eqMgr.GetCount(); } catch { }
            for (int i = 0; i < count; i++)
            {
                string raw = null; try { raw = eqMgr.get_Equation(i); } catch { }
                string nm = LhsName(raw);
                if (string.IsNullOrEmpty(nm)) continue;
                double val = double.NaN; try { val = eqMgr.get_Value(i); } catch { }
                bool glob = false; try { glob = eqMgr.get_GlobalVariable(i); } catch { }
                res.Equations.Add(new EquationInfo { Name = nm, Value = val, IsGlobal = glob, Raw = raw });
                if (glob) res.GlobalCount++;
            }
            res.Count = res.Equations.Count;

            res.Info = BuildInfo(res);
            await emit("Reader", null, "done", res.Count + " equation" + (res.Count == 1 ? "" : "s") + " (" + res.GlobalCount + " global" + (res.GlobalCount == 1 ? "" : "s") + ")");
            return res;
        }

        private static string BuildInfo(ListEquationsResult r)
        {
            if (r.Count == 0) return "This part has no equations or global variables.";
            var sb = new StringBuilder();
            sb.Append(r.Count + " equation" + (r.Count == 1 ? "" : "s"));
            if (r.GlobalCount > 0) sb.Append(" (" + r.GlobalCount + " global variable" + (r.GlobalCount == 1 ? "" : "s") + ")");
            sb.Append(": ");
            var parts = new List<string>();
            foreach (var e in r.Equations)
            {
                parts.Add(e.Name + " = " + (double.IsNaN(e.Value) ? "?" : e.Value.ToString("0.###", CultureInfo.InvariantCulture)) + (e.IsGlobal ? " (global)" : ""));
                if (parts.Count >= 12) { parts.Add("…"); break; }
            }
            sb.Append(string.Join(", ", parts));
            sb.Append(". (Read-only — nothing changed.)");
            return sb.ToString();
        }

        private static string LhsName(string eq)
        {
            if (string.IsNullOrEmpty(eq)) return null;
            var m = Regex.Match(eq, "^\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
    }
}
