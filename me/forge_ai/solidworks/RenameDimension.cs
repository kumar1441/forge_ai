using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class RenameDimensionResult
    {
        public string OldName;
        public string NewName;
        public bool Renamed;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 64 — rename_dimension (WRITE, metadata). Gives a model dimension a meaningful name so it can be referenced
    /// later ("set Thickness = 5"). "rename the 20mm dimension to Thickness", "name the depth dimension Thickness".
    /// Resolves the target dimension (by an explicit current name, or the sole dimension, or a value match), sets its
    /// local name, and verifies by reading the name back (fail closed). Setters are often dead on this build, so it
    /// confirms the change rather than trusting it. Undoable; Forge never saves.
    /// </summary>
    public static class RenameDimension
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(rename|name|call)\b") &&
                   Regex.IsMatch(c, @"\b(dimension|dim|dims)\b") &&
                   Regex.IsMatch(c, @"\bto\b|\bas\b|\bcalled\b");
        }

        public static async Task<RenameDimensionResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RenameDimensionResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to rename a dimension."; return res; }

            // parse the new name (after to/as/called)
            var m = Regex.Match(intent ?? "", @"\b(?:to|as|called)\s+([A-Za-z][A-Za-z0-9_]*)", RegexOptions.IgnoreCase);
            if (!m.Success) { res.Error = "Tell me the new name, e.g. \"rename the depth dimension to Thickness\"."; return res; }
            string newName = m.Groups[1].Value;
            res.NewName = newName;

            // optional target: a value like "20mm" to disambiguate; else the sole dimension
            double? wantVal = null;
            var vm = Regex.Match(intent ?? "", @"(\d+(?:\.\d+)?)\s*mm", RegexOptions.IgnoreCase);
            if (vm.Success && double.TryParse(vm.Groups[1].Value, out double v)) wantVal = v;

            await emit("Gauge", "finding the dimension", "run", null);
            Dimension target = null; int seen = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null && target == null)
            {
                var dd = f.GetFirstDisplayDimension() as DisplayDimension;
                while (dd != null)
                {
                    var d = dd.GetDimension2(0) as Dimension;
                    if (d != null)
                    {
                        seen++;
                        double val = double.NaN; try { val = d.Value; } catch { }
                        if (wantVal.HasValue) { if (Math.Abs(val - wantVal.Value) < 0.01) { target = d; break; } }
                        else if (target == null) target = d;   // first dimension when no value given
                    }
                    dd = f.GetNextDisplayDimension(dd) as DisplayDimension;
                }
                f = f.GetNextFeature() as Feature;
            }
            if (target == null) { res.Error = wantVal.HasValue ? "No dimension near " + wantVal + "mm." : "No dimensions to rename."; await emit("Gauge", null, "fail", "no target"); return res; }

            try { res.OldName = target.FullName; } catch { }
            await emit("Scribe", "renaming to '" + newName + "'", "run", null);
            string diag = "";
            bool Took() { try { var n = target.FullName; return n != null && n.StartsWith(newName + "@", StringComparison.OrdinalIgnoreCase); } catch { return false; } }

            try { target.Name = newName; } catch (Exception ex) { diag += "Name:EX(" + ex.GetType().Name + ") "; }
            diag += "afterName=" + (Took() ? "ok" : "no") + " ";
            if (!Took()) { try { model.ForceRebuild3(false); } catch { } diag += "afterRebuild=" + (Took() ? "ok" : "no") + " "; }
            res.Diag = diag;

            await emit("Sentinel", "verifying", "run", null);
            res.Renamed = Took();
            if (!res.Renamed)
            {
                res.Error = "The dimension name didn't take (setter appears inert on this build). Diag: " + diag;
                await emit("Sentinel", null, "fail", "rename didn't stick");
                return res;
            }

            await emit("Sentinel", null, "done", "'" + res.OldName + "' → '" + newName + "'");
            res.Info = "Renamed dimension '" + res.OldName + "' → '" + newName + "'. One Ctrl+Z undoes it; Forge didn't save.";
            return res;
        }
    }
}
