using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class LocalizedNameEntry
    {
        public string Name;      // the display name (as authored, possibly localized)
        public string Type;      // the resolved feature TYPE (GetTypeName2) — language-independent
        public bool Localized;   // display name carries a non-ASCII (localized) character
    }

    public class ResolveLocalizedNamesResult
    {
        public bool IsPart;
        public int TotalFeatures;        // real features considered (folders/scaffold excluded)
        public int LocalizedCount;       // features whose display name is non-ASCII (localized)
        public LocalizedNameEntry[] LocalizedFeatures;  // the localized ones, with their TRUE type
        public string Verdict;           // "localized" | "clean"
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 245 — resolve_localized_names (READ). Feature trees from global vendors arrive in German / Japanese / French
    /// ("Verrundung1" = Fillet1). Operating on a localized DISPLAY name is fragile — the name lies; the TYPE is the truth.
    /// This resolves every feature to its language-independent TYPE (IFeature.GetTypeName2, a type ID) and flags the
    /// features whose display name is non-ASCII (localized), so any downstream op can target by TYPE, never by the name.
    /// Read-only, offer-don't-touch. The INDEPENDENT GT recounts the non-ASCII names by regex and takes its own type
    /// census, and the known-truth fixture anchors the answer (a base extrude renamed to a German fillet-looking name
    /// still resolves to type Extrusion — proving the name is ignored). Part-only (feature trees live in parts).
    /// </summary>
    public static class ResolveLocalizedNames
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // Requires a LOCALIZATION signal so it never shadows list_features / find_feature_by_name / find_features_by_type.
            bool loc = Regex.IsMatch(c, @"\b(localor?i[sz]ed|localis|non[\s-]?english|foreign[\s-]?language|foreign\s+names?|another\s+language|other\s+language|different\s+language|german|japanese|french|chinese|mojibake|non[\s-]?ascii|unicode\s+names?)\b");
            // "resolve … by type / real type despite the name / names are in <lang>" — the type-not-name idea, feature-scoped.
            bool typeNotName = Regex.IsMatch(c, @"\b(real|true|actual)\s+type\b") && Regex.IsMatch(c, @"\bname");
            bool resolveByType = Regex.IsMatch(c, @"\bresolve\b") && Regex.IsMatch(c, @"\b(feature|name)s?\b") && Regex.IsMatch(c, @"\btype\b");
            if (!(loc || typeNotName || resolveByType)) return false;
            // must be about features/names/tree so a stray "german" chat doesn't trip it
            return Regex.IsMatch(c, @"\b(feature|features|name|names|tree|fillet|extrude|cut|type|types)\b");
        }

        public static async Task<ResolveLocalizedNamesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ResolveLocalizedNamesResult();
            if (model == null) { res.Error = "Open a part to resolve its feature names by type."; return res; }
            if (!(model is PartDoc)) { res.Error = "Localized-name resolution is a part check - open the part, not the assembly."; return res; }
            res.IsPart = true;

            await emit("Scout", "resolving feature names by type", "run", null);

            var localized = new List<LocalizedNameEntry>();
            int total = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (!string.IsNullOrEmpty(tn) && IsRealFeature(tn))
                {
                    string n = null; try { n = f.Name; } catch { }
                    total++;
                    if (!string.IsNullOrEmpty(n) && HasNonAscii(n))
                        localized.Add(new LocalizedNameEntry { Name = n, Type = tn, Localized = true });
                }
                f = f.GetNextFeature() as Feature;
            }

            res.TotalFeatures = total;
            res.LocalizedCount = localized.Count;
            res.LocalizedFeatures = localized.ToArray();
            res.Verdict = localized.Count > 0 ? "localized" : "clean";

            var diag = new StringBuilder("verdict=" + res.Verdict + " features=" + total + " localized=" + res.LocalizedCount);
            foreach (var e in localized) diag.Append(" | " + e.Name + " -> type " + e.Type);
            res.Diag = diag.ToString();

            await emit("Scout", null, "done",
                localized.Count == 0
                    ? ("all " + total + " feature names are standard - each resolves to its type directly")
                    : (res.LocalizedCount + " localized name" + (res.LocalizedCount == 1 ? "" : "s") + " - resolved by type: " +
                       string.Join("; ", localized.Select(e => e.Name + " is a " + FriendlyType(e.Type)))));

            res.Info = BuildInfo(res, localized);
            return res;
        }

        private static bool HasNonAscii(string s)
        {
            foreach (char ch in s) if (ch > 127) return true;
            return false;
        }

        // a real feature, not a container folder / origin scaffold / the datum planes
        private static bool IsRealFeature(string tn)
        {
            if (tn.EndsWith("Folder", StringComparison.OrdinalIgnoreCase)) return false;
            switch (tn)
            {
                case "RefPlane": case "RefAxis": case "OriginProfileFeature": case "CoordSys":
                case "DetailCabinet": case "HistoryFolder": return false;
                default: return true;
            }
        }

        // human-friendly label for a SolidWorks type ID (report only — never used for matching)
        private static string FriendlyType(string tn)
        {
            switch (tn)
            {
                case "Extrusion": return "boss/extrude";
                case "ICE": return "cut";
                case "LPattern": return "linear pattern";
                case "Fillet": return "fillet";
                case "Chamfer": return "chamfer";
                case "ProfileFeature": return "sketch";
                default: return tn;
            }
        }

        private static string BuildInfo(ResolveLocalizedNamesResult r, List<LocalizedNameEntry> localized)
        {
            if (r.LocalizedCount == 0)
                return "No localized feature names - all " + r.TotalFeatures + " features carry standard names, each resolving to its type directly.";
            var sb = new StringBuilder();
            sb.Append(r.LocalizedCount + " feature name" + (r.LocalizedCount == 1 ? " is" : "s are") + " localized (non-English) - Forge resolves them by TYPE, not by the display name:");
            foreach (var e in localized)
                sb.Append("\n  " + e.Name + " -> type " + e.Type + " (" + FriendlyType(e.Type) + ")");
            sb.Append("\nAny operation targets the resolved type, so the language of the name doesn't matter.");
            return sb.ToString();
        }
    }
}
