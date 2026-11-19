using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class HandleRollbackBarResult
    {
        public bool BarIsSet;            // true = the rollback bar sits mid-tree (some feature is rolled back / not built)
        public int RolledBackCount;      // number of rolled-back features (excluding the empty container folders)
        public string BarPosition;       // name of the FIRST rolled-back feature — the feature the bar sits before
        public List<string> FeaturesBelow = new List<string>(); // names of every feature below the bar (not built)
        public int TotalFeatures;        // real features walked (excluding container folders)
        public string Verdict;           // "complete" (bar at end) | "incomplete" (bar mid-tree)
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 240 — handle_rollback_bar (READ). A file saved with the rollback bar mid-tree is a real hazard: the tree
    /// LOOKS complete but the features below the bar are NOT built, so any op that assumes the whole tree exists is
    /// operating on a lie (a mirror/pattern/export silently misses the un-built features). This detects that state
    /// BEFORE any write: walk the feature tree, ask each feature IFeature.IsRolledBack(), and report what sits below
    /// the bar. Read-only — it NEVER moves the bar. The 11 empty container folders (per the tree-walk landmine) are
    /// skipped so they don't inflate the count. The independent GT cross-checks with its own IsRolledBack walk PLUS a
    /// geometry signal (a rolled-back hole cut leaves one fewer cylindrical bore than a fully-built tree).
    /// </summary>
    public static class HandleRollbackBar
    {
        // The 11 empty container folders FirstFeature/GetNextFeature walks (tree-walk landmine) — never real features.
        private static readonly HashSet<string> Folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CommentsFolder","FavoriteFolder","SelectionSetFolder","SensorSuite","SensorFolder","DocsFolder",
            "SurfaceBodyFolder","SolidBodyFolder","EnvFolder","InkMarkupFolder","EqnFolder","MaterialFolder"
        };

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // The distinguishing noun is the rollback bar itself. Requiring "roll ?back" (or an explicit "below the bar"
            // / "tree complete" phrasing paired with a bar/feature noun) keeps it off detect_file_health (health/preflight)
            // and get_rebuild_errors (error list).
            if (Regex.IsMatch(c, @"\broll\s?back\b")) return true;
            if (Regex.IsMatch(c, @"\bbelow the (rollback )?bar\b")) return true;
            if (Regex.IsMatch(c, @"\bis (the|this) (feature )?tree (complete|fully built|all built)\b")) return true;
            return false;
        }

        public static async Task<HandleRollbackBarResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new HandleRollbackBarResult();
            if (model == null) { res.Error = "Open a part or assembly to check its rollback bar."; return res; }

            await emit("Sentinel", "checking the rollback bar", "run", null);

            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (string.IsNullOrEmpty(tn) || !Folders.Contains(tn))
                    {
                        res.TotalFeatures++;
                        bool rolled = false; try { rolled = f.IsRolledBack(); } catch { }
                        if (rolled)
                        {
                            res.RolledBackCount++;
                            string nm = null; try { nm = f.Name; } catch { }
                            if (!string.IsNullOrEmpty(nm)) res.FeaturesBelow.Add(nm);
                            if (res.BarPosition == null) res.BarPosition = nm;
                        }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch (Exception ex) { res.Diag = "tree walk threw: " + ex.Message; }

            res.BarIsSet = res.RolledBackCount > 0;
            res.Verdict = res.BarIsSet ? "incomplete" : "complete";
            res.Diag = "barIsSet=" + res.BarIsSet + " rolledBack=" + res.RolledBackCount +
                       " total=" + res.TotalFeatures + " barPos=" + (res.BarPosition ?? "-") + (res.Diag != null ? " | " + res.Diag : "");

            await emit("Sentinel", null, "done",
                res.BarIsSet ? ("rollback bar is set - " + res.RolledBackCount + " feature(s) below it") : "tree fully built (bar at end)");

            res.Info = BuildInfo(res);
            return res;
        }

        private static string BuildInfo(HandleRollbackBarResult r)
        {
            if (!r.BarIsSet)
                return "Tree fully built - the rollback bar is at the end, all " + r.TotalFeatures + " features are built. Safe to operate on the whole tree.";
            var sb = new StringBuilder();
            sb.Append("Rollback bar is set mid-tree - " + r.RolledBackCount + " feature(s) below it are NOT built: " +
                      string.Join(", ", r.FeaturesBelow) + ".");
            sb.Append("\nThe tree is incomplete. Do not mirror/pattern/export as if these features exist - move the bar to the end first.");
            return sb.ToString();
        }
    }
}
