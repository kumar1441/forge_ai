using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class MeshOpeningsResult
    {
        public int OpeningsPerRow = -1;    // gaps between adjacent crossing wires along one row (wires - 1)
        public int RowWireCount;           // wires running along the row axis (parallel to a row)
        public int ColWireCount;           // wires crossing a row (their count - 1 IS the opening count)
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// MeshOpenings (READ-ONLY, ASSEMBLY): count of openings (holes/apertures) across one row of a woven/welded
    /// wire mesh — "count of openings across one row of the mesh", "how many mesh cells in a row", "opening count".
    ///
    /// test-loop wrong-answer finding count-mesh-cells (real "Wire Mesh (Parametric)" woven screen, 137 components,
    /// "what's the count of openings across one row of the mesh"): no action in the cloud's vocabulary for this,
    /// so it fell to a generic assembly scan (137 raw component count — an irrelevant answer, not opening count).
    /// A genuinely NEW geometric-analysis capability, not a routing fix.
    ///
    /// Method (honest, geometry-derived — Character #2/#4): a rectangular woven/welded mesh is built from two wire
    /// families running PERPENDICULAR to each other (rows and columns), each spanning the full mesh in its own
    /// direction. Classify every non-fastener, non-frame component by its own bounding-box aspect ratio within the
    /// mesh's plane (the elongated axis IS the wire's run direction) into a "runs along axis A" group and a "runs
    /// along axis B" group. Since every wire in one family crosses EVERY wire in the other family (a rectangular
    /// weave), the number of openings along ANY row equals (crossing-family wire count − 1) — you don't need to
    /// isolate one specific row; every row has the same crossing count. Reports both wire-family counts so the
    /// derivation is transparent, not just the final number.
    ///
    /// Robustness: ASSEMBLY only (a woven mesh is inherently a multi-component weave; a perforated PART needs a
    /// different, feature-based approach — refused honestly, not guessed). A component whose bbox isn't clearly
    /// elongated in the mesh plane (aspect ratio too close to 1:1 — a frame, a fastener, a hub) is excluded from
    /// classification rather than forced into a group. Fewer than 2 wires in EITHER family → honest refusal
    /// (Rule #4), never a guessed opening count.
    /// </summary>
    public static class MeshOpenings
    {
        public static bool IsMeshOpeningsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // "change/set/adjust/make the mesh opening to 2mm" is a SET_DIMENSION WRITE, not a count question —
            // a real regression this matcher was too broad to avoid (Wire Mesh model, sibling scenario
            // change-mesh-opening: its "change the mesh opening to 2mm" was getting misrouted here instead of
            // set_dimension). Exclude write verbs up front, same shape as WallThickness's shell/hollow exclusion.
            if (Regex.IsMatch(c, @"\b(change|set|adjust|make|increase|decrease|reduce|widen|narrow|resize|shrink|grow|enlarge)\b")) return false;
            return Regex.IsMatch(c, @"\bmesh\b.{0,20}\b(openings?|cells?|apertures?|holes?)\b")
                || Regex.IsMatch(c, @"\b(openings?|cells?|apertures?)\b.{0,20}\bmesh\b")
                || Regex.IsMatch(c, @"\bopenings?\s+(across|per|along|in)\s+(a|one|the)?\s*row\b")
                || Regex.IsMatch(c, @"\bmesh\s+count\b");
        }

        public static async Task<MeshOpeningsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new MeshOpeningsResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            { res.Error = "Counting mesh openings works on an assembly of woven/welded wires — open the .SLDASM, not a single part."; return res; }
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "This document has no assembly structure to analyze."; return res; }

            await emit("Scout", "reading the mesh components", "run", null);
            try { asm.ResolveAllLightWeightComponents(false); } catch { }

            double[] unionBox = null;
            var comps = new List<(Component2 c, double[] box)>();
            foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                if (LooksLikeFrameOrFastener(nm)) continue;
                double[] box = null; try { box = c.GetBox(false, false) as double[]; } catch { }
                if (box == null || box.Length < 6 || box[3] <= box[0]) continue;
                comps.Add((c, box));
                unionBox = Union(unionBox, box);
            }
            if (comps.Count < 4 || unionBox == null)
            { res.Error = "Not enough wire-like components found to analyze a mesh pattern (" + comps.Count + " candidates)."; return res; }

            // the mesh's own PLANE = the two largest-spanning axes of the union bbox; the third (smallest) is the
            // wire-diameter/out-of-plane direction and is ignored for row/column classification
            double ux = unionBox[3] - unionBox[0], uy = unionBox[4] - unionBox[1], uz = unionBox[5] - unionBox[2];
            int thinAxis = (ux <= uy && ux <= uz) ? 0 : (uy <= uz ? 1 : 2);
            int axisA = thinAxis == 0 ? 1 : 0;
            int axisB = thinAxis == 2 ? 1 : 2;

            var groupA = new List<double>();   // cross-axis (axisB) centers of wires that RUN along axisA
            var groupB = new List<double>();   // cross-axis (axisA) centers of wires that RUN along axisB
            int excluded = 0;
            foreach (var (c, box) in comps)
            {
                double spanA = box[axisA + 3] - box[axisA], spanB = box[axisB + 3] - box[axisB];
                double bigger = Math.Max(spanA, spanB), smaller = Math.Min(spanA, spanB);
                if (bigger < 1e-6 || bigger < 1.5 * smaller) { excluded++; continue; }   // not clearly elongated — skip
                if (spanA > spanB) groupA.Add((box[axisB] + box[axisB + 3]) / 2.0);      // runs along A -> its fixed position is on B
                else groupB.Add((box[axisA] + box[axisA + 3]) / 2.0);                    // runs along B -> its fixed position is on A
            }

            if (groupA.Count < 2 || groupB.Count < 2)
            {
                res.Error = "Couldn't separate the components into two clear wire directions (found " + groupA.Count +
                            " + " + groupB.Count + " after excluding " + excluded + " non-elongated parts) — this mesh's " +
                            "geometry doesn't fit the row/column weave assumption this method relies on.";
                return res;
            }

            // rows = the LARGER family (traversing along it crosses every member of the smaller, crossing family) —
            // openings along one row = crossing-family count - 1 (every row is crossed by every crossing-family wire)
            bool aIsRows = groupA.Count >= groupB.Count;
            int rowCount = aIsRows ? groupA.Count : groupB.Count;
            int colCount = aIsRows ? groupB.Count : groupA.Count;

            res.RowWireCount = rowCount;
            res.ColWireCount = colCount;
            res.OpeningsPerRow = colCount - 1;
            res.Verified = res.OpeningsPerRow > 0;

            res.Info = res.Verified
                ? res.OpeningsPerRow + " openings across one row (" + rowCount + " row wires × " + colCount +
                  " crossing wires — every row is crossed by all " + colCount + " of the other direction's wires, " +
                  "giving " + colCount + " − 1 = " + res.OpeningsPerRow + " enclosed openings)."
                : "Only " + colCount + " crossing wire(s) found — not enough to enclose a real opening.";
            await emit("Scout", null, "done", rowCount + "×" + colCount + " wires, " + res.OpeningsPerRow + " openings/row");
            return res;
        }

        private static readonly string[] FrameFastenerHints =
            { "frame", "bolt", "screw", "nut", "washer", "hcs", "shcs", "clip", "bracket" };
        private static bool LooksLikeFrameOrFastener(string n)
        {
            if (string.IsNullOrEmpty(n)) return false; n = n.ToLowerInvariant();
            foreach (var h in FrameFastenerHints) if (n.Contains(h)) return true;
            return false;
        }

        private static double[] Union(double[] acc, double[] b)
        {
            if (b == null || b.Length < 6) return acc;
            if (acc == null) return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
            return new[]
            {
                Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
            };
        }
    }
}
