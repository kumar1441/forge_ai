using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SimplifyResult
    {
        public int Fillets;
        public int Holes;
        public int Suppressed;
        public string Config;
        public string Info;
        public string Error;
        public List<string> Diag = new List<string>();
    }

    /// <summary>
    /// Print-prep / simplify - suppress cosmetic fillets and small holes into a NEW configuration, so the
    /// original is untouched. Feature-only (no mates), so it's clean in this add-in.
    /// THROW #1: create a config, iterate the tree, suppress Fillet + hole features (size-filter holes if
    /// the diameter is readable). Instrumented so we see what got found/suppressed.
    /// </summary>
    public static class Simplifier
    {
        public static bool IsSimplifyIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return Regex.IsMatch(cmd, @"\b(simplify|print[- ]?prep|defeature|fea[- ]?prep|print config)\b") ||
                   (Regex.IsMatch(cmd, @"\b(suppress|kill|remove|hide)\b") && Regex.IsMatch(cmd, @"\b(fillet|fillets|hole|holes|cosmetic)\b"));
        }

        public static async Task<SimplifyResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SimplifyResult();
            if ((int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to simplify."; return res; }

            await emit("Ripple", "making a safe copy config", "run", null);
            res = SimplifyPartDoc(model, intent);
            await emit("Ripple", null, "done", "config '" + res.Config + "' - original untouched");
            foreach (var d in res.Diag) await emit(null, null, "done", "> " + d);
            await emit("Ripple", null, "done", res.Suppressed + " features suppressed (" + res.Fillets + " fillets, " + res.Holes + " holes)");

            await emit("Sentinel", "checking nothing broke", "run", null);
            int after = 0; try { after = model.Extension.GetWhatsWrongCount(); } catch { }
            await emit("Sentinel", null, "done", after == 0 ? "rebuild clean, nothing broke" : "a rebuild warning appeared - take a look");
            return res;
        }

        // Core simplify on ONE part doc: create the Forge-Simplified config, suppress cosmetic fillets + small holes
        // there (original config untouched), rebuild. Shared by Run (single part) and Batcher (every part in an assembly).
        public static SimplifyResult SimplifyPartDoc(IModelDoc2 model, string intent)
        {
            var res = new SimplifyResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res.Error = "not a part"; return res; }
            string cmd = (intent ?? "").ToLowerInvariant();

            double thrMm = 3.0;
            var m = Regex.Match(cmd, @"(\d+(\.\d+)?)\s*mm");
            if (m.Success) double.TryParse(m.Groups[1].Value, out thrMm);
            bool doFillets = !cmd.Contains("hole") || cmd.Contains("fillet") || Regex.IsMatch(cmd, @"\b(simplify|print|defeature|fea)\b");
            bool doHoles = !cmd.Contains("fillet") || cmd.Contains("hole") || Regex.IsMatch(cmd, @"\b(simplify|print|defeature|fea)\b");

            string cfg = "Forge-Simplified";
            try
            {
                var c = model.ConfigurationManager.AddConfiguration(cfg, "", "", 0, "", "");
                if (c != null) model.ShowConfiguration2(cfg);
            }
            catch { }
            res.Config = cfg;

            var feat = model.FirstFeature() as Feature;
            while (feat != null)
            {
                string tn = ""; try { tn = feat.GetTypeName2(); } catch { }
                bool isFillet = tn == "Fillet";
                bool isHoleWzd = tn == "HoleWzd" || tn == "SimpleHole" || tn == "CoscadHole";
                if (isFillet) res.Fillets++;
                if (isHoleWzd) res.Holes++;

                // Geometry-based small-hole detection: many real parts model holes as Cut-Extrudes (no HoleWzd feature).
                // A cut feature whose cylindrical faces are all small-diameter is a drillable hole -> suppress for print-prep.
                bool smallHoleCut = false;
                if (!isFillet && !isHoleWzd)
                {
                    string d; smallHoleCut = IsSmallHoleCut(feat, thrMm, out d);
                    if (d != null) res.Diag.Add((feat.Name ?? "?") + ": " + d + (smallHoleCut ? " -> suppress" : ""));
                    if (smallHoleCut) res.Holes++;
                }

                bool hit = (doFillets && isFillet) || (doHoles && ((isHoleWzd && SmallEnough(feat, thrMm)) || smallHoleCut));
                if (hit)
                {
                    try { if (feat.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null)) res.Suppressed++; }
                    catch { }
                }
                feat = feat.GetNextFeature() as Feature;
            }
            model.EditRebuild3();
            res.Info = "Simplified into '" + cfg + "' - " + res.Suppressed + " features suppressed.";
            return res;
        }

        // Geometry test: is this feature a CUT whose cylindrical faces are all small-diameter holes (<= thrMm)?
        // Catches Cut-Extrude / Cut-Revolve drilled holes that carry no HoleWzd feature. Emits a diag string.
        private static bool IsSmallHoleCut(Feature feat, double thrMm, out string diag)
        {
            diag = null;
            string tn = null; try { tn = feat.GetTypeName2(); } catch { }
            // On this 3DEXPERIENCE R2026x build cut-extrudes report GetTypeName2() == "ICE", NOT "Cut" - matching only
            // "Cut" silently skips every drilled hole. Keep BOTH (other builds/cut-revolves still report "Cut").
            if (tn == null || (tn.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) < 0 &&
                               !tn.Equals("ICE", StringComparison.OrdinalIgnoreCase))) return false;
            object[] faces = null; try { faces = feat.GetFaces() as object[]; } catch { }
            if (faces == null || faces.Length == 0) return false;
            int cyl = 0; double maxDiaMm = 0;
            foreach (var fo in faces)
            {
                var face = fo as Face2; if (face == null) continue;
                Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                if (s == null || !s.IsCylinder()) continue;
                double[] cp = s.CylinderParams as double[];
                if (cp != null && cp.Length >= 7) { cyl++; double dia = cp[6] * 2.0 * 1000.0; if (dia > maxDiaMm) maxDiaMm = dia; }
            }
            if (cyl == 0) return false;
            diag = tn + " cyl=" + cyl + " maxDiaMm=" + maxDiaMm.ToString("F1");
            return maxDiaMm > 0 && maxDiaMm <= thrMm;
        }

        // Is any driving dimension of this feature below the threshold (mm)? Used to keep only small holes.
        private static bool SmallEnough(Feature feat, double thrMm)
        {
            try
            {
                var dd = feat.GetFirstDisplayDimension() as DisplayDimension;
                while (dd != null)
                {
                    var d = dd.GetDimension2(0) as Dimension;
                    if (d != null)
                    {
                        double vMm = Convert.ToDouble(d.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null)) * 1000.0;
                        if (vMm > 0 && vMm <= thrMm) return true;
                    }
                    dd = feat.GetNextDisplayDimension(dd) as DisplayDimension;
                }
            }
            catch { }
            return false; // if we can't read a small dim, don't suppress (conservative)
        }
    }
}
