using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class FileReferenceRow
    {
        public string Name;
        public string Path;
        public string Kind;     // "part" | "assembly" | "drawing" | "other"
        public bool Exists;     // the file is actually on disk RIGHT NOW
    }

    public class GetFileReferencesResult
    {
        public string RootPath;
        public int UniqueFiles;
        public int Parts;
        public int Assemblies;
        public int Missing;         // listed as a reference but not on disk — the pack-and-go / send-to-vendor killer
        public List<FileReferenceRow> Rows = new List<FileReferenceRow>();
        public bool ReadOnly = true;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// GetFileReferences (tool #130 get_file_references) — every file this document depends on, with each one checked
    /// against the disk. The pre-flight for pack-and-go, for sending a job to a vendor, and for "why does this open
    /// with errors on the other machine": a reference that resolves here and is missing there is the single most
    /// common way a released assembly arrives broken.
    ///
    ///   Gauge — ISldWorks.GetDocumentDependencies2 on the open document's path, traversing subassemblies. The API
    ///           returns alternating name/path entries; the root document itself is dropped from the list.
    ///   Sentinel — every returned path is checked with File.Exists. A path SolidWorks happily lists is not proof the
    ///           file is there (on this build a missing reference is only visible in OpenDoc6's return codes — see the
    ///           docs/SOLIDWORKS-GOTCHAS.md landmine — so the disk check is the honest signal, not the document's own health).
    ///
    /// READ-ONLY: nothing is opened, changed, rebuilt or saved.
    /// </summary>
    public static class GetFileReferences
    {
        // NARROW: needs FILE/document/reference vocabulary. Bails on the ghost/stale wording (tool 247 owns that) and
        // on feature/dimension/mate scopes (tools 153 / 70 own those).
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(ghost|haunted|stale|orphan(ed)?|dangling|leftover)\b")) return false;
            if (Regex.IsMatch(c, @"\b(locked|lock|read-?only|checked.?out|permission.?denied|in use)\b")) return false;
            // detect_in_context_writes (tool 242) owns "will an OPERATION touch/change other files" — a feature-
            // attributed risk warning, not a plain dependency listing.
            if (Regex.IsMatch(c, @"\bin[\s-]?context\b|\bexternal(ly)?[\s-]?referenc(e|ed|ing)\b|\bin[\s-]?place\b|\bripple\b|\bpropagat(e|es|ing)\b|\bside[\s-]?effect")) return false;
            if (Regex.IsMatch(c, @"\b(feature|features|dimension|dimensions|mate|mates|sketch|sketches|equation|equations)\b")) return false;
            bool noun = Regex.IsMatch(c, @"\b(file|files|document|documents|reference|references|referenced|dependency|dependencies|depends on)\b");
            bool verbish = Regex.IsMatch(c, @"\b(what|which|list|show|report|find|get|where)\b|\bdepend");
            return noun && verbish;
        }

        public static async Task<GetFileReferencesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetFileReferencesResult();
            if (model == null) { res.Error = "Open the document whose references you want to see."; return res; }

            string root = null; try { root = model.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(root))
            { res.Error = "This document has never been saved, so it has no path — references can only be listed for a file on disk."; return res; }
            res.RootPath = root;

            await emit("Gauge", "reading the reference list for " + Path.GetFileName(root), "run", null);

            object[] deps = null;
            try { deps = app.GetDocumentDependencies2(root, true, true, false) as object[]; }
            catch (Exception ex) { res.Error = "The dependency API failed (" + ex.GetType().Name + ") — no reference list to report."; await emit("Gauge", null, "fail", res.Error); return res; }
            if (deps == null)
            { res.Error = "GetDocumentDependencies2 returned nothing for this document — the reference list is unavailable on this build."; await emit("Gauge", null, "fail", res.Error); return res; }

            // the API returns alternating entries: name, path, name, path, …
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int k = 0; k + 1 < deps.Length; k += 2)
            {
                string nm = deps[k] as string;
                string pth = deps[k + 1] as string;
                if (string.IsNullOrWhiteSpace(pth)) continue;
                if (string.Equals(pth, root, StringComparison.OrdinalIgnoreCase)) continue;   // the document itself is not its own reference
                if (!seen.Add(pth)) continue;
                var row = new FileReferenceRow { Name = string.IsNullOrWhiteSpace(nm) ? Path.GetFileName(pth) : nm, Path = pth, Kind = KindOf(pth) };
                try { row.Exists = File.Exists(pth); } catch { row.Exists = false; }
                res.Rows.Add(row);
            }

            res.UniqueFiles = res.Rows.Count;
            res.Parts = res.Rows.Count(r => r.Kind == "part");
            res.Assemblies = res.Rows.Count(r => r.Kind == "assembly");
            res.Missing = res.Rows.Count(r => !r.Exists);

            await emit("Sentinel", "checking every referenced path against the disk", "run", null);
            await emit("Sentinel", null, res.Missing == 0 ? "done" : "fail",
                res.UniqueFiles + " referenced file" + (res.UniqueFiles == 1 ? "" : "s") +
                (res.Missing == 0 ? " — all present on disk" : " — " + res.Missing + " MISSING from disk"));

            res.Info = res.UniqueFiles == 0
                ? Path.GetFileName(root) + " references no other files."
                : Path.GetFileName(root) + " references " + res.UniqueFiles + " file" + (res.UniqueFiles == 1 ? "" : "s") +
                  " (" + res.Parts + " part" + (res.Parts == 1 ? "" : "s") + (res.Assemblies > 0 ? ", " + res.Assemblies + " subassembly(ies)" : "") + "). " +
                  (res.Missing == 0 ? "All present on disk." : res.Missing + " of them are NOT on disk — this will open with errors anywhere else.");
            return res;
        }

        private static string KindOf(string p)
        {
            string ext = null; try { ext = Path.GetExtension(p); } catch { }
            if (string.IsNullOrEmpty(ext)) return "other";
            ext = ext.ToLowerInvariant();
            if (ext == ".sldprt") return "part";
            if (ext == ".sldasm") return "assembly";
            if (ext == ".slddrw") return "drawing";
            return "other";
        }
    }
}
