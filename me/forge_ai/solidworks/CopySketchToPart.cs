using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CopySketchToPartResult
    {
        public string SourcePath;
        public string SourceSketchName;
        public string TargetSketchName;
        public int LinesCopied;
        public int SkippedSegments;   // non-line segments the source sketch had (Rule #4 partial success)
        public bool Applied;
        public bool AlreadyDone;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CopySketchToPart (tool 152, "copy_sketch_to_part") — "copy Sketch1 from Template.SLDPRT": reads a sketch's
    /// raw line-segment geometry off a SOURCE document (reused if already open, Rule #7 — opened+closed by Forge
    /// otherwise, same shape as CopyPropertiesBetweenFiles/tool 142) and recreates it as a brand-new sketch on the
    /// currently-open TARGET document via SketchManager.CreateLine — the proven-live entity-creation primitive
    /// AddSketchEntity.cs already uses. "Clean copy, no broken external refs" is automatic: the target sketch is
    /// built from raw coordinates, not a linked/external reference to the source feature.
    ///
    /// SOURCE sketch is resolved by an explicit name in the command ("sketch Sketch1"/"sketch named Sketch1"), else
    /// the FIRST ProfileFeature sketch found. Only LINE segments are copied in this first version (arcs/splines/etc.
    /// are counted as Skipped, an honest partial-success per Rule #4) — the common single-contour profile case (a
    /// rectangle/polygon outline) is exactly this shape. TARGET plane defaults to Front (or a named plane in the
    /// command, same parser as CreateSketch). Tagged "Forge-CopiedSketch" for idempotency; never saves either doc.
    /// </summary>
    public static class CopySketchToPart
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bcopy\b")) return false;
            if (Regex.IsMatch(c, @"\b(custom )?propert(y|ies)\b")) return false;   // CopyPropertiesBetweenFiles (142) owns those
            if (!Regex.IsMatch(c, @"\bsketch\b")) return false;
            return Regex.IsMatch(c, @"\bfrom\b");
        }

        public static async Task<CopySketchToPartResult> Run(ISldWorks app, IModelDoc2 model, string intent, string attachedFile, Func<string, string, string, string, Task> emit)
        {
            var res = new CopySketchToPartResult();
            var targetPart = model as PartDoc;
            if (targetPart == null) { res.Error = "Open the part that should receive the sketch first."; return res; }

            string sourcePath = CopyPropertiesBetweenFiles.ExtractPath(intent);
            if (sourcePath == null && !string.IsNullOrEmpty(attachedFile)) sourcePath = attachedFile;
            if (sourcePath == null)
            {
                res.Error = "No source file found in the command — say \"copy the sketch from <file>.sldprt\" or attach it.";
                return res;
            }
            if (!File.Exists(sourcePath)) { res.Error = "Couldn't find \"" + sourcePath + "\"."; return res; }
            res.SourcePath = sourcePath;

            var existing = FindTaggedFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Applied = true; res.TargetSketchName = SafeName(existing);
                res.LinesCopied = SegmentCount(existing);
                res.Info = "A copied sketch (" + res.TargetSketchName + ") is already here — nothing to do.";
                await emit("Draftsman", null, "done", res.TargetSketchName + " already present — nothing to do");
                return res;
            }

            // ---- resolve the SOURCE document: reuse if already open (Rule #7), else open it ourselves ----
            bool sourceWasOpen = false;
            IModelDoc2 sourceDoc = null;
            try { sourceDoc = app.GetOpenDocumentByName(sourcePath) as IModelDoc2; } catch { }
            if (sourceDoc != null) sourceWasOpen = true;
            else
            {
                await emit("Reader", "opening the source file to read its sketch", "run", null);
                int errs = 0, warns = 0;
                try { sourceDoc = app.OpenDoc6(sourcePath, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns) as IModelDoc2; }
                catch { sourceDoc = null; }
                if (sourceDoc == null) { res.Error = "Couldn't open the source file \"" + Path.GetFileName(sourcePath) + "\" (errs=" + errs + ")."; return res; }
            }

            List<double[]> lines = null;
            try
            {
                string wantName = ParseSketchName(intent);
                var srcSketchFeat = FindSketchFeature(sourceDoc, wantName);
                if (srcSketchFeat == null)
                { res.Error = wantName != null ? ("No sketch named \"" + wantName + "\" in " + Path.GetFileName(sourcePath) + ".") : ("No sketch found in " + Path.GetFileName(sourcePath) + "."); return res; }
                res.SourceSketchName = SafeName(srcSketchFeat);

                lines = new List<double[]>();
                var sk = srcSketchFeat.GetSpecificFeature2() as Sketch;
                foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                {
                    var seg = o as SketchSegment; if (seg == null) continue;
                    bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                    if (constr) continue;
                    int t = -1; try { t = seg.GetType(); } catch { }
                    if (t != (int)swSketchSegments_e.swSketchLINE) { res.SkippedSegments++; continue; }
                    var line = seg as SketchLine; if (line == null) { res.SkippedSegments++; continue; }
                    var p1 = line.GetStartPoint2() as SketchPoint;
                    var p2 = line.GetEndPoint2() as SketchPoint;
                    if (p1 == null || p2 == null) { res.SkippedSegments++; continue; }
                    lines.Add(new double[] { p1.X, p1.Y, p1.Z, p2.X, p2.Y, p2.Z });
                }
            }
            catch (Exception ex) { res.Error = "Reading the source sketch failed: " + ex.Message; return res; }
            finally
            {
                if (!sourceWasOpen && sourceDoc != null)
                { try { app.CloseDoc(sourcePath); } catch { } }
            }

            if (lines == null || lines.Count == 0)
            { res.Error = "The source sketch has no line segments to copy (only arcs/splines/etc. are supported so far)."; return res; }

            string plane = ParsePlane(intent);
            await emit("Draftsman", "copying " + lines.Count + " line(s) from " + Path.GetFileName(sourcePath), "run", null);

            var beforeNames = new HashSet<string>(SketchFeatureNames(model));
            try
            {
                SelectPlane(model, plane);
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                foreach (var ln in lines) sm.CreateLine(ln[0], ln[1], ln[2], ln[3], ln[4], ln[5]);
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Recreating the sketch failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, beforeNames);
            int segs = created != null ? SegmentCount(created) : 0;
            if (created == null || segs == 0)
            { res.Error = "The copied sketch was not created."; await emit("Draftsman", null, "fail", res.Error); return res; }

            try { created.Name = "Forge-CopiedSketch"; } catch { }
            res.TargetSketchName = SafeName(created);
            res.LinesCopied = segs;
            res.Applied = true;
            res.Diag = "plane=" + plane + " name=" + res.TargetSketchName + " lines=" + segs + " skipped=" + res.SkippedSegments;

            await emit("Draftsman", null, "done", res.TargetSketchName + " copied (" + segs + " line(s))");

            res.Info = "Copied " + segs + " line segment(s) from " + (res.SourceSketchName ?? "the source sketch") + " in " +
                       Path.GetFileName(sourcePath) + " into a new sketch (" + res.TargetSketchName + ") on " + plane +
                       (res.SkippedSegments > 0 ? "; " + res.SkippedSegments + " non-line segment(s) skipped." : ".") +
                       " One Ctrl+Z removes it; Forge didn't save either document.";
            return res;
        }

        // ---------- parsing ----------
        private static string ParseSketchName(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var m = Regex.Match(intent, @"\bsketch\s+(?:named\s+|called\s+)?[""']?([A-Za-z0-9_\-]+)[""']?", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string name = m.Groups[1].Value;
            if (string.Equals(name, "from", StringComparison.OrdinalIgnoreCase)) return null;   // bare "the sketch from <file>"
            return name;
        }

        private static string ParsePlane(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\btop\s*plane\b")) return "Top Plane";
            if (Regex.IsMatch(c, @"\bright\s*plane\b")) return "Right Plane";
            return "Front Plane";
        }

        // ---------- geometry helpers ----------
        private static void SelectPlane(IModelDoc2 model, string plane)
        { try { model.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); } catch { } }

        private static Feature FindSketchFeature(IModelDoc2 model, string wantName)
        {
            Feature first = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "ProfileFeature")
                {
                    if (first == null) first = f;
                    if (wantName != null)
                    {
                        string nm = SafeName(f);
                        if (nm != null && nm.Equals(wantName, StringComparison.OrdinalIgnoreCase)) return f;
                    }
                }
                f = f.GetNextFeature() as Feature;
            }
            return wantName == null ? first : null;
        }

        private static int SegmentCount(Feature f)
        {
            int segs = 0;
            try
            {
                var sk = f.GetSpecificFeature2() as Sketch;
                if (sk != null)
                    foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                    {
                        var seg = o as SketchSegment; if (seg == null) continue;
                        bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                        if (!constr) segs++;
                    }
            }
            catch { }
            return segs;
        }

        private static IEnumerable<string> SketchFeatureNames(IModelDoc2 model)
        {
            var list = new List<string>();
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "ProfileFeature") list.Add(SafeName(f));
                f = f.GetNextFeature() as Feature;
            }
            return list;
        }

        private static Feature NewSketchFeature(IModelDoc2 model, HashSet<string> before)
        {
            Feature found = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "ProfileFeature" && !before.Contains(SafeName(f))) found = f;
                f = f.GetNextFeature() as Feature;
            }
            return found;
        }

        private static Feature FindTaggedFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.Equals("Forge-CopiedSketch", StringComparison.OrdinalIgnoreCase)) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
    }
}
