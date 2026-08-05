using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetFixtureCapacityResult
    {
        public bool Applicable;
        public int TotalBodies;
        public int UniqueGroups;
        public int MaxQuantity;
        public string RepBody;
        public string SourceComponent;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// READ: "what's the max parts this fixture can take", "how many pieces can this jig hold" — a fixture/jig
    /// capacity question the cloud parser has no action for (test-loop wrong-answer finding count-clamping-
    /// positions: routed_handler=scan, which only reported the CURRENT component count, not the fixture's
    /// capacity). A fixture's capacity isn't a labeled property anywhere in the model — it IS the count of
    /// identical repeated pockets/clamp positions machined into the fixture body, which shows up as a duplicate-
    /// body group (same shape cut/positioned N times as separate solid bodies) in a multibody part. Reuses
    /// GetCutList.GroupByShape (volume + area + sorted extents, position-independent) so the two stay
    /// consistent, but reports only the DOMINANT group's quantity — the fixture's actual, geometry-derived
    /// capacity, not a generic body/component count. On an assembly, drills into whichever non-suppressed
    /// component carries the most solid bodies (the fixture plate itself, distinct from single-body hardware/
    /// fasteners) without opening a separate document — IComponent2.GetBodies3 reads bodies directly off the
    /// already-resident component. Honest refusal when no repeated shape exists — nothing to derive a capacity
    /// number from, so Forge won't guess one from the file name. Read-only.
    /// </summary>
    public static class GetFixtureCapacity
    {
        private static readonly Regex CapacityPattern = new Regex(
            @"\b(max(?:imum)?|how many)\b[^.?!]{0,40}\b(parts?|pieces?|workpieces?|pcs|items?)\b[^.?!]{0,40}\b(fixture|jig|vise|vice|clamp\w*|hold|take|fit)\b",
            RegexOptions.IgnoreCase);

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return CapacityPattern.IsMatch(cmd);
        }

        public static async Task<GetFixtureCapacityResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetFixtureCapacityResult();
            if (model == null) { res.Error = "Open a part or assembly to check capacity."; return res; }
            int docType = 0; try { docType = (int)model.GetType(); } catch { }
            if (docType != (int)swDocumentTypes_e.swDocPART && docType != (int)swDocumentTypes_e.swDocASSEMBLY)
            { res.Error = "Open a part or assembly to check capacity."; return res; }

            await emit("Tally", "looking for repeated clamp/pocket geometry", "run", null);

            List<Body2> bodies = new List<Body2>();
            string sourceName = null;

            if (docType == (int)swDocumentTypes_e.swDocPART)
            {
                var part = model as PartDoc;
                object[] b = null; try { b = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
                bodies = (b ?? new object[0]).OfType<Body2>().ToList();
            }
            else
            {
                var asmDoc = model as AssemblyDoc;
                object[] comps = null; try { comps = asmDoc.GetComponents(true) as object[]; } catch { }
                Component2 best = null; List<Body2> bestBodies = null;
                foreach (var o in comps ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                    if (sup) continue;
                    object bi;
                    object[] cb = null; try { cb = c.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[]; } catch { }
                    var cbList = (cb ?? new object[0]).OfType<Body2>().ToList();
                    if (cbList.Count > 1 && cbList.Count > (bestBodies?.Count ?? 1))
                    { best = c; bestBodies = cbList; }
                }
                if (best != null) { bodies = bestBodies; try { sourceName = best.Name2; } catch { } }
            }

            res.Applicable = true;
            res.TotalBodies = bodies.Count;
            res.SourceComponent = sourceName;

            if (bodies.Count == 0)
            {
                res.Info = "No multibody component found" + (sourceName != null ? " ('" + sourceName + "')" : "") +
                            " — Forge can't read a capacity number off this geometry; that would be a guess, not a measurement.";
                await emit("Tally", null, "done", "no multibody geometry found");
                return res;
            }

            var groups = GetCutList.GroupByShape(bodies);
            res.UniqueGroups = groups.Length;
            var top = groups.OrderByDescending(g => g.Quantity).First();
            res.MaxQuantity = top.Quantity;
            res.RepBody = top.Rep;

            if (top.Quantity <= 1)
            {
                res.Verified = false;
                res.Info = "No repeated identical body found" + (sourceName != null ? " on " + sourceName : "") +
                            " (" + res.TotalBodies + " bod" + (res.TotalBodies == 1 ? "y" : "ies") + ", all distinct shapes) — " +
                            "Forge can't read a capacity number off this geometry; that would be a guess, not a measurement.";
                await emit("Tally", null, "done", "no repeated-body group found");
                return res;
            }

            res.Verified = true;
            res.Info = "Up to " + top.Quantity + " identical part" + (top.Quantity == 1 ? "" : "s") + " at a time — " +
                        top.Quantity + " matching '" + top.Rep + "'-shaped bodies" + (sourceName != null ? " in " + sourceName : "") +
                        " (" + res.UniqueGroups + " unique body shape" + (res.UniqueGroups == 1 ? "" : "s") + ", " + res.TotalBodies + " bodies total).";
            await emit("Tally", null, "done", top.Quantity + "x '" + top.Rep + "' — capacity " + top.Quantity);
            return res;
        }
    }
}
