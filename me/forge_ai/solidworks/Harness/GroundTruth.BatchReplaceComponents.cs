using System;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT per-instance kind+file census for batch_replace_components (tool 164). Shares no code with the
        // handler: re-reads every non-suppressed top-level component's Name2/GetPathName straight from the tree and
        // classifies its kind with its own bolt/plate heuristic. The harness diffs run0 (baseline, each kind on its
        // ORIGINAL file) against run1 (handler swapped >=2 distinct kinds to >=2 distinct replacement files) and
        // proves every kind's population moved, independently, to its own target — it does not know any from/to
        // file so it can never agree by construction.
        public static JObject MeasureBatchReplaceComponents(IModelDoc2 model)
        {
            var mo = new JObject();
            var asm = model as AssemblyDoc;
            var arr = new JArray();
            var byKindFile = new JObject();
            if (asm == null) { mo["error"] = "active doc is not an assembly"; mo["components"] = arr; mo["byKindFile"] = byKindFile; return mo; }

            int total = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                string path = null; try { path = c.GetPathName(); } catch { }
                string file = string.IsNullOrEmpty(path) ? "(none)" : Path.GetFileName(path).ToLowerInvariant();
                string kind = BrcKind(nm, file);
                total++;
                arr.Add(new JObject { ["name"] = nm ?? "", ["file"] = file, ["kind"] = kind });
                if (byKindFile[kind] == null) byKindFile[kind] = new JObject();
                var kf = (JObject)byKindFile[kind];
                kf[file] = (kf[file] != null ? (int)kf[file] : 0) + 1;
            }

            mo["total"] = total;
            mo["components"] = arr;      // per-instance name/file/kind
            mo["byKindFile"] = byKindFile; // kind -> file basename -> count
            return mo;
        }

        // kind classifier by instance name OR file name, generalizing RcBolt (tool 31) with a plate case (164 also
        // swaps the plate, which RcBolt only ever excluded, never named).
        private static string BrcKind(string name, string file)
        {
            string n = ((name ?? "") + " " + (file ?? "")).ToLowerInvariant();
            if (n.Contains("plate")) return "plate";
            if (n.Contains("nut")) return "nut";
            if (n.Contains("washer")) return "washer";
            foreach (var h in new[] { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud" })
                if (n.Contains(h)) return "bolt";
            return "other";
        }
    }
}
