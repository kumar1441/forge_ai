using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the arc_height handler. Shares NO code with ArcHeight.cs, and uses a
    /// DIFFERENT SolidWorks primitive at every step:
    ///
    ///   - main body selection: same "largest volume" rule (a shared, deterministic pick — not a measurement).
    ///   - length/height axis + bbox: IBody2.GetBodyBox, same as the handler (there's only one honest way to
    ///     read a bbox, so this isn't where independence comes from).
    ///   - END heights: IBody2.GetVertices() — real BREP topology vertices near the two length extremes, averaged.
    ///     The handler never touches BREP vertices at all (it only reads tessellation triangles).
    ///   - MIDPOINT height: Face2.GetClosestPointOn at the bbox-computed midpoint coordinate — a closest-point
    ///     SURFACE PROJECTION (the same primitive WallThickness/AddHole use elsewhere), not a tessellated point
    ///     cloud. Genuinely different math than the handler's max-per-bin triangle scan.
    ///
    /// Agreement between this and ArcHeight.cs's own number is a real cross-check, not a mirror of its math.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureArcHeight(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            object[] bodies = null;
            try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            mo["bodyCount"] = bodies?.Length ?? 0;
            if (bodies == null || bodies.Length == 0) { mo["hasSolid"] = false; return mo; }
            mo["hasSolid"] = true;

            Body2 mainBody = null; double bestVol = -1;
            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                double vol = 0;
                try { var mp = body.GetMassProperties(1.0) as double[]; if (mp != null && mp.Length >= 4) vol = mp[3]; } catch { }
                if (vol > bestVol) { bestVol = vol; mainBody = body; }
            }
            if (mainBody == null) { mo["arcHeightMm"] = -1; return mo; }

            double[] bbox = null; try { bbox = mainBody.GetBodyBox() as double[]; } catch { }
            if (bbox == null || bbox.Length < 6) { mo["arcHeightMm"] = -1; return mo; }
            double dx = bbox[3] - bbox[0], dy = bbox[4] - bbox[1], dz = bbox[5] - bbox[2];
            int lenAxis = (dx >= dy && dx >= dz) ? 0 : (dy >= dz ? 1 : 2);
            int a1 = (lenAxis + 1) % 3, a2 = (lenAxis + 2) % 3;
            double span1 = bbox[a1 + 3] - bbox[a1], span2 = bbox[a2 + 3] - bbox[a2];
            int heightAxis = span1 >= span2 ? a1 : a2;

            double lenMin = bbox[lenAxis], lenMax = bbox[lenAxis + 3];
            double lenSpan = lenMax - lenMin;
            mo["chordLengthMm"] = lenSpan * 1000.0;
            if (lenSpan < 1e-6) { mo["arcHeightMm"] = -1; return mo; }

            // END heights: real BREP vertices within the first/last 5% of the length span, averaged on the height axis
            object[] verts = null; try { verts = mainBody.GetVertices() as object[]; } catch { }
            double startSum = 0, endSum = 0; int startN = 0, endN = 0;
            double band = lenSpan * 0.05;
            foreach (var vo in verts ?? new object[0])
            {
                var vert = vo as Vertex; if (vert == null) continue;
                double[] p = null; try { p = vert.GetPoint() as double[]; } catch { }
                if (p == null || p.Length < 3) continue;
                double lenC = p[lenAxis], hC = p[heightAxis];
                if (lenC <= lenMin + band) { startSum += hC; startN++; }
                else if (lenC >= lenMax - band) { endSum += hC; endN++; }
            }
            if (startN == 0 || endN == 0) { mo["arcHeightMm"] = -1; mo["reason"] = "no BREP vertices near the ends"; return mo; }
            double hStart = startSum / startN, hEnd = endSum / endN;

            // MIDPOINT height: closest-point surface projection at the bbox-computed midpoint, tried against every
            // face — a different primitive (surface projection) than the handler's tessellation scan.
            double midLen = (lenMin + lenMax) / 2.0;
            double[] midGuess = new double[3];
            midGuess[lenAxis] = midLen;
            midGuess[heightAxis] = (bbox[heightAxis] + bbox[heightAxis + 3]) / 2.0;
            int thickAxis = 3 - lenAxis - heightAxis;
            midGuess[thickAxis] = (bbox[thickAxis] + bbox[thickAxis + 3]) / 2.0;

            object[] faces = null; try { faces = mainBody.GetFaces() as object[]; } catch { }
            double bestH = double.NaN, bestDist = double.MaxValue;
            foreach (var fo in faces ?? new object[0])
            {
                var face = fo as Face2; if (face == null) continue;
                double[] q = null;
                try { q = face.GetClosestPointOn(midGuess[0], midGuess[1], midGuess[2]) as double[]; } catch { }
                if (q == null || q.Length < 3) continue;
                double qLen = q[lenAxis];
                if (Math.Abs(qLen - midLen) > lenSpan * 0.1) continue;   // only consider hits actually near mid-length
                double d = Math.Abs(q[thickAxis] - midGuess[thickAxis]);
                if (d < bestDist) { bestDist = d; bestH = q[heightAxis]; }
            }

            mo["heightAtStartMm"] = hStart * 1000.0;
            mo["heightAtEndMm"] = hEnd * 1000.0;
            if (double.IsNaN(bestH)) { mo["arcHeightMm"] = -1; mo["reason"] = "no face found near mid-length"; return mo; }
            mo["heightAtMidMm"] = bestH * 1000.0;
            double chordAtMid = (hStart + hEnd) / 2.0;
            mo["arcHeightMm"] = Math.Abs(bestH - chordAtMid) * 1000.0;
            return mo;
        }
    }
}
