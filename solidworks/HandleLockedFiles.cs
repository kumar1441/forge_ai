using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class LockedFileRow
    {
        public string Path;
        public string Status;   // "ok" | "read-only" | "locked" | "permission-denied" | "missing"
    }

    public class HandleLockedFilesResult
    {
        public bool Success;
        public int Checked;
        public int Blocked;     // count NOT "ok"
        public List<LockedFileRow> Rows = new List<LockedFileRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// HandleLockedFiles (tool 248, READ) — pre-flight lock check on the currently open document's OWN file.
    /// "Detect upfront, queue and report — never half-process a batch because file 60 was Bob's."
    ///
    /// EMPIRICALLY CONFIRMED LIVE (2026-07-31, not theorised): SolidWorks holds its OWN active document open
    /// with an exclusive OS-level file handle for the entire session — an exclusive-open probe (File.Open
    /// ReadWrite/FileShare.None or .Read) against `model.GetPathName()` ALWAYS throws IOException, even for a
    /// perfectly normal writable file, because SolidWorks itself is the lock holder. That makes an exclusive-
    /// open probe on the ACTIVE document a permanently-true false positive, not a real signal — a first harness
    /// run caught this immediately (a plain writable fixture reported "locked"), so it's coded around here
    /// rather than shipped broken.
    ///
    /// v1 scope, therefore: the OS READ-ONLY ATTRIBUTE only (File.GetAttributes) — the one signal that survives
    /// SW's own self-lock and still matches the tool's real-world case (a network mirror or checked-out copy
    /// flagged read-only). Genuine cross-process "someone else has this file open" detection would need to
    /// target a file THIS session hasn't already opened (e.g. an assembly's referenced-but-not-yet-loaded
    /// component) — not implemented in v1, honestly out of scope rather than guessed at a fixture that can't
    /// prove it. Batch/dependency-list scanning (every referenced component file — get_file_references'
    /// territory, tool 130) is the natural v2 extension.
    /// </summary>
    public static class HandleLockedFiles
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool lockWord = Regex.IsMatch(c, @"\b(locked|lock|read-?only|checked.?out|permission.?denied|in use|can'?t (write|save|overwrite))\b");
            bool fileWord = Regex.IsMatch(c, @"\b(file|files|document|documents)\b");
            return lockWord && fileWord;
        }

        public static async Task<HandleLockedFilesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new HandleLockedFilesResult();
            if (model == null) { res.Error = "Open a document to check for locked files."; return res; }

            string path = null; try { path = model.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(path))
            { res.Error = "This document has never been saved, so it has no file to check."; return res; }

            await emit("Sentinel", "checking " + Path.GetFileName(path) + " against the disk", "run", null);

            var row = ProbeFile(path);
            res.Rows.Add(row);
            res.Checked = 1;
            res.Blocked = row.Status == "ok" ? 0 : 1;
            res.Success = true;

            res.Info = res.Blocked == 0
                ? Path.GetFileName(path) + " is clear — writable, not locked, no permission issue."
                : Path.GetFileName(path) + " is " + row.Status + " — queue it for manual review, don't half-process the batch.";
            await emit("Sentinel", null, res.Blocked == 0 ? "done" : "fail", res.Info);
            return res;
        }

        // OS read-only attribute only — see the class doc comment for why an exclusive-open probe against the
        // ACTIVE document is a guaranteed false positive on this build (SolidWorks holds its own lock on it).
        internal static LockedFileRow ProbeFile(string path)
        {
            var row = new LockedFileRow { Path = path };
            if (!File.Exists(path)) { row.Status = "missing"; return row; }

            bool readOnly = false;
            try { readOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0; } catch { }
            row.Status = readOnly ? "read-only" : "ok";
            return row;
        }
    }
}
