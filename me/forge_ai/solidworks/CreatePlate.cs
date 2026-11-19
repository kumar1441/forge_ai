using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// CreatePlate — tool: create a rectangular plate part FROM SCRATCH (blank part + centred rectangle on
    /// the Front plane + extrude). The agent-loop's "make a plate" primitive — CreatePart alone makes a BLANK
    /// part (no solid), and AddBoss needs an existing face, so a from-scratch rectangular boss is its own tool.
    /// Intent carries L x W x T in mm: "create a 100x60x8 mm plate", "make a 40x40x40 cube".
    /// Reuses the proven MakeSeededBlock box path (CreateCornerRectangle + FeatureExtrusion3). Never saves.
    /// </summary>
    public class CreatePlateResult
    {
        public bool Created;
        public double[] DimsMm;        // {L, W, T}
        public double VolumeMm3 = -1;  // post-rebuild, independently measured
        public bool RebuildClean;
        public string Info;
        public string Error;
    }

    public static class CreatePlate
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool plate = Regex.IsMatch(c, @"\b(plate|panel|slab|blank|block|cube|rectangular)\b");
            bool create = Regex.IsMatch(c, @"\b(create|make|build|new|start)\b");
            bool hasDims = Regex.IsMatch(c, @"\d+\s*[x×]\s*\d+(\s*[x×]\s*\d+)?\s*mm");
            return create && plate && (hasDims || Regex.IsMatch(c, @"\bplate\b"));
        }

        public static async Task<CreatePlateResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreatePlateResult();
            string cue = CreateGuardrail.UnsupportedCue(intent);
            if (cue != null) { res.Error = "I can only create a plain plate from scratch — \"" + cue + "\" (compound/positional/feature requests) isn't supported yet. Ask for a bare shape like \"create a 50mm plate\"."; return res; }
            double L = 100, W = 60, T = 8;
            var m = Regex.Match(intent ?? "", @"(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)");
            if (m.Success)
            {
                double.TryParse(m.Groups[1].Value, out L); double.TryParse(m.Groups[2].Value, out W); double.TryParse(m.Groups[3].Value, out T);
            }
            else
            {
                // two dims or a single "N mm plate" fallback
                var m2 = Regex.Match(intent ?? "", @"(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)\s*mm");
                if (m2.Success) { double.TryParse(m2.Groups[1].Value, out L); double.TryParse(m2.Groups[2].Value, out W); }
                else { var m1 = Regex.Match(intent ?? "", @"(\d+(?:\.\d+)?)\s*mm"); if (m1.Success) double.TryParse(m1.Groups[1].Value, out L); }
            }
            res.DimsMm = new[] { L, W, T };
            await emit("Builder", "creating " + Trim(L) + "×" + Trim(W) + "×" + Trim(T) + " mm plate", "run", null);
            try
            {
                // clean slate: close any open doc so the new one is active without lock issues
                try { var cur = app.ActiveDoc as IModelDoc2; if (cur != null) app.CloseDoc(cur.GetTitle()); } catch { }
                string template = null;
                try { template = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart); } catch { }
                var part = app.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (part == null) { res.Error = "NewDocument returned nothing — the part template may be invalid."; return res; }
                double mU = 0.001;
                bool sel = false;
                try { sel = part.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null, 0); } catch { }
                if (!sel) { res.Error = "could not select the Front Plane"; return res; }
                part.SketchManager.InsertSketch(true);
                part.SketchManager.CreateCornerRectangle(-L / 2 * mU, -W / 2 * mU, 0, L / 2 * mU, W / 2 * mU, 0);
                part.SketchManager.InsertSketch(true);
                part.FeatureManager.FeatureExtrusion3(true, false, false, 0, 0, T * mU, 0, false, false, false, false, 0, 0,
                    false, false, false, false, true, true, true, 0, 0, false);
                try { part.ForceRebuild3(false); } catch { }
                try { res.RebuildClean = part.Extension.GetWhatsWrongCount() == 0; } catch { }
                try { var mp = part.Extension.CreateMassProperty(); if (mp != null) { mp.UseSystemUnits = true; res.VolumeMm3 = mp.Volume * 1e9; } } catch { }
                res.Created = true;
                await emit("Sentinel", null, "done", "plate " + Trim(L) + "×" + Trim(W) + "×" + Trim(T) + " mm, " +
                    (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³" : "volume read") + ", rebuild " + (res.RebuildClean ? "clean" : "flagged"));
                res.Info = "Created a " + Trim(L) + "×" + Trim(W) + "×" + Trim(T) + " mm plate — " +
                           (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³, " : "") +
                           "rebuild " + (res.RebuildClean ? "clean" : "flagged") + ". Forge didn't save.";
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }

        private static string Trim(double v) => v.ToString("0.###");
    }
}
