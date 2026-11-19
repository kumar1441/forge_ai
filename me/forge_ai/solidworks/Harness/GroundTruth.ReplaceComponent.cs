using System;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT per-instance part-FILE census for replace_component (tool 31). Shares no code with the handler: it
        // re-reads every non-suppressed top-level component's GetPathName straight from the tree, reduces to the file's
        // base name, and flags bolts with its own classifier. The harness diffs run0 (baseline, bolts on the ORIGINAL file)
        // against run1 (handler swapped them to the REPLACEMENT file) and proves the NON-target components (the plate) kept
        // their file; run2 is idempotent. It does not know the from/to file, so it can never agree by construction.
        public static JObject MeasureReplaceComponent(IModelDoc2 model)
        {
            var mo = new JObject();
            var asm = model as AssemblyDoc;
            var arr = new JArray();
            var byFile = new JObject();
            if (asm == null) { mo["error"] = "active doc is not an assembly"; mo["components"] = arr; mo["byFile"] = byFile; return mo; }

            int total = 0, bolts = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                string path = null; try { path = c.GetPathName(); } catch { }
                string file = string.IsNullOrEmpty(path) ? "(none)" : Path.GetFileName(path).ToLowerInvariant();
                bool isBolt = RcBolt(nm, file);
                total++; if (isBolt) bolts++;
                arr.Add(new JObject { ["name"] = nm ?? "", ["file"] = file, ["isBolt"] = isBolt });
                byFile[file] = (byFile[file] != null ? (int)byFile[file] : 0) + 1;
            }

            mo["total"] = total;
            mo["boltCount"] = bolts;
            mo["components"] = arr;   // per-instance name/file/isBolt for the swap + scoping proof
            mo["byFile"] = byFile;    // file basename -> count
            return mo;
        }

        // bolt classifier by instance name OR file name (the replacement file is also a "...-bolt.SLDPRT")
        private static bool RcBolt(string name, string file)
        {
            string n = ((name ?? "") + " " + (file ?? "")).ToLowerInvariant();
            if (n.Contains("nut") || n.Contains("washer") || n.Contains("plate")) return false;
            foreach (var h in new[] { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud" })
                if (n.Contains(h)) return true;
            return false;
        }
    }
}
