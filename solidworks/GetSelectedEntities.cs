using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class SelectedEntityInfo
    {
        public string Type;   // "Face" | "Edge" | "Component" | "Plane" | "Feature" | "Vertex" | "Unknown"
        public string Name;
        public double AreaMm2 = -1;
        public double LengthMm = -1;
    }

    public class GetSelectedEntitiesResult
    {
        public bool Success;
        public int Count;
        public List<SelectedEntityInfo> Items = new List<SelectedEntityInfo>();
        public string PreSelectCriterion;   // set only when an embedded "select the X face" sub-command ran first
        public double PreSelectAreaMm2 = -1;
        public double[] PreSelectNormal;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// GetSelectedEntities (tool 16) — READ: reports what is currently selected in the live SolidWorks session,
    /// honestly — including "nothing is selected" when that's true. Never fabricates a selection just to have
    /// something to report; that would misinform a real user who genuinely selected nothing.
    ///
    /// Supports one optional EMBEDDED pre-selection sub-command ("select the largest face, then tell me what's
    /// selected") — a plausible compound user command, and the ONLY way this tool can be proven against a REAL
    /// non-empty selection in the automated harness: the harness's GroundTruth.Measure calls ForceRebuild3
    /// immediately after every handler run, which drops any live selection as a side effect (same non-negotiable
    /// established by select_face/edge/plane/component — see their GT doc comments). Doing the optional arrange
    /// step and the read in the SAME synchronous call sidesteps that entirely; a plain "what's selected?" with no
    /// embedded sub-command never arranges anything and just reports the live state as-is.
    /// </summary>
    public static class GetSelectedEntities
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(what|show|list|get|tell)\b.*\bselect(ed|ion)?\b")) return true;
            if (Regex.IsMatch(c, @"\bselect(ed|ion)?\b.*\b(entities|entity|items|objects)\b")) return true;
            return false;
        }

        public static async Task<GetSelectedEntitiesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetSelectedEntitiesResult();
            if (model == null) { res.Error = "Open a part or assembly first."; return res; }

            await emit("Selector", "reading current selection", "run", null);

            string crit = ParsePreSelectCriterion(intent);
            if (crit != null)
            {
                var sf = await SelectFace.Run(app, model, "select the " + crit + " face", (a, b, c2, d) => Task.CompletedTask);
                if (!sf.Success)
                {
                    res.Error = "Couldn't pre-select the " + crit + " face: " + sf.Error;
                    await emit("Selector", null, "fail", res.Error);
                    return res;
                }
                res.PreSelectCriterion = crit;
                res.PreSelectAreaMm2 = sf.AreaMm2;
                res.PreSelectNormal = sf.Normal;
            }

            var sm = model.SelectionManager as SelectionMgr;
            int count = 0;
            try { count = sm.GetSelectedObjectCount2(-1); }
            catch (Exception ex) { res.Error = "Couldn't read the selection manager: " + ex.Message; return res; }
            res.Count = count;

            for (int i = 1; i <= count; i++)
            {
                object o = null; try { o = sm.GetSelectedObject6(i, -1); } catch { }
                if (o == null) continue;
                res.Items.Add(Describe(o));
            }

            res.Success = true;
            res.Info = count == 0 ? "Nothing is currently selected." : count + " item(s) currently selected: " + Summarize(res.Items);
            await emit("Selector", null, "done", res.Info);
            return res;
        }

        private static SelectedEntityInfo Describe(object o)
        {
            var info = new SelectedEntityInfo();
            var face = o as Face2;
            if (face != null)
            {
                info.Type = "Face"; info.Name = "Face";
                try { info.AreaMm2 = face.GetArea() * 1e6; } catch { }
                return info;
            }
            var edge = o as Edge;
            if (edge != null)
            {
                info.Type = "Edge"; info.Name = "Edge";
                try { info.LengthMm = SelectEdge.EdgeLengthMm(edge); } catch { }
                return info;
            }
            var comp = o as Component2;
            if (comp != null) { info.Type = "Component"; try { info.Name = comp.Name2; } catch { } return info; }
            var feat = o as Feature;
            if (feat != null)
            {
                string tn = null; try { tn = feat.GetTypeName2(); } catch { }
                info.Type = tn == "RefPlane" ? "Plane" : "Feature";
                try { info.Name = feat.Name; } catch { }
                return info;
            }
            var vert = o as Vertex;
            if (vert != null) { info.Type = "Vertex"; return info; }
            info.Type = "Unknown";
            return info;
        }

        private static string Summarize(List<SelectedEntityInfo> items)
        {
            var groups = new Dictionary<string, int>();
            foreach (var it in items)
                groups[it.Type] = groups.TryGetValue(it.Type, out int n) ? n + 1 : 1;
            var parts = new List<string>();
            foreach (var kv in groups) parts.Add(kv.Value + " " + kv.Key);
            return string.Join(", ", parts);
        }

        private static string ParsePreSelectCriterion(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bselect\b") || !Regex.IsMatch(c, @"\bface\b")) return null;
            if (Regex.IsMatch(c, @"\b(largest|biggest)\b")) return "largest";
            if (Regex.IsMatch(c, @"\btop\b")) return "top";
            if (Regex.IsMatch(c, @"\bbottom\b")) return "bottom";
            if (Regex.IsMatch(c, @"\bleft\b")) return "left";
            if (Regex.IsMatch(c, @"\bright\b")) return "right";
            return null;
        }
    }
}
