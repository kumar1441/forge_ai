using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for resolve_localized_names (tool 245) — shares NO code with ResolveLocalizedNames.cs.
    /// The handler flags non-ASCII display names with a char-code loop and reports each one's type. This GT recounts the
    /// localized names a DIFFERENT way (a [^\x00-\x7F] regex over the tree) and takes its own type census, so a
    /// disagreement exposes a bad walk. The KNOWN TRUTH anchors it: localized-names-block has exactly ONE localized name
    /// (the base extrude renamed to a German fillet-looking name) whose true TYPE is Extrusion — proving the name is
    /// ignored. props-block (clean) has 0 localized names. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        private static readonly Regex NonAsciiRx = new Regex("[^\\x00-\\x7F]", RegexOptions.Compiled);

        public static JObject MeasureResolveLocalizedNames(IModelDoc2 model)
        {
            var d = new JObject();
            if (!(model is PartDoc)) { d["applicable"] = false; d["reason"] = "not a part"; return d; }
            d["applicable"] = true;

            int total = 0, localized = 0;
            string firstLocalizedType = null;
            var names = new JArray();

            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (!string.IsNullOrEmpty(tn) && RealFeature(tn))
                {
                    string n = null; try { n = f.Name; } catch { }
                    total++;
                    if (!string.IsNullOrEmpty(n) && NonAsciiRx.IsMatch(n))
                    {
                        localized++;
                        if (firstLocalizedType == null) firstLocalizedType = tn;
                        names.Add(new JObject { ["name"] = n, ["type"] = tn });
                    }
                }
                f = f.GetNextFeature() as Feature;
            }

            d["totalFeatures"] = total;
            d["localizedCount"] = localized;
            d["firstLocalizedType"] = firstLocalizedType;   // KNOWN TRUTH: Extrusion on the localized fixture
            d["expectedVerdict"] = localized > 0 ? "localized" : "clean";
            d["localizedNames"] = names;
            return d;
        }

        private static bool RealFeature(string tn)
        {
            if (tn.EndsWith("Folder", StringComparison.OrdinalIgnoreCase)) return false;
            switch (tn)
            {
                case "RefPlane": case "RefAxis": case "OriginProfileFeature": case "CoordSys":
                case "DetailCabinet": case "HistoryFolder": return false;
                default: return true;
            }
        }
    }
}
