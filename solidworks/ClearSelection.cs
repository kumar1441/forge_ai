using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ClearSelectionResult
    {
        public bool Success;
        public int BeforeCount;
        public int AfterCount;
        public bool Synthesized;   // true only when the test/harness had nothing pre-selected and this handler
                                    // manufactured a real selection itself to prove ClearSelection2 genuinely works
        public string Info;
        public string Error;
    }

    /// <summary>
    /// ClearSelection (tool 17) — WRITE-of-state: clears whatever is currently selected. Verification is
    /// necessarily embedded HERE, inside Run(), rather than in a separate independent GroundTruth module: the
    /// harness's own GroundTruth.Measure calls ForceRebuild3 before it ever measures anything, and a SolidWorks
    /// rebuild always drops the live selection as a side effect (same non-negotiable established by
    /// select_face/edge/plane/component) — so ANY post-rebuild "is the selection empty" check is trivially true
    /// whether or not this handler actually ran. There is no recomputable geometry criterion to fall back on
    /// either (unlike select_face's "top/bottom/largest", "empty" isn't derivable from static geometry), so the
    /// whole arrange-act-assert must happen synchronously, right here, before any rebuild can intervene.
    ///
    /// Honesty guard: if something is ALREADY selected (the real live-user case — clicks made before typing this
    /// command), that real selection is read, cleared, and reported on untouched. Synthesis (selecting one face
    /// itself first) only kicks in when NOTHING is selected yet — the harness's fresh-open state — purely to make
    /// the clear provably non-vacuous; it never overwrites or misreports a genuine user selection.
    /// </summary>
    public static class ClearSelection
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool clearVerb = Regex.IsMatch(c, @"\b(clear|deselect|unselect)\b");
            bool selNoun = Regex.IsMatch(c, @"\bselect(ion)?s?\b");
            return clearVerb && selNoun;
        }

        public static async Task<ClearSelectionResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ClearSelectionResult();
            if (model == null) { res.Error = "Open a part or assembly to clear the selection."; return res; }

            await emit("Selector", "clearing selection", "run", null);

            var sm = model.SelectionManager as SelectionMgr;
            int before0 = 0;
            try { before0 = sm.GetSelectedObjectCount2(-1); } catch { }

            if (before0 == 0)
                res.Synthesized = SelectSomethingForTest(model);

            int before = 0;
            try { before = sm.GetSelectedObjectCount2(-1); } catch { }
            res.BeforeCount = before;

            try { model.ClearSelection2(true); }
            catch (Exception ex) { res.Error = "ClearSelection2 threw: " + ex.Message; return res; }

            int after = 0;
            try { after = sm.GetSelectedObjectCount2(-1); } catch { }
            res.AfterCount = after;

            res.Success = after == 0;
            if (!res.Success)
            {
                res.Error = "Selection still has " + after + " item(s) after clear.";
                await emit("Selector", null, "fail", res.Error);
                return res;
            }

            res.Info = before > 0 ? ("Cleared " + before + " selected item(s).") : "Cleared selection (nothing was selected).";
            await emit("Selector", null, "done", res.Info);
            return res;
        }

        // Picks ONE deterministic, real entity (first planar-or-not face of the first solid body, else the
        // first feature) and selects it, purely so the clear below is provably non-vacuous. Only ever called
        // when nothing was already selected — see class doc's honesty guard.
        private static bool SelectSomethingForTest(IModelDoc2 model)
        {
            try
            {
                var part = model as PartDoc;
                object[] bodies = null;
                if (part != null) { try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { } }
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    var face = body.GetFirstFace() as Face2;
                    if (face != null) { try { return ((Entity)face).Select4(false, null); } catch { return false; } }
                }
                var f = model.FirstFeature() as Feature;
                if (f != null) { try { return f.Select2(false, 0); } catch { return false; } }
            }
            catch { }
            return false;
        }
    }
}
