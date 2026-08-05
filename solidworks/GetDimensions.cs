using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DimRow { public string Name; public double ValueMm; public string Feature; }

    public class GetDimensionsResult
    {
        public int Count;
        public List<DimRow> Rows = new List<DimRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 26 — get_dimension_value / list dimensions (READ). Every model dimension of a PART with its name + value
    /// (mm), read by walking the feature tree's display dimensions. The read counterpart to set_dimension — answers
    /// "what's the thickness", "list the dimensions", "how wide is this". Read-only; own value read via GetSystemValue3.
    /// </summary>
    public static class GetDimensions
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // read verbs only — set_dimension owns the write verbs (set/change/make ... = value)
            bool readVerb = System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(list|show|what|what's|how (wide|long|thick|big|tall)|read|get|tell me)\b");
            bool dimNoun = System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(dimension|dimensions|dims?|thickness|width|length|height|size)\b");
            bool writeVerb = System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(set|change|make|resize|to)\b\s*\d") ||
                             System.Text.RegularExpressions.Regex.IsMatch(c, @"=\s*\d");
            return readVerb && dimNoun && !writeVerb;
        }

        public static async Task<GetDimensionsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetDimensionsResult();
            if (model == null) { res.Error = "Open a part to read its dimensions."; return res; }

            await emit("Reader", "reading model dimensions", "run", null);
            var feat = model.FirstFeature() as Feature;
            while (feat != null)
            {
                string fname = null; try { fname = feat.Name; } catch { }
                var dd = feat.GetFirstDisplayDimension() as DisplayDimension;
                while (dd != null)
                {
                    Dimension d = null; try { d = dd.GetDimension2(0) as Dimension; } catch { }
                    if (d != null)
                    {
                        string dn = null; try { dn = d.FullName; } catch { }
                        if (string.IsNullOrEmpty(dn)) { try { dn = d.Name; } catch { } }
                        double val = double.NaN;
                        try { var sv = d.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null) as double[]; if (sv != null && sv.Length > 0) val = sv[0]; }
                        catch { }
                        if (double.IsNaN(val)) { try { val = d.SystemValue; } catch { } }
                        res.Rows.Add(new DimRow { Name = dn ?? "?", ValueMm = val * 1000.0, Feature = fname });
                        res.Count++;
                    }
                    dd = feat.GetNextDisplayDimension(dd) as DisplayDimension;
                }
                feat = feat.GetNextFeature() as Feature;
            }

            await emit("Reader", null, "done", res.Count + " dimension" + (res.Count == 1 ? "" : "s"));
            if (res.Count == 0) { res.Info = "No model dimensions found on this part."; return res; }

            var sb = new StringBuilder(res.Count + " dimension" + (res.Count == 1 ? "" : "s") + ":");
            int shown = 0;
            foreach (var r in res.Rows)
            {
                if (shown++ >= 24) { sb.Append("\n… (" + (res.Count - 24) + " more)"); break; }
                sb.Append("\n• " + r.Name + " = " + r.ValueMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm");
            }
            res.Info = sb.ToString();
            return res;
        }
    }
}
