using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CutListGroup
    {
        public string Rep;          // representative body name
        public int Quantity;        // how many identical bodies in this group
        public double VolumeMm3;    // per-body volume
        public double[] DimsMm;     // sorted bounding-box extents (mm)
    }

    public class GetCutListResult
    {
        public bool IsPart;
        public int TotalBodies;
        public int UniqueGroups;    // distinct shapes
        public CutListGroup[] Groups;
        public string Verdict;      // "multibody" | "single"
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 165 — get_cut_list (READ). The heart of a cut list: group a multibody part's solid bodies into UNIQUE shapes
    /// with quantities, so "how many of each" is answered once (weldment profile/length extraction is N/A on a plain
    /// multibody — reported as bodies with volume + bounding-box). Two bodies are the same PART if their shape matches
    /// regardless of position: group by (volume, surface area, sorted bounding-box extents), all position-independent.
    /// Read-only. The INDEPENDENT GT groups by sorted bounding-box extents ALONE (a different key), so a disagreement
    /// exposes a bad grouping; known truth anchors it (multibody-block = 4 bodies, 2 unique shapes, quantities 2 & 2).
    /// Part-only (bodies live in parts).
    /// </summary>
    public static class GetCutList
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\bcut[\s-]?list\b")) return true;                                   // "cut list"
            // group/unique/quantities of bodies — more specific than list_bodies' plain count, so it claims these first.
            bool bodyNoun = Regex.IsMatch(c, @"\b(bodies|body)\b");
            if (bodyNoun && Regex.IsMatch(c, @"\b(unique|distinct|identical|duplicate|group|grouped|quantit|how many of each|each unique|bill of)\b")) return true;
            return false;
        }

        public static async Task<GetCutListResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetCutListResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to build its cut list."; return res; }
            res.IsPart = true;

            await emit("Tally", "grouping bodies into unique shapes", "run", null);

            var part = model as PartDoc;
            object[] bodies = null;
            try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            var list = (bodies ?? new object[0]).OfType<Body2>().ToList();
            res.TotalBodies = list.Count;

            var ordered = GroupByShape(list);
            res.Groups = ordered;
            res.UniqueGroups = ordered.Length;
            res.Verdict = res.TotalBodies > 1 ? "multibody" : "single";

            var diag = new StringBuilder("bodies=" + res.TotalBodies + " unique=" + res.UniqueGroups);
            foreach (var g in ordered) diag.Append(" | " + g.Rep + " x" + g.Quantity + " (" + F(g.DimsMm) + "mm, " + g.VolumeMm3 + "mm3)");
            res.Diag = diag.ToString();

            await emit("Tally", null, "done",
                res.TotalBodies + " bod" + (res.TotalBodies == 1 ? "y" : "ies") + " -> " + res.UniqueGroups + " unique shape" + (res.UniqueGroups == 1 ? "" : "s") + ": " +
                string.Join(", ", ordered.Select(g => g.Rep + " x" + g.Quantity)));

            res.Info = BuildInfo(res, ordered);
            return res;
        }

        /// <summary>
        /// Groups solid bodies into unique shapes by a position-independent signature: volume | area | sorted
        /// bounding-box extents (all rounded). Shared with GetFixtureCapacity.cs (a fixture's clamp/pocket
        /// capacity IS its dominant duplicate-body group) so both stay consistent on what counts as "the same
        /// shape".
        /// </summary>
        public static CutListGroup[] GroupByShape(List<Body2> bodies)
        {
            var groups = new Dictionary<string, CutListGroup>();
            foreach (var b in bodies)
            {
                double vol = 0, area = 0; double[] dims = { 0, 0, 0 };
                try
                {
                    var mp = b.GetMassProperties(0) as double[];   // [3]=volume, [4]=surface area (SI)
                    if (mp != null && mp.Length >= 5) { vol = mp[3] * 1e9; area = mp[4] * 1e6; }
                }
                catch { }
                try
                {
                    var box = b.GetBodyBox() as double[];           // [0..2]=min, [3..5]=max (SI)
                    if (box != null && box.Length >= 6)
                    {
                        double dx = Math.Abs(box[3] - box[0]) * 1000, dy = Math.Abs(box[4] - box[1]) * 1000, dz = Math.Abs(box[5] - box[2]) * 1000;
                        dims = new[] { dx, dy, dz }.OrderBy(v => v).ToArray();
                    }
                }
                catch { }
                string key = R(vol) + "|" + R(area) + "|" + R(dims[0]) + "|" + R(dims[1]) + "|" + R(dims[2]);
                string bname = null; try { bname = b.Name; } catch { }
                if (!groups.TryGetValue(key, out var g))
                {
                    g = new CutListGroup { Rep = bname ?? "Body", Quantity = 0, VolumeMm3 = Math.Round(vol, 1), DimsMm = dims };
                    groups[key] = g;
                }
                g.Quantity++;
            }
            return groups.Values.OrderByDescending(g => g.Quantity).ThenByDescending(g => g.VolumeMm3).ToArray();
        }

        private static string R(double v) => Math.Round(v, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static string F(double[] d) => string.Join("x", d.Select(v => Math.Round(v, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));

        private static string BuildInfo(GetCutListResult r, CutListGroup[] groups)
        {
            var sb = new StringBuilder();
            sb.Append(r.TotalBodies + " solid bod" + (r.TotalBodies == 1 ? "y" : "ies") + " -> " + r.UniqueGroups + " unique shape" + (r.UniqueGroups == 1 ? "" : "s") + ":");
            foreach (var g in groups)
                sb.Append("\n  " + g.Rep + " x" + g.Quantity + "  " + F(g.DimsMm) + "mm  " + g.VolumeMm3 + "mm3/each");
            if (r.TotalBodies <= 1) sb.Append("\nSingle-body part - the cut list is just the one body.");
            return sb.ToString();
        }
    }
}
