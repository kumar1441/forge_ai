using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ConfigSpecificDimensionResult
    {
        public string TargetDim;               // resolved dim FullName
        public string ConfigName;
        public double RequestedMm = -1;
        public double BeforeInConfigMm = -1;
        public double AfterInConfigMm = -1;
        public bool OtherConfigsUnchanged;     // the per-config scoping proof
        public bool AlreadyDone;
        public bool NeedsConfirm;
        public string Question;
        public int MatchedDims;
        public bool Verified;
        public int RebuildErrors;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// ConfigSpecificDimension (tool #90 set_config_specific_dimension, WRITE) — change a dimension's value in ONE named
    /// configuration, leaving the value in every other config alone. The variant primitive: "set the depth to 30 in the
    /// Variant-1 configuration", "make D1 40mm in the Large config only". DISTINCT from set_dimension (tool 63), which
    /// writes the ACTIVE config (SystemValue).
    ///
    /// API (proven-live, config-scoped): IDimension.SetSystemValue3(valueMeters, swSpecifyConfiguration, string[] names)
    /// writes only the named configs; IDimension.GetSystemValue2(configName) reads a config's value WITHOUT switching the
    /// active configuration. Pass a real string[] (the SAFEARRAY lesson — a boxed object[] is silently ignored).
    ///
    /// FAIL CLOSED (Rule #6): verify the target config's fresh read-back equals the request AND every OTHER config's value
    /// is unchanged (scoping actually scoped). IDEMPOTENT (Rule #5); UNDO is sacred (Rule #7); Forge never saves.
    /// </summary>
    public static class ConfigSpecificDimension
    {
        private const double EpsMm = 1e-3;

        // verb + a numeric value + an explicit CONFIG word — the config word routes here ahead of the active-config
        // set_dimension (tool 63). Excludes scale / fastener-resize like set_dimension does.
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (ScalePart.IsScaleIntent(c)) return false;
            if (Regex.IsMatch(c, @"\bm\d+\b.*\bm\d+\b")) return false;
            if (!Regex.IsMatch(c, @"\b(change|set|make|adjust|update|modify)\b")) return false;
            if (!Regex.IsMatch(c, @"(?<![a-z0-9])\d+(\.\d+)?")) return false;
            return Regex.IsMatch(c, @"\b(config|configuration|configurations)\b");
        }

        public static async Task<ConfigSpecificDimensionResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ConfigSpecificDimensionResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Per-config dimension edits work on a single part — open the .SLDPRT."; return res; }

            await emit("Gauge", "resolving the config, the dimension and the value", "run", null);

            var configNames = (model.GetConfigurationNames() as string[]) ?? new string[0];
            if (configNames.Length < 2)
            { res.Error = "This part has only one configuration — use set_dimension for the active config."; await emit("Gauge", null, "fail", "single config"); return res; }

            string lc = (intent ?? "").ToLowerInvariant();

            // ---- target config: a real config name that appears in the command ----
            string cfg = configNames.FirstOrDefault(n => lc.Contains(n.ToLowerInvariant()));
            if (cfg == null)
            {
                res.NeedsConfirm = true;
                res.Question = "Which configuration? This part has: " + string.Join(", ", configNames) + ".";
                await emit("Gauge", null, "fail", "config not named — asking");
                return res;
            }
            res.ConfigName = cfg;

            // strip the config phrase so its digits/words never pollute the value/target parse
            string forParse = Regex.Replace(lc, @"\b(in|for|only in|within)?\s*(the\s+)?" + Regex.Escape(cfg.ToLowerInvariant()) + @"\s*(config|configuration|configurations)?\b", " ", RegexOptions.IgnoreCase);

            // ---- value ----
            if (!ParseValue(forParse, out double reqMm) || reqMm <= 0)
            {
                res.NeedsConfirm = true;
                res.Question = "Set that dimension to what value in " + cfg + "? e.g. \"set the depth to 30 in " + cfg + "\".";
                await emit("Gauge", null, "fail", "no target value");
                return res;
            }
            res.RequestedMm = reqMm;

            // ---- resolve the dimension (traversal + token match) ----
            var dims = ReadDims(model);
            if (dims.Count == 0)
            { res.Error = "This part has no editable driving dimensions."; await emit("Gauge", null, "fail", "no dims"); return res; }

            var hits = MatchDims(dims, forParse, reqMm);
            res.MatchedDims = hits.Count;
            if (hits.Count == 0)
            {
                res.NeedsConfirm = true;
                res.Question = "Which dimension? This part has: " + string.Join(", ", dims.Take(6).Select(d => d.Short + " (" + Trim(d.CurMm) + "mm)")) + ".";
                await emit("Gauge", null, "fail", "dim not resolved — asking");
                return res;
            }
            if (hits.Count > 1)
            {
                res.NeedsConfirm = true;
                res.Question = "That matches " + hits.Count + " dimensions — which one? " + string.Join(" · ", hits.Take(6).Select(d => d.Full + " = " + Trim(d.CurMm) + "mm")) + ".";
                await emit("Gauge", null, "fail", hits.Count + " candidate dims");
                return res;
            }
            var dim = hits[0];
            res.TargetDim = dim.Full;

            // ---- snapshot per-config values (for the "others unchanged" proof) ----
            // NOTE (2026-07-24): GetSystemValue2(name)/GetSystemValue3(swAllConfiguration) read 0 for INACTIVE configs of a
            // still-shared dim on this build — unreliable. The honest per-config read is activate-the-config + SystemValue.
            string origActive = ActiveConfigName(model);
            var before = ReadPerConfig(model, dim.Full, configNames, origActive);
            res.BeforeInConfigMm = before[cfg];

            // ---- IDEMPOTENT ----
            if (Math.Abs(before[cfg] - reqMm) <= EpsMm)
            {
                res.AlreadyDone = true; res.Verified = true; res.AfterInConfigMm = before[cfg]; res.OtherConfigsUnchanged = true;
                res.Info = dim.Full + " is already " + Trim(reqMm) + "mm in " + cfg + " — nothing to change.";
                await emit("Scribe", null, "done", "already " + Trim(reqMm) + "mm in " + cfg);
                return res;
            }

            await emit("Gauge", null, "done", "setting " + Short(dim.Full) + " to " + Trim(reqMm) + "mm in " + cfg + " only");
            await emit("Scribe", "writing the config-specific value", "run", null);

            int st = int.MinValue;
            try { st = dim.Dim.SetSystemValue3(reqMm / 1000.0, (int)swInConfigurationOpts_e.swSpecifyConfiguration, new string[] { cfg }); }
            catch (Exception ex) { res.Error = "SetSystemValue3 threw (" + ex.GetType().Name + ") — the part is unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            res.Diag = "ret=" + st + " ";

            // ---- Sentinel: FAIL CLOSED ----
            await emit("Sentinel", "verifying the target config moved and the others didn't", "run", null);
            var after = ReadPerConfig(model, dim.Full, configNames, origActive);
            res.AfterInConfigMm = after[cfg];
            res.OtherConfigsUnchanged = configNames.Where(n => n != cfg).All(n => Math.Abs(before[n] - after[n]) <= EpsMm);
            res.Diag += "before=[" + string.Join(",", configNames.Select(n => n + ":" + Trim(before[n]))) + "] after=[" + string.Join(",", configNames.Select(n => n + ":" + Trim(after[n]))) + "]";

            bool landed = Math.Abs(after[cfg] - reqMm) <= Math.Max(EpsMm, 1e-4 * reqMm);
            res.Verified = landed && res.OtherConfigsUnchanged && res.RebuildErrors == 0;
            if (!res.Verified)
            {
                res.Error = !landed ? "The value didn't take in " + cfg + " (read back " + Trim(after[cfg]) + "mm, wanted " + Trim(reqMm) + "mm). " + res.Diag
                          : !res.OtherConfigsUnchanged ? "The change bled into other configurations — per-config scoping failed. " + res.Diag
                          : "The write left " + res.RebuildErrors + " rebuild error(s). " + res.Diag;
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", Short(dim.Full) + ": " + Trim(res.BeforeInConfigMm) + " → " + Trim(res.AfterInConfigMm) + "mm in " + cfg + " only · others held · clean");
            res.Info = "Set " + dim.Full + " to " + Trim(res.AfterInConfigMm) + "mm in " + cfg + " only (the other config" +
                       (configNames.Length - 1 == 1 ? "" : "s") + " unchanged at " + Trim(before[configNames.First(n => n != cfg)]) + "mm). One Ctrl+Z restores it; Forge didn't save.";
            return res;
        }

        private class DimHit { public Dimension Dim; public string Full; public string Short; public string Feature; public double CurMm; public string[] TypeSyn; }

        // reliable per-config read: activate each config, rebuild, read the RESOLVED active-config value, restore original.
        private static Dictionary<string, double> ReadPerConfig(IModelDoc2 model, string dimFull, string[] configNames, string origActive)
        {
            var map = new Dictionary<string, double>();
            foreach (var n in configNames)
            {
                double mm = -1;
                try
                {
                    model.ShowConfiguration2(n);
                    model.ForceRebuild3(false);
                    var d = model.Parameter(dimFull) as Dimension;
                    if (d != null) mm = d.SystemValue * 1000.0;
                }
                catch { }
                map[n] = mm;
            }
            try { if (!string.IsNullOrEmpty(origActive)) { model.ShowConfiguration2(origActive); model.ForceRebuild3(false); } } catch { }
            return map;
        }

        private static string ActiveConfigName(IModelDoc2 model)
        {
            try { var cfg = model.ConfigurationManager?.ActiveConfiguration; return cfg?.Name; } catch { return null; }
        }

        private static List<DimHit> ReadDims(IModelDoc2 model)
        {
            var list = new List<DimHit>(); var seen = new HashSet<string>();
            try
            {
                var feat = model.FirstFeature() as Feature;
                while (feat != null)
                {
                    string fname = null; try { fname = feat.Name; } catch { }
                    var dd = feat.GetFirstDisplayDimension() as DisplayDimension;
                    while (dd != null)
                    {
                        var d = dd.GetDimension2(0) as Dimension;
                        if (d != null)
                        {
                            string full = null; try { full = d.FullName; } catch { }
                            if (!string.IsNullOrEmpty(full) && seen.Add(full))
                            {
                                double mm = -1; try { mm = d.SystemValue * 1000.0; } catch { }
                                list.Add(new DimHit { Dim = d, Full = full, Short = full.Split('@')[0], Feature = fname ?? "", CurMm = mm, TypeSyn = TypeSynonyms(d) });
                            }
                        }
                        dd = feat.GetNextDisplayDimension(dd) as DisplayDimension;
                    }
                    feat = feat.GetNextFeature() as Feature;
                }
            }
            catch { }
            return list;
        }

        // token match (short name / feature / type synonym / current-value echo). reqMm lets "the 20mm dimension" resolve.
        private static List<DimHit> MatchDims(List<DimHit> dims, string forParse, double reqMm)
        {
            var tokens = TargetTokens(forParse);
            // if only one driving dim exists, an unambiguous target — return it (common on a simple block)
            if (dims.Count == 1 && tokens.Count > 0) return new List<DimHit> { dims[0] };
            if (tokens.Count == 0) return new List<DimHit>();
            int best = 0; var scored = new List<KeyValuePair<DimHit, int>>();
            foreach (var d in dims)
            {
                string shortL = (d.Short ?? "").ToLowerInvariant(); string featL = (d.Feature ?? "").ToLowerInvariant(); string fullL = (d.Full ?? "").ToLowerInvariant();
                int score = 0;
                foreach (var t in tokens)
                {
                    if (shortL == t) score += 4;
                    else if (featL.Length > 0 && featL.Contains(t)) score += 2;
                    else if (d.TypeSyn.Contains(t)) score += 1;
                    else if (fullL.Contains(t)) score += 1;
                }
                if (score > 0) scored.Add(new KeyValuePair<DimHit, int>(d, score));
                if (score > best) best = score;
            }
            if (best == 0) return new List<DimHit>();
            return scored.Where(kv => kv.Value == best).Select(kv => kv.Key).ToList();
        }

        private static List<string> TargetTokens(string cmd)
        {
            var vm = Regex.Match(cmd, @"\bto\s+\d");
            if (vm.Success) cmd = cmd.Substring(0, vm.Index);
            var stop = new HashSet<string> { "change", "set", "make", "adjust", "update", "modify", "the", "a", "an", "to",
                "value", "of", "dim", "dimension", "please", "its", "it", "this", "that", "on", "for", "in", "only", "config", "configuration" };
            var outl = new List<string>();
            foreach (Match m in Regex.Matches(cmd, @"[a-z0-9]+"))
            {
                var w = m.Value; if (w.Length < 2 || stop.Contains(w)) continue;
                if (Regex.IsMatch(w, @"^\d+(mm|cm|m|in)?$")) continue;   // drop bare values
                if (!outl.Contains(w)) outl.Add(w);
            }
            return outl;
        }

        private static string[] TypeSynonyms(Dimension dim)
        {
            int t; try { t = dim.GetType(); } catch { return new string[0]; }
            switch (t)
            {
                case 5: case 6: case 14: case 15: return new[] { "diameter", "dia", "bore", "hole", "radius", "rad" };
                case 2: case 11: case 12: return new[] { "length", "height", "depth", "width", "thickness", "distance", "len", "long" };
                case 3: return new[] { "angle", "angular" };
                case 10: return new[] { "chamfer" };
                default: return new string[0];
            }
        }

        private static bool ParseValue(string cmd, out double valueMm)
        {
            valueMm = -1;
            Match m = Regex.Match(cmd, @"\bto\s+(\d+(\.\d+)?)\s*([a-z""']*)");
            if (!m.Success)
            {
                var all = Regex.Matches(cmd, @"(?<![a-z0-9])(\d+(\.\d+)?)\s*([a-z""']*)");
                if (all.Count == 0) return false;
                m = all[all.Count - 1];
            }
            if (!double.TryParse(m.Groups[1].Value, out double num)) return false;
            valueMm = num * UnitToMm(m.Groups[3].Value ?? "");
            return true;
        }

        private static double UnitToMm(string unit)
        {
            switch ((unit ?? "").Trim())
            {
                case "cm": case "centimeter": case "centimeters": return 10.0;
                case "m": case "meter": case "meters": return 1000.0;
                case "in": case "inch": case "inches": case "\"": return 25.4;
                default: return 1.0;
            }
        }

        private static string Short(string full)
        {
            if (string.IsNullOrEmpty(full)) return full;
            var segs = full.Split('@');
            return segs.Length >= 2 ? segs[0] + "@" + segs[1] : segs[0];
        }

        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
        private static string Trim(double v) => v.ToString("0.###");
    }
}
