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
    public class WhereUsedRow
    {
        public string Name;
        public string Path;
        public string Kind;    // "assembly" | "drawing"
    }

    public class FindWhereUsedResult
    {
        public string TargetPath;
        public string SearchedFolder;
        public int ScannedFiles;      // candidates examined
        public int Unreadable;        // candidates whose reference list could NOT be read — reported, never assumed clean
        public int ParentAssemblies;
        public int ParentDrawings;
        public List<WhereUsedRow> Parents = new List<WhereUsedRow>();
        public List<string> UnreadableFiles = new List<string>();
        public bool ReadOnly = true;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// FindWhereUsed (tool #131 find_where_used) — the reverse of get_file_references: every assembly and drawing that
    /// USES this part. The question before any edit that matters ("if I change this bracket, what else moves?") and the
    /// one nobody can answer from inside the part.
    ///
    ///   Scout — scan the document's folder for .SLDASM/.SLDDRW candidates and ask each one for its reference list via
    ///           ISldWorks.GetDocumentDependencies2. Nothing is OPENED: this is a file-level query, so a folder of
    ///           broken or huge assemblies costs nothing and can't pop a dialog.
    ///   Honesty— a candidate whose reference list can't be read is counted as UNREADABLE and named, never folded into
    ///           "doesn't use it" (Character #1: a silent false negative here is how someone edits a part that a job on
    ///           the floor depends on).
    ///
    /// Scope is the containing folder, and the answer says so — a whole-vault search is a different (indexed) tool.
    /// READ-ONLY: nothing is opened, changed or saved.
    /// </summary>
    public static class FindWhereUsed
    {
        // NARROW: the reverse-lookup question. Requires "where used"/"who uses"/"what uses"/"which assemblies use"
        // phrasing, so it can never take get_file_references' forward question ("what files does this reference").
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(ghost|haunted|stale|orphan(ed)?|dangling)\b")) return false;
            if (Regex.IsMatch(c, @"\bwhere[- ]used\b")) return true;
            // BUG FIX (2026-07-29, found by regression sweep): the old "who" regex didn't check word ORDER, so
            // get_file_references' own forward phrasing "what files does THIS ASSEMBLY reference" matched too
            // ("what" ... "reference" within 40 chars) despite the comment above claiming otherwise — "this/it"
            // sits BEFORE the verb there (a SUBJECT construction: THIS document does the referencing), whereas a
            // genuine where-used reverse lookup always has "this/it" AFTER the verb (an OBJECT construction: some
            // OTHER document uses/references THIS one). Bail on the subject form explicitly.
            if (Regex.IsMatch(c, @"\bdoes\s+(this|it)\b")) return false;
            bool who = Regex.IsMatch(c, @"\b(who|what|which)\b.{0,40}\b(use|uses|using|reference|references|contain|contains|include|includes)\b");
            bool scope = Regex.IsMatch(c, @"\b(assembly|assemblies|drawing|drawings|parent|parents|file|files|part|this)\b");
            return who && scope;
        }

        public static async Task<FindWhereUsedResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new FindWhereUsedResult();
            if (model == null) { res.Error = "Open the part you want the where-used list for."; return res; }

            string target = null; try { target = model.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(target))
            { res.Error = "This document has never been saved, so nothing can reference it yet."; return res; }
            res.TargetPath = target;

            string folder = null; try { folder = Path.GetDirectoryName(target); } catch { }
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            { res.Error = "Couldn't read the folder this document lives in."; return res; }
            res.SearchedFolder = folder;

            await emit("Scout", "scanning " + Path.GetFileName(folder) + " for assemblies and drawings that use " + Path.GetFileName(target), "run", null);

            var candidates = new List<string>();
            try
            {
                candidates.AddRange(Directory.GetFiles(folder, "*.SLDASM"));
                candidates.AddRange(Directory.GetFiles(folder, "*.SLDDRW"));
            }
            catch (Exception ex) { res.Error = "Couldn't list the folder (" + ex.GetType().Name + ")."; await emit("Scout", null, "fail", res.Error); return res; }

            foreach (var cand in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(cand, target, StringComparison.OrdinalIgnoreCase)) continue;
                res.ScannedFiles++;
                object[] deps = null;
                try { deps = app.GetDocumentDependencies2(cand, true, true, false) as object[]; }
                catch { deps = null; }
                if (deps == null) { res.Unreadable++; res.UnreadableFiles.Add(Path.GetFileName(cand)); continue; }

                bool uses = false;
                for (int k = 0; k + 1 < deps.Length; k += 2)
                {
                    string p = deps[k + 1] as string;
                    if (!string.IsNullOrEmpty(p) && string.Equals(p, target, StringComparison.OrdinalIgnoreCase)) { uses = true; break; }
                }
                if (!uses) continue;

                string kind = cand.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase) ? "drawing" : "assembly";
                res.Parents.Add(new WhereUsedRow { Name = Path.GetFileName(cand), Path = cand, Kind = kind });
                if (kind == "drawing") res.ParentDrawings++; else res.ParentAssemblies++;
            }

            await emit("Scout", null, res.Unreadable == 0 ? "done" : "fail",
                res.Parents.Count + " parent(s) among " + res.ScannedFiles + " scanned" +
                (res.Unreadable > 0 ? " · " + res.Unreadable + " could NOT be read" : ""));

            string nm = Path.GetFileName(target);
            res.Info = res.Parents.Count == 0
                ? "Nothing in this folder uses " + nm + " (" + res.ScannedFiles + " assemblies/drawings checked)."
                : nm + " is used by " + res.ParentAssemblies + " assembly(ies)" +
                  (res.ParentDrawings > 0 ? " and " + res.ParentDrawings + " drawing(s)" : "") + ": " +
                  string.Join(", ", res.Parents.Select(p => p.Name)) + ". Searched this folder only (" + res.ScannedFiles + " files)." +
                  (res.Unreadable > 0 ? " " + res.Unreadable + " file(s) couldn't be read — treat the list as incomplete: " + string.Join(", ", res.UnreadableFiles) + "." : "");
            return res;
        }
    }
}
