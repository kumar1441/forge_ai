using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DrivingDimRow
    {
        public string FullName;          // "Length@Sketch1" — the handle you can actually command by
        public string Name;              // "Length"
        public string Feature;
        public double ValueMm;
        public bool Driving;             // false = a driven / reference dimension: it REPORTS, it does not control
        public bool ReadOnly;
        public bool UserNamed;           // a real name, not SolidWorks' auto D1 / RD2 / DIA3
        public string EquationLhs;       // the equation that controls this dimension, if any
    }

    public class GetDrivingDimensionsResult
    {
        public int Total;
        public int DrivingCount;
        public int DrivenCount;
        public int UserNamedDrivingCount;
        public int EquationDrivenCount;
        public List<DrivingDimRow> Rows = new List<DrivingDimRow>();
        public List<string> Equations = new List<string>();
        public List<string> Unreadable = new List<string>();
        public bool HasNamedParametricControl;   // the verdict requirement #7 actually asks for
        public string Info;
        public string Error;
    }

    /// <summary>
    /// GetDrivingDimensions (tool #259 get_driving_dimensions) — which dimensions actually CONTROL this part, what
    /// they are called, and which of those names a human (or Forge) can command by. A part with 60 dimensions all
    /// called D1..D60 is not parametric in any useful sense; a part with "Length", "BoreDia" and a couple of globals
    /// is. This is the read that decides which of the two you are holding, and the pre-flight every parametric edit
    /// (set_dimension, variant loops) needs before it can name a target.
    ///
    ///   Reader   — walks the feature tree's display dimensions and records, per dimension: driving vs DRIVEN (a
    ///              reference dimension reports a value, it does not control anything — counting it as parametric
    ///              control is the easy lie here), read-only, and whether the name is a real name or SolidWorks'
    ///              auto D1/RD2/DIA3.
    ///   Reader   — then reads the equation manager, so a dimension driven by a global is attributed to it. Equation
    ///              READS are live on this build; equation WRITES are dead (docs/SOLIDWORKS-GOTCHAS.md) and none are attempted.
    ///   Sentinel — the verdict is deliberately narrow: named parametric control means at least one DRIVING dimension
    ///              carries a user name, or at least one equation/global exists. Anything else is reported as what it
    ///              is, with what would make it work — never dressed up.
    ///
    /// READ-ONLY: nothing is selected, edited, rebuilt or saved.
    /// </summary>
    public static class GetDrivingDimensions
    {
        // SolidWorks' auto-generated dimension names on this build: D1, D2 ... plus the radial/diameter variants.
        private static readonly Regex AutoName = new Regex(@"^(d|rd|dia|da|ld|ad)\d+$", RegexOptions.IgnoreCase);

        // NARROW: needs a DIMENSION noun and a PARAMETRIC-CONTROL word. Placed before GetDimensions (tool 26), whose
        // matcher is the broad "list the dimensions" roster — "show the driving dimensions" must not land there.
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(set|change|make|resize|rename|delete|remove|add|create)\b")) return false;
            bool dimNoun = Regex.IsMatch(c, @"\b(dimension|dimensions|dims?|parameter|parameters|global|globals)\b");
            bool control = Regex.IsMatch(c, @"\b(driv(e|es|ing|en)|parametric|named|control|controls|controlling)\b");
            return dimNoun && control;
        }

        public static async Task<GetDrivingDimensionsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetDrivingDimensionsResult();
            if (model == null) { res.Error = "Open a part to read its driving dimensions."; return res; }

            await emit("Reader", "reading every dimension and whether it drives or just reports", "run", null);

            // equations first, so each dimension can be attributed to the global that controls it
            var eqByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var eq = model.GetEquationMgr();
                if (eq != null)
                {
                    int n = 0; try { n = eq.GetCount(); } catch { }
                    for (int k = 0; k < n; k++)
                    {
                        string s = null; try { s = eq.Equation[k]; } catch { }
                        if (string.IsNullOrEmpty(s)) continue;
                        res.Equations.Add(s);
                        var m = Regex.Match(s, "^\\s*\"([^\"]+)\"");   // the LHS between the first pair of quotes
                        if (m.Success) eqByTarget[m.Groups[1].Value] = s;
                    }
                }
            }
            catch { }

            var feat = model.FirstFeature() as Feature;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (feat != null)
            {
                string fname = null; try { fname = feat.Name; } catch { }
                DisplayDimension dd = null;
                try { dd = feat.GetFirstDisplayDimension() as DisplayDimension; } catch { }
                while (dd != null)
                {
                    Dimension d = null; try { d = dd.GetDimension2(0) as Dimension; } catch { }
                    if (d != null)
                    {
                        var row = new DrivingDimRow { Feature = fname };
                        try { row.FullName = d.FullName; } catch { }
                        try { row.Name = d.Name; } catch { }
                        if (string.IsNullOrEmpty(row.FullName)) row.FullName = row.Name;
                        if (string.IsNullOrEmpty(row.FullName)) { res.Unreadable.Add((fname ?? "?") + " / an unnamed dimension"); goto next; }
                        if (!seen.Add(row.FullName)) goto next;   // the same dimension can surface under more than one feature

                        row.ValueMm = double.NaN;
                        try { var sv = d.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null) as double[]; if (sv != null && sv.Length > 0) row.ValueMm = sv[0] * 1000.0; }
                        catch { }
                        if (double.IsNaN(row.ValueMm)) { try { row.ValueMm = d.Value; } catch { } }

                        int ds = 0; try { ds = d.DrivenState; } catch { }
                        row.Driving = ds != (int)swDimensionDrivenState_e.swDimensionDriven;
                        try { row.ReadOnly = d.ReadOnly; } catch { }

                        string bare = row.Name ?? "";
                        row.UserNamed = bare.Length > 0 && !AutoName.IsMatch(bare);

                        string eqs;
                        if (eqByTarget.TryGetValue(row.FullName, out eqs)) row.EquationLhs = eqs;

                        res.Rows.Add(row);
                    }
                next:
                    try { dd = feat.GetNextDisplayDimension(dd) as DisplayDimension; } catch { dd = null; }
                }
                feat = feat.GetNextFeature() as Feature;
            }

            res.Total = res.Rows.Count;
            foreach (var r in res.Rows)
            {
                if (r.Driving) res.DrivingCount++; else res.DrivenCount++;
                if (r.Driving && r.UserNamed) res.UserNamedDrivingCount++;
                if (r.EquationLhs != null) res.EquationDrivenCount++;
            }
            res.HasNamedParametricControl = res.UserNamedDrivingCount > 0 || res.Equations.Count > 0;

            await emit("Sentinel", "deciding whether this part has NAMED parametric control", "run", null);

            if (res.Total == 0)
            {
                res.Info = "No dimensions at all — this looks like an imported dumb solid, not a parametric part. " +
                           "There is nothing here to drive; the geometry would have to be re-modelled with features first.";
                await emit("Sentinel", null, "done", res.Info);
                return res;
            }

            var named = new List<string>();
            foreach (var r in res.Rows) if (r.Driving && r.UserNamed) named.Add(r.FullName + " = " + Math.Round(r.ValueMm, 3) + "mm");

            res.Info = res.Total + " dimensions: " + res.DrivingCount + " driving, " + res.DrivenCount + " driven (reference only). ";
            if (res.UserNamedDrivingCount > 0)
                res.Info += res.UserNamedDrivingCount + " driving dimension" + (res.UserNamedDrivingCount == 1 ? " carries" : "s carry") +
                            " a real name you can command by: " + string.Join(", ", named.ToArray()) + ". ";
            else
                res.Info += "None of the driving dimensions carries a real name — they are all SolidWorks' auto D1/D2 handles, " +
                            "so nothing here can be addressed by meaning. Renaming the two or three that matter (rename_dimension) " +
                            "is what would make this part drivable. ";
            if (res.Equations.Count > 0)
                res.Info += res.Equations.Count + " equation/global" + (res.Equations.Count == 1 ? "" : "s") + " present" +
                            (res.EquationDrivenCount > 0 ? ", driving " + res.EquationDrivenCount + " of the dimensions" : "") + ". ";
            if (res.Unreadable.Count > 0)
                res.Info += res.Unreadable.Count + " dimension(s) could not be read: " + string.Join(", ", res.Unreadable.ToArray()) + ". ";

            await emit("Sentinel", null, "done", res.HasNamedParametricControl
                ? "named parametric control: YES"
                : "named parametric control: NO — auto-named dimensions only");
            return res;
        }
    }
}
