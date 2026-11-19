using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class EditPatternCountResult
    {
        public string PatternName;
        public int InstancesBefore = -1;
        public int InstancesAfter = -1;
        public int Requested;
        public bool AlreadyAtCount;
        public bool Verified;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 49 — edit_pattern_count (WRITE). Changes a linear pattern's instance count. "change the pattern to 5",
    /// "make it 6 instances". Reads the ILinearPatternFeatureData, sets D1TotalInstances, applies via
    /// IFeature.ModifyDefinition, and verifies by an INDEPENDENT re-read (fail closed). Idempotent; undoable; never saves.
    /// </summary>
    public static class EditPatternCount
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\bpattern\b") &&
                   Regex.IsMatch(c, @"\b(change|set|make|edit|update|to)\b") &&
                   Regex.IsMatch(c, @"\b(\d+)\b") &&
                   Regex.IsMatch(c, @"\b(instance|instances|count|copies|to)\b");
        }

        public static async Task<EditPatternCountResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new EditPatternCountResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to edit its pattern."; return res; }

            var nm = Regex.Match(intent ?? "", @"\b(\d+)\b");
            if (!nm.Success || !int.TryParse(nm.Groups[1].Value, out int want) || want < 1)
            { res.Error = "Tell me the new instance count, e.g. \"change the pattern to 5\"."; return res; }
            res.Requested = want;

            await emit("Gauge", "finding the pattern", "run", null);
            Feature pat = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (tn != null && tn.IndexOf("LPattern", StringComparison.OrdinalIgnoreCase) >= 0) { pat = f; break; }
                f = f.GetNextFeature() as Feature;
            }
            if (pat == null) { res.Error = "No linear pattern on this part to edit."; await emit("Gauge", null, "fail", "no pattern"); return res; }
            try { res.PatternName = pat.Name; } catch { }

            var def = pat.GetDefinition() as ILinearPatternFeatureData;
            if (def == null) { res.Error = "Couldn't read the pattern definition."; return res; }
            try { res.InstancesBefore = def.D1TotalInstances; } catch { }
            if (res.InstancesBefore == want)
            {
                res.AlreadyAtCount = true; res.Verified = true; res.InstancesAfter = want;
                res.Info = "The pattern is already " + want + " instances — nothing to do.";
                await emit("Sentinel", null, "done", "already " + want);
                return res;
            }

            await emit("Scribe", "setting the pattern to " + want, "run", null);
            string diag = "";
            try { def.AccessSelections(model, null); } catch { }
            try { def.D1TotalInstances = want; } catch (Exception ex) { diag += "set:EX(" + ex.GetType().Name + ") "; }
            bool applied = false;
            try { applied = pat.ModifyDefinition(def, model, null); } catch (Exception ex) { diag += "modify:EX(" + ex.GetType().Name + ") "; }
            diag += "applied=" + applied + " ";
            try { model.ForceRebuild3(false); } catch { }
            res.Diag = diag;

            // ---- Sentinel: independent re-read (fail closed) ----
            await emit("Sentinel", "verifying", "run", null);
            int after = -1;
            var f2 = model.FirstFeature() as Feature;
            while (f2 != null)
            {
                string tn = null; try { tn = f2.GetTypeName2(); } catch { }
                if (tn != null && tn.IndexOf("LPattern", StringComparison.OrdinalIgnoreCase) >= 0)
                { var d2 = f2.GetDefinition() as ILinearPatternFeatureData; if (d2 != null) { try { after = d2.D1TotalInstances; } catch { } } break; }
                f2 = f2.GetNextFeature() as Feature;
            }
            res.InstancesAfter = after;
            res.Verified = after == want;
            if (!res.Verified)
            {
                res.Error = "The pattern count didn't change to " + want + " (" + res.InstancesBefore + " → " + after + "). Diag: " + diag;
                await emit("Sentinel", null, "fail", "count didn't take");
                return res;
            }

            await emit("Sentinel", null, "done", "pattern now " + want + " instances (" + res.InstancesBefore + " → " + after + ")");
            res.Info = "Changed the pattern to " + want + " instances (" + res.InstancesBefore + " → " + after + "). One Ctrl+Z undoes it; Forge didn't save.";
            return res;
        }
    }
}
