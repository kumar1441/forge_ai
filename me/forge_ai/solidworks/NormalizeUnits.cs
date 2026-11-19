using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class UnitOffender
    {
        public string Component;   // component instance name
        public string Units;       // its part-document linear units (friendly)
    }

    public class NormalizeUnitsResult
    {
        public bool IsAssembly;
        public int Components;         // unique referenced parts inspected
        public string AssemblyUnits;   // the assembly document's own linear units
        public string DominantUnits;   // the most common component unit system
        public int DistinctUnits;      // how many different unit systems appear across the components
        public int MismatchCount;      // components NOT on the dominant unit system
        public UnitOffender[] Offenders;
        public string Verdict;         // "mixed" | "consistent"
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 244 — normalize_units (READ / detect-and-report). An inch part dropped into a metric assembly (or vice
    /// versa) is a silent scale trap: mates hold, but a driven dimension or a downstream export reads the wrong number.
    /// This walks the assembly's components, reads each referenced part's DOCUMENT linear units (swUnitsLinear), finds
    /// the dominant system, and flags every component off it. Report only — a unit conversion is offered, never done
    /// unasked (Rule #5). The INDEPENDENT GT re-reads the same per-component units by opening each unique part path, so
    /// a disagreement exposes a bad traversal; known truth anchors it (mixed-units-assembly = 2 parts, 1 inch + 1 mm ->
    /// 1 mismatch). Assembly-scoped (mixed units is a cross-component condition).
    /// </summary>
    public static class NormalizeUnits
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool unitNoun = Regex.IsMatch(c, @"\b(unit|units|inch|inches|imperial|metric|mm|millimet)\b");
            if (!unitNoun) return false;
            // a MIXED / consistency / normalize framing — NOT a plain "what units is this" (GetDocumentUnits) or
            // "set the units to X" (SetDocumentUnits). Requires a mix/consistency/normalize word.
            if (Regex.IsMatch(c, @"\b(mixed|mix|consistent|consisten|inconsisten|normali[sz]e|same units|different units|unit mismatch|mismatched|imperial\s+and\s+metric|metric\s+and\s+imperial)\b")) return true;
            // "are any parts in inches / is anything not in mm" — a mismatch hunt phrased as a question
            if (Regex.IsMatch(c, @"\b(any|anything|which|are there)\b") && Regex.IsMatch(c, @"\b(inch|inches|imperial|metric|mm)\b")) return true;
            return false;
        }

        public static async Task<NormalizeUnitsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new NormalizeUnitsResult();
            if (model == null) { res.Error = "Open the assembly to check for mixed units."; return res; }
            if ((int)model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            { res.Error = "Mixed-unit detection is an assembly check - open the .SLDASM."; return res; }
            res.IsAssembly = true;
            res.AssemblyUnits = Friendly(ReadUnits(model));

            await emit("Surveyor", "reading each component's units", "run", null);

            // one reading per UNIQUE referenced part (don't double-count identical instances)
            var perPart = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // path -> unit enum
            var firstInstanceName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var asm = model as AssemblyDoc;
            var comps = asm.GetComponents(true) as object[];
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                try { if (c.IsSuppressed()) continue; } catch { }
                string path = null; try { path = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path)) continue;
                if (perPart.ContainsKey(path)) continue;
                var md = c.GetModelDoc2() as IModelDoc2; if (md == null) continue;
                int u = ReadUnits(md);
                perPart[path] = u;
                string nm = null; try { nm = c.Name2; } catch { }
                firstInstanceName[path] = nm ?? System.IO.Path.GetFileNameWithoutExtension(path);
            }

            res.Components = perPart.Count;
            if (perPart.Count == 0) { res.Error = "No resolved components to read units from."; return res; }

            var byUnit = perPart.Values.GroupBy(u => u).ToDictionary(g => g.Key, g => g.Count());
            res.DistinctUnits = byUnit.Count;
            int dominant = byUnit.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
            res.DominantUnits = Friendly(dominant);

            var offenders = new List<UnitOffender>();
            foreach (var kv in perPart)
                if (kv.Value != dominant)
                    offenders.Add(new UnitOffender { Component = firstInstanceName[kv.Key], Units = Friendly(kv.Value) });

            res.Offenders = offenders.ToArray();
            res.MismatchCount = offenders.Count;
            res.Verdict = res.DistinctUnits > 1 ? "mixed" : "consistent";

            var diag = new StringBuilder("verdict=" + res.Verdict + " comps=" + res.Components + " distinct=" + res.DistinctUnits + " dominant=" + res.DominantUnits + " mismatch=" + res.MismatchCount);
            foreach (var of in offenders) diag.Append(" | " + of.Component + " is " + of.Units);
            res.Diag = diag.ToString();

            await emit("Surveyor", null, "done",
                res.MismatchCount == 0
                    ? ("all " + res.Components + " components share " + res.DominantUnits + " units")
                    : (res.MismatchCount + " component" + (res.MismatchCount == 1 ? "" : "s") + " off the dominant " + res.DominantUnits + ": " +
                       string.Join(", ", offenders.Select(of => of.Component + " (" + of.Units + ")"))));

            res.Info = BuildInfo(res, offenders);
            return res;
        }

        private static int ReadUnits(IModelDoc2 md)
        {
            try { return md.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); }
            catch { return -999; }
        }

        // friendly label for swLengthUnit_e (report only — verdict keys off the raw enum, not this string)
        private static string Friendly(int u)
        {
            switch (u)
            {
                case (int)swLengthUnit_e.swMM: return "mm";
                case (int)swLengthUnit_e.swCM: return "cm";
                case (int)swLengthUnit_e.swMETER: return "m";
                case (int)swLengthUnit_e.swINCHES: return "inch";
                case (int)swLengthUnit_e.swFEET: return "ft";
                case (int)swLengthUnit_e.swFEETINCHES: return "ft-in";
                case (int)swLengthUnit_e.swMIL: return "mil";
                case (int)swLengthUnit_e.swMICRON: return "micron";
                case (int)swLengthUnit_e.swNANOMETER: return "nm";
                case (int)swLengthUnit_e.swANGSTROM: return "angstrom";
                default: return "unit#" + u;
            }
        }

        private static string BuildInfo(NormalizeUnitsResult r, List<UnitOffender> offenders)
        {
            if (r.MismatchCount == 0)
                return "Units are consistent - all " + r.Components + " components use " + r.DominantUnits + ". Nothing to normalize.";
            var sb = new StringBuilder();
            sb.Append(r.MismatchCount + " component" + (r.MismatchCount == 1 ? " is" : "s are") + " on different units than the dominant " + r.DominantUnits + " (a silent scale trap):");
            foreach (var of in offenders)
                sb.Append("\n  " + of.Component + " is in " + of.Units);
            sb.Append("\nWant these normalized to " + r.DominantUnits + "? (units only change the display, not the geometry.)");
            return sb.ToString();
        }
    }
}
