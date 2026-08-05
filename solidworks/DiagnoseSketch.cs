using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SketchDiagnosis
    {
        public string Name;
        public string Status;            // "fully defined" | "under defined" | "over defined" | "no solution" | "unknown"
        public int Segments;
        public int ConstructionSegments;
        public int Points;
        public int UnrelatedSegments;    // segments carrying ZERO geometric relations — the usual reason a sketch drifts
        public int ClosedContours;
        public bool OpenProfile;         // has geometry but no closed contour: it will not extrude
        public string Why;               // the one-line reason, or null when the sketch is clean
    }

    public class DiagnoseSketchResult
    {
        public int SketchCount;
        public int FullyDefined;
        public int UnderDefined;
        public int OverDefined;
        public int OpenProfiles;
        public string Worst;             // the sketch a user should fix first
        public List<SketchDiagnosis> Sketches = new List<SketchDiagnosis>();
        public bool ReadOnly = true;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// DiagnoseSketch (tool #149 diagnose_sketch) — not "is this sketch under-defined" (get_sketch_info already counts
    /// that) but WHY: how many entities carry no relations at all, whether the profile is even closed, and which sketch
    /// to fix first. The question an engineer actually asks when a part drifts after someone edits a dimension.
    ///
    ///   Reader — walk the tree's ProfileFeatures, open each sketch WITHOUT editing it, and read: constrained status,
    ///            segment/point inventory, per-segment relation counts, and closed-contour count.
    ///   Verdict— an under-defined sketch is explained by the number of segments with ZERO relations; a sketch with
    ///            geometry but no closed contour is called out separately, because that one can't extrude at all and is
    ///            a different fix.
    ///
    /// READ-ONLY: no sketch is entered for edit, nothing is constrained, nothing is rebuilt or saved. (fully_define_sketch,
    /// tool 150, is the WRITE that acts on this diagnosis — deliberately a separate command.)
    /// </summary>
    public static class DiagnoseSketch
    {
        // NARROW: needs a sketch noun AND a diagnostic question. get_sketch_info (the plain count) keeps "list/show the
        // sketches"; this one only fires on why/diagnose/under-defined/unconstrained wording.
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(sketch|sketches)\b")) return false;
            if (Regex.IsMatch(c, @"\b(rename|delete|suppress|create|add|draw|prefix)\b")) return false;   // writes belong elsewhere
            return Regex.IsMatch(c, @"\b(why|diagnose|diagnosis|explain|under[- ]?defined|underdefined|unconstrained|not fully defined|problem|problems|wrong|open contour|open profile)\b");
        }

        public static async Task<DiagnoseSketchResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DiagnoseSketchResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Sketch diagnosis works on a single part — open the .SLDPRT."; return res; }

            await emit("Reader", "reading every sketch and its relations", "run", null);

            string wanted = ParseSketchName(intent);

            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (string.Equals(tn, "ProfileFeature", StringComparison.OrdinalIgnoreCase))
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (wanted == null || Norm(nm).Contains(Norm(wanted)))
                    {
                        var sk = null as Sketch;
                        try { sk = f.GetSpecificFeature2() as Sketch; } catch { }
                        if (sk != null) res.Sketches.Add(Diagnose(nm, sk));
                    }
                }
                f = f.GetNextFeature() as Feature;
            }

            res.SketchCount = res.Sketches.Count;
            if (res.SketchCount == 0)
            {
                res.Error = wanted != null
                    ? "No sketch here matches \"" + wanted + "\"."
                    : "This part has no sketches — an imported dumb solid has geometry but no sketch to diagnose.";
                await emit("Reader", null, "fail", res.Error);
                return res;
            }

            res.FullyDefined = res.Sketches.Count(s => s.Status == "fully defined");
            res.UnderDefined = res.Sketches.Count(s => s.Status == "under defined");
            res.OverDefined = res.Sketches.Count(s => s.Status == "over defined");
            res.OpenProfiles = res.Sketches.Count(s => s.OpenProfile);

            // fix-this-first: an open profile beats an over-defined sketch beats the most unconstrained under-defined one
            var worst = res.Sketches.FirstOrDefault(s => s.OpenProfile)
                     ?? res.Sketches.FirstOrDefault(s => s.Status == "over defined")
                     ?? res.Sketches.Where(s => s.Status == "under defined").OrderByDescending(s => s.UnrelatedSegments).FirstOrDefault();
            res.Worst = worst?.Name;

            await emit("Verdict", null, "done",
                res.SketchCount + " sketch" + (res.SketchCount == 1 ? "" : "es") + " · " + res.FullyDefined + " fully defined · " +
                res.UnderDefined + " under-defined" + (res.OpenProfiles > 0 ? " · " + res.OpenProfiles + " OPEN profile(s)" : ""));

            res.Info = res.UnderDefined == 0 && res.OverDefined == 0 && res.OpenProfiles == 0
                ? "All " + res.SketchCount + " sketches are fully defined and closed — nothing to fix."
                : (worst != null ? worst.Name + ": " + worst.Why + ". " : "") +
                  res.FullyDefined + " of " + res.SketchCount + " sketches are fully defined" +
                  (res.OpenProfiles > 0 ? ", " + res.OpenProfiles + " have no closed contour (they cannot extrude)" : "") + ".";
            return res;
        }

        private static SketchDiagnosis Diagnose(string name, Sketch sk)
        {
            var d = new SketchDiagnosis { Name = name };

            int st = -1; try { st = sk.GetConstrainedStatus(); } catch { }
            d.Status = st == (int)swConstrainedStatus_e.swFullyConstrained ? "fully defined"
                     : st == (int)swConstrainedStatus_e.swUnderConstrained ? "under defined"
                     : st == (int)swConstrainedStatus_e.swOverConstrained ? "over defined"
                     : st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution ? "no solution"
                     : "unknown";

            object[] segs = null; try { segs = sk.GetSketchSegments() as object[]; } catch { }
            foreach (var o in segs ?? new object[0])
            {
                var s = o as SketchSegment; if (s == null) continue;
                d.Segments++;
                bool cons = false; try { cons = s.ConstructionGeometry; } catch { }
                if (cons) d.ConstructionSegments++;
                int rc = -1; try { rc = s.GetRelationsCount(); } catch { }
                if (rc == 0) d.UnrelatedSegments++;
            }
            try { d.Points = sk.GetSketchPointsCount2(); } catch { }
            try { d.ClosedContours = sk.GetSketchContourCount(); } catch { }
            d.OpenProfile = d.Segments > d.ConstructionSegments && d.ClosedContours == 0;

            d.Why = d.OpenProfile ? "the profile has no closed contour, so it cannot extrude"
                  : d.Status == "over defined" ? "over defined — a relation or dimension is fighting another"
                  : d.Status == "under defined" ? (d.UnrelatedSegments > 0
                        ? d.UnrelatedSegments + " of " + d.Segments + " entities carry no relations, so they move freely when anything upstream changes"
                        : "every entity has relations but the sketch still has free dimensions")
                  : null;
            return d;
        }

        private static string ParseSketchName(string intent)
        {
            var m = Regex.Match(intent ?? "", @"\b(sketch\s*\d+|[A-Za-z][\w\-]*sketch[\w\-]*)\b", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string v = m.Value.Trim();
            return Regex.IsMatch(v, @"^sketch(es)?$", RegexOptions.IgnoreCase) ? null : v;
        }

        private static string Norm(string s) { return Regex.Replace(s ?? "", @"[^0-9A-Za-z]", "").ToLowerInvariant(); }
    }
}
