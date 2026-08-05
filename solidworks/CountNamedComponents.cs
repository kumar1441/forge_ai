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
    public class CountNamedComponentsResult
    {
        public int Count;
        public int Suppressed;
        public bool Verified;
        public List<string> Keywords = new List<string>();
        public List<string> MatchedNames = new List<string>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// READ: "how many servos are in the robot", "count the rollers in this bearing", "total number of
    /// tapered rollers in this setup" — a named-part-type count question the cloud parser routes to a
    /// generic fallback (list_bodies/list_features/get_bounding_box) for lack of a specific action, same
    /// gap MeasureBoltCircle closed for hole counts. Extracts the noun phrase between the count-word and
    /// "in/on/inside" straight from the user's own words (no hardcoded per-model vocabulary), normalizes
    /// away spaces/hyphens, and substring-matches every assembly component's Name2 or backing file name.
    /// Explicitly NOT holes/patterns/features/faces/edges/bodies/mates — those already have their own
    /// authoritative handler and must keep routing there. Never writes.
    /// </summary>
    public static class CountNamedComponents
    {
        private static readonly Regex CountPhrase = new Regex(
            @"(?:how many(?:\s+of\s+(?:those|these))?|count(?:\s+(?:the|all|of))?|(?:total\s+)?number\s+of)\s+(.+?)\s*(?:are|is|go|goes)?\s*(?:there\s+)?(?:in|on|inside|within)\b",
            RegexOptions.IgnoreCase);

        private static readonly HashSet<string> Stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "those","these","the","a","an","of","that","this","things","thing","stuff","item","items",
            "component","components","part","parts","piece","pieces","unit","units","total","setup",
            "assembly","model","here","there","and","or"
        };

        // Own capability territory already covered by a specific handler — never shadow it.
        private static readonly Regex Excluded = new Regex(
            @"\b(hole|holes|pattern|patterns|feature|features|tooth|teeth|face|faces|edge|edges|body|bodies|mate|mates|config|configs|configuration|configurations)\b",
            RegexOptions.IgnoreCase);

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            if (Excluded.IsMatch(cmd)) return false;
            return ExtractKeywords(cmd).Count > 0;
        }

        public static List<string> ExtractKeywords(string cmd)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(cmd)) return result;
            var m = CountPhrase.Match(cmd);
            if (!m.Success) return result;
            var phrase = m.Groups[1].Value.Trim();
            foreach (var raw in phrase.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var w = raw.Trim();
                if (w.Length < 3 || Stopwords.Contains(w)) continue;
                if (w.EndsWith("s", StringComparison.OrdinalIgnoreCase) && w.Length > 4 && !w.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
                    w = w.Substring(0, w.Length - 1);
                result.Add(w);
            }
            return result;
        }

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");
        }

        public static async Task<CountNamedComponentsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CountNamedComponentsResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            { res.Error = "Open an assembly to count its components."; return res; }

            var keywords = ExtractKeywords(intent);
            res.Keywords = keywords;
            if (keywords.Count == 0) { res.Error = "Couldn't tell what to count from that."; return res; }
            var normKeywords = keywords.Select(Normalize).Where(k => k.Length > 0).ToList();

            await emit("Scribe", "counting matching components", "run", null);
            var asm = model as AssemblyDoc;
            Component2 soleMatch = null;
            foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                string name = null; try { name = c.Name2; } catch { }
                string file = null;
                try { var p = c.GetPathName(); file = string.IsNullOrEmpty(p) ? null : System.IO.Path.GetFileNameWithoutExtension(p); } catch { }
                var nName = Normalize(name);
                var nFile = Normalize(file);
                bool isMatch = normKeywords.Any(k => (nName.Length > 0 && nName.Contains(k)) || (nFile.Length > 0 && nFile.Contains(k)));
                if (!isMatch) continue;

                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) { res.Suppressed++; continue; }
                res.Count++;
                res.MatchedNames.Add(name);
                soleMatch = c;
            }

            res.Verified = true;
            await emit("Scribe", null, "done", res.Count + " match(es)");

            var kwLabel = string.Join(" / ", keywords);
            if (res.Count == 0 && res.Suppressed == 0)
            {
                res.Info = "No components matching \"" + kwLabel + "\" found in this assembly.";
            }
            else
            {
                var sb = new StringBuilder(res.Count + " component" + (res.Count == 1 ? "" : "s") + " matching \"" + kwLabel + "\"" +
                                            (res.Suppressed > 0 ? " (" + res.Suppressed + " more suppressed)" : "") + ":");
                foreach (var n in res.MatchedNames.Take(20)) sb.Append("\n• " + n);
                if (res.MatchedNames.Count > 20) sb.Append("\n… (" + (res.MatchedNames.Count - 20) + " more)");

                // A single matching COMPONENT that bundles many BODIES (e.g. a bearing race modeled as one part
                // with every roller as a separate solid body) means "1" would read as "there is 1 roller" — an
                // honest count needs to say the items aren't separate assembly components at all.
                if (res.Count == 1 && soleMatch != null)
                {
                    int bodyCount = 0;
                    try { object bi; bodyCount = (soleMatch.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[])?.Length ?? 0; } catch { }
                    if (bodyCount > 2)
                        sb.Append("\nNote: this is ONE assembly component that bundles " + bodyCount + " solid bodies — the individual " +
                                  kwLabel + " items look like they're modeled as bodies inside this part, not separate components, " +
                                  "so Forge can't give an exact per-item count from the assembly tree alone.");
                }
                res.Info = sb.ToString();
            }
            return res;
        }
    }
}
