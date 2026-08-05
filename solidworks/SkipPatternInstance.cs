using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SkipPatternInstanceResult
    {
        public string PatternName;
        public int TotalInstances = -1;
        public int Requested;
        public int SkippedBefore = -1;
        public int SkippedAfter = -1;
        public bool AlreadySkipped;   // idempotent: that instance is already skipped
        public bool NeedsConfirm;
        public string Question;
        public bool Verified;         // fail closed: independent re-read shows exactly one more skipped instance
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// SkipPatternInstance (tool #52 skip_pattern_instance, WRITE) — suppress ONE instance of a linear pattern without
    /// touching the others, the real "leave a gap here for the handle" edit. "skip instance 2 of the pattern",
    /// "drop the 3rd hole in the pattern".
    ///
    /// API (proven-live family): IFeature.GetDefinition() → ILinearPatternFeatureData → SkippedItemArray (get/set the
    /// 1-based instance IDs to omit; instance 1 is the seed and cannot be skipped) → IFeature.ModifyDefinition. Same
    /// AccessSelections/ModifyDefinition route that edit_pattern_count (tool 49) and edit_pattern_spacing (48) already
    /// use successfully on this build.
    ///
    /// Named crew:
    ///   Gauge — find the linear pattern, read its total instance count and the CURRENT skipped set; validate the asked
    ///           instance is a real, skippable copy (2..N). Already skipped → idempotent no-op.
    ///   Scribe — union the requested instance into SkippedItemArray, ModifyDefinition, one ForceRebuild3.
    ///   Sentinel — FAIL CLOSED: independently re-read the skipped count; verified only if it rose by exactly one and now
    ///           contains the requested instance. The harness cross-checks with GEOMETRY (one fewer cylindrical bore).
    ///
    /// IDEMPOTENT (Rule #5); UNDO is sacred (Rule #7) — one Ctrl+Z restores the instance; Forge never saves.
    /// </summary>
    public static class SkipPatternInstance
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // "skip" is the distinguishing verb no other pattern tool claims; pair it with a pattern/instance noun so it
            // can't grab a generic "skip" elsewhere. edit_pattern_count needs a change verb (change/set/make/to) — "skip"
            // is not one, so the two never collide.
            if (!Regex.IsMatch(c, @"\bskip\b")) return false;
            return Regex.IsMatch(c, @"\b(pattern|instance|instances|copy|copies|hole)\b");
        }

        public static async Task<SkipPatternInstanceResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SkipPatternInstanceResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Skipping a pattern instance works on a single part — open the .SLDPRT."; return res; }

            await emit("Gauge", "finding the pattern and reading its skipped set", "run", null);

            // ---- which instance? first bare number in the intent ----
            var nm = Regex.Match(intent ?? "", @"\b(\d+)\b");
            if (!nm.Success || !int.TryParse(nm.Groups[1].Value, out int want) || want < 1)
            { res.Error = "Tell me which instance to skip, e.g. \"skip instance 2 of the pattern\"."; await emit("Gauge", null, "fail", "no instance number"); return res; }
            res.Requested = want;

            // ---- find the linear pattern (same tree walk as edit_pattern_count) ----
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
            try { res.TotalInstances = def.D1TotalInstances; } catch { }

            var existing = ReadSkipped(def);
            res.SkippedBefore = existing.Count;

            // instance 1 is the seed (never skippable); the top of the range is the total count
            if (res.TotalInstances >= 1 && (want < 2 || want > res.TotalInstances))
            {
                res.NeedsConfirm = true;
                res.Question = "The pattern has " + res.TotalInstances + " instances — skippable copies are 2.." + res.TotalInstances +
                               " (instance 1 is the seed). Which instance?";
                await emit("Gauge", null, "fail", "instance " + want + " out of range");
                return res;
            }

            // ---- IDEMPOTENT (Rule #5) ----
            if (existing.Contains(want))
            {
                res.AlreadySkipped = true; res.Verified = true; res.SkippedAfter = res.SkippedBefore;
                res.Info = "Instance " + want + " is already skipped — nothing to do.";
                await emit("Scribe", null, "done", "already skipped — no change");
                return res;
            }

            await emit("Gauge", null, "done", res.TotalInstances + " instances · " + res.SkippedBefore + " already skipped");
            await emit("Scribe", "skipping instance " + want, "run", null);

            var target = new SortedSet<int>(existing) { want };
            string diag = "";
            try { def.AccessSelections(model, null); } catch (Exception ex) { diag += "access:EX(" + ex.GetType().Name + ") "; }

            // THROW SEVERAL WAYS (instrument-first): the SkippedItemArray setter is a COM SAFEARRAY sink; a boxed object[]
            // can marshal as SAFEARRAY(VARIANT) and be silently ignored while a real int[] marshals as SAFEARRAY(I4).
            // Set, then READ BACK on the SAME def BEFORE ModifyDefinition to see which array type actually sticks.
            int[] intArr = target.ToArray();
            object[] objArr = target.Select(x => (object)x).ToArray();
            try { def.SkippedItemArray = intArr; } catch (Exception ex) { diag += "setI4:EX(" + ex.GetType().Name + ") "; }
            int rbI4 = SafeSkipCount(def); diag += "afterI4=" + rbI4 + " ";
            if (rbI4 == 0)
            {
                try { def.SkippedItemArray = objArr; } catch (Exception ex) { diag += "setVar:EX(" + ex.GetType().Name + ") "; }
                diag += "afterVar=" + SafeSkipCount(def) + " ";
            }

            bool applied = false;
            try { applied = pat.ModifyDefinition(def, model, null); } catch (Exception ex) { diag += "modify:EX(" + ex.GetType().Name + ") "; }
            diag += "applied=" + applied + " ";
            try { model.ForceRebuild3(false); } catch { }
            res.Diag = diag;

            // ---- Sentinel: FAIL CLOSED — independent re-read ----
            await emit("Sentinel", "verifying the skip took", "run", null);
            var def2 = FindPatternDef(model);
            var after = def2 != null ? ReadSkipped(def2) : new HashSet<int>();
            res.SkippedAfter = after.Count;
            res.Verified = after.Contains(want) && after.Count == res.SkippedBefore + 1;
            if (!res.Verified)
            {
                res.Error = "The skip didn't take — skipped count " + res.SkippedBefore + " → " + res.SkippedAfter +
                            (after.Contains(want) ? "" : ", instance " + want + " not in the skipped set") + ". Diag: " + diag;
                await emit("Sentinel", null, "fail", "skip didn't take");
                return res;
            }

            await emit("Sentinel", null, "done", "instance " + want + " skipped (" + res.SkippedBefore + " → " + res.SkippedAfter + " skipped)");
            res.Info = "Skipped instance " + want + " of the pattern (" + res.SkippedBefore + " → " + res.SkippedAfter + " skipped). One Ctrl+Z restores it; Forge didn't save.";
            return res;
        }

        private static int SafeSkipCount(ILinearPatternFeatureData def) { try { return def.GetSkippedItemCount(); } catch { return -1; } }

        private static HashSet<int> ReadSkipped(ILinearPatternFeatureData def)
        {
            var set = new HashSet<int>();
            object arr = null; try { arr = def.SkippedItemArray; } catch { }
            if (arr is System.Array a) { foreach (var v in a) { try { set.Add(Convert.ToInt32(v)); } catch { } } }
            return set;
        }

        private static ILinearPatternFeatureData FindPatternDef(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (tn != null && tn.IndexOf("LPattern", StringComparison.OrdinalIgnoreCase) >= 0)
                    return f.GetDefinition() as ILinearPatternFeatureData;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }
    }
}
