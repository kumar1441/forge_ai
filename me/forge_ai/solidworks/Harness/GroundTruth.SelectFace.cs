using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for select_face (tool 13). DOES NOT read SolidWorks's live selection state back —
    /// the harness's own orchestration calls ForceRebuild3 between the handler's run and this measurement (to
    /// settle geometry before any GT reads it), and a rebuild drops the active selection as a side effect; a
    /// selection-based check would fail 100% of the time for reasons having nothing to do with the handler being
    /// wrong (found live: run1 measured selectedCount=0 right after the handler itself reported success). So the
    /// independence axis here is TRAVERSAL, not live-state: walks each body's face LINKED LIST
    /// (IBody2.GetFirstFace/IFace2.GetNextFace), NOT the handler's GetFaces() array, to independently recompute
    /// which face each criterion (top/bottom/left/right/largest) SHOULD pick — then the harness script cross-checks
    /// the handler's own reported area against the matching independent value.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureSelectFace(IModelDoc2 model)
        {
            var mo = new JObject();
            var part = model as PartDoc;
            if (part == null) { mo["error"] = "not a part"; return mo; }

            object[] bodies = null; try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }

            double[] box = null;
            foreach (var bo in bodies ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                double[] b = null; try { b = body.GetBodyBox() as double[]; } catch { }
                if (b == null || b.Length < 6) continue;
                box = box == null ? b : new[] {
                    Math.Min(box[0], b[0]), Math.Min(box[1], b[1]), Math.Min(box[2], b[2]),
                    Math.Max(box[3], b[3]), Math.Max(box[4], b[4]), Math.Max(box[5], b[5]) };
            }
            int vAxis = 2;
            if (box != null && box.Length >= 6) { double ySpan = box[4] - box[1], zSpan = box[5] - box[2]; vAxis = zSpan >= ySpan ? 2 : 1; }
            mo["verticalAxis"] = vAxis;

            double largestArea = -1;
            double topScore = -2, botScore = 2, leftScore = 2, rightScore = -2;
            double topArea = -1, botArea = -1, leftArea = -1, rightArea = -1;
            int planarCount = 0;

            // linked-list walk — deliberately NOT the handler's body.GetFaces() array traversal.
            foreach (var bo in bodies ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                Face2 f = null; try { f = body.GetFirstFace() as Face2; } catch { }
                while (f != null)
                {
                    Surface s = null; try { s = f.GetSurface() as Surface; } catch { }
                    bool plane = false; try { plane = s != null && s.IsPlane(); } catch { }
                    if (plane)
                    {
                        double area = 0; try { area = f.GetArea() * 1e6; } catch { }
                        double[] n = null; try { n = f.Normal as double[]; } catch { }
                        if (area > 0 && n != null && n.Length >= 3)
                        {
                            planarCount++;
                            if (area > largestArea) largestArea = area;
                            double vs = n[vAxis];
                            if (vs > topScore) { topScore = vs; topArea = area; }
                            if (vs < botScore) { botScore = vs; botArea = area; }
                            double xs = n[0];
                            if (xs < leftScore) { leftScore = xs; leftArea = area; }
                            if (xs > rightScore) { rightScore = xs; rightArea = area; }
                        }
                    }
                    Face2 next = null; try { next = f.GetNextFace() as Face2; } catch { }
                    f = next;
                }
            }

            mo["planarFaceCount"] = planarCount;
            mo["independentLargestAreaMm2"] = Math.Round(largestArea, 2);
            mo["independentTopAreaMm2"] = Math.Round(topArea, 2);
            mo["independentBottomAreaMm2"] = Math.Round(botArea, 2);
            mo["independentLeftAreaMm2"] = Math.Round(leftArea, 2);
            mo["independentRightAreaMm2"] = Math.Round(rightArea, 2);
            return mo;
        }
    }
}
