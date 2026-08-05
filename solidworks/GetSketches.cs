using System;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetSketchesResult
    {
        public int SketchCount;
        public int FullyDefined;
        public int UnderDefined;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — get_sketch_info (READ). Counts a part's sketches and how many are fully-defined vs under-defined —
    /// a real DFM/robustness signal (under-defined sketches drift when edited). Walks the feature tree; a sketch is a
    /// feature whose specific-feature is an ISketch. Read-only; own read of Sketch.GetConstrainedState/status.
    /// </summary>
    public static class GetSketches
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\bsketch(es)?\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(how many|count|list|under.?defined|fully.?defined|info|inventory)\b");
        }

        public static async Task<GetSketchesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetSketchesResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to inventory its sketches."; return res; }

            await emit("Reader", "reading sketches", "run", null);
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                CountSketchesIn(f, res);
                f = f.GetNextFeature() as Feature;
            }

            await emit("Reader", null, "done", res.SketchCount + " sketches · " + res.UnderDefined + " under-defined");
            if (res.SketchCount == 0) { res.Info = "No sketches (an imported dumb solid, or empty part)."; return res; }

            res.Info = res.SketchCount + " sketches: " + res.FullyDefined + " fully-defined, " + res.UnderDefined +
                       " under-defined" + (res.UnderDefined > 0 ? " (these can drift when edited)" : "") + ".";
            return res;
        }

        // a sketch may be a top-level feature OR consumed under an extrude/cut sub-feature — check both
        private static void CountSketchesIn(Feature f, GetSketchesResult res)
        {
            if (f == null) return;
            Sketch sk = null; try { sk = f.GetSpecificFeature2() as Sketch; } catch { }
            if (sk != null)
            {
                res.SketchCount++;
                bool full = false; try { full = sk.GetConstrainedStatus() == (int)swConstrainedStatus_e.swFullyConstrained; } catch { }
                if (full) res.FullyDefined++; else res.UnderDefined++;
            }
            var sub = f.GetFirstSubFeature() as Feature;
            while (sub != null) { CountSketchesIn(sub, res); sub = sub.GetNextSubFeature() as Feature; }
        }
    }
}
