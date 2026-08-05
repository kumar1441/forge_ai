using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AutosaveCandidateRow
    {
        public string Path;
        public string Kind;          // "backup" | "autosave" | "same-location"
        public string LastWriteUtc;  // ISO 8601
        public long SizeBytes;
    }

    public class RecoverAutosaveResult
    {
        public bool Success;
        public int Checked;          // candidates found
        public bool Recoverable;     // newest candidate is genuinely newer AND differs in content
        public string NewestPath;
        public string NewestKind;
        public string NewestLastWriteUtc;
        public List<AutosaveCandidateRow> Candidates = new List<AutosaveCandidateRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// RecoverAutosave (tool 253, READ) — "after a crash: locate the newest autosave/backup copy of the
    /// currently-open document, diff it against the last SAVED file, and OFFER recovery." Never auto-overwrites
    /// anything — a positive result is a pointer + timestamp + honest content-differs verdict for the human to
    /// act on, same "report, don't act" posture as `handle_locked_files` (tool 248).
    ///
    /// SolidWorks exposes its own configured recovery locations live (Tools > Options > Backup/Recover),
    /// confirmed via reflection (2026-07-31, not guessed): `swUserPreferenceStringValue_e.swBackupDirectory` +
    /// `swAutoSaveDirectory`, and `swUserPreferenceToggle_e.swSaveBackupFilesInSameLocationAsOriginal` (if set,
    /// backups land beside the original file instead of the configured backup folder). Never hardcode a path —
    /// always resolve these three live off `app`.
    ///
    /// Matching: SolidWorks backup copies keep the original base filename (first copy) or append a numbered
    /// "(N)" suffix (2nd+ copies per `swBackupCopiesPerDocument`); AutoRecover snapshots have been observed to
    /// land either directly in the AutoSave folder or one level down inside a subfolder named for the source
    /// document. This handler covers both shapes: top-level files in each resolved directory whose base name
    /// matches (exact or "(N)" suffix), PLUS one level into any subfolder whose name starts with the base name.
    ///
    /// SAFETY: a genuine post-crash autosave/backup file can be a partially-written, corrupt document — this
    /// handler NEVER opens the candidate file in SolidWorks, only stats it (size/timestamp) and hashes its raw
    /// bytes (SHA-256) to tell "differs from last save" from "identical, nothing to recover". Geometry-level
    /// diffing (what specifically changed) is out of scope for v1 — honestly reported as a pointer, not guessed.
    /// </summary>
    public static class RecoverAutosave
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool recoverWord = Regex.IsMatch(c, @"\b(recover(y)?|restore|salvage)\b");
            bool triggerWord = Regex.IsMatch(c, @"\b(autosave|auto-save|auto recover(y)?|backup|crash(ed)?)\b");
            return recoverWord && triggerWord;
        }

        public static async Task<RecoverAutosaveResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RecoverAutosaveResult();
            if (model == null) { res.Error = "Open a document to look for an autosave/backup to recover."; return res; }

            string path = null; try { path = model.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(path))
            { res.Error = "This document has never been saved, so there's no last-saved baseline to recover against."; return res; }

            await emit("Sentinel", "scanning SolidWorks' configured backup/autosave folders for " + Path.GetFileName(path), "run", null);

            var candidates = Scan(app, path);
            res.Candidates = candidates;
            res.Checked = candidates.Count;

            if (candidates.Count == 0)
            {
                res.Success = true;
                res.Info = "No autosave or backup copy of " + Path.GetFileName(path) + " found in SolidWorks' configured recovery folders.";
                await emit("Sentinel", null, "done", res.Info);
                return res;
            }

            var newest = candidates.OrderByDescending(c => c.LastWriteUtc).First();
            DateTime savedUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            DateTime newestUtc = DateTime.Parse(newest.LastWriteUtc).ToUniversalTime();
            bool newer = newestUtc > savedUtc;
            bool differs = newer && !SameContent(path, newest.Path);

            res.NewestPath = newest.Path;
            res.NewestKind = newest.Kind;
            res.NewestLastWriteUtc = newest.LastWriteUtc;
            res.Recoverable = differs;
            res.Success = true;
            res.Info = differs
                ? "Found a newer " + newest.Kind + " copy (" + newest.LastWriteUtc + ") that differs from the last save — recovery available at " + newest.Path
                : "Newest " + newest.Kind + " copy isn't newer than (or doesn't differ from) the last saved content — nothing to recover.";
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }

        // Live-resolved recovery directories — never hardcoded. Returns each distinct, existing directory once,
        // tagged with the "kind" it came from.
        internal static List<Tuple<string, string>> ResolveDirs(ISldWorks app)
        {
            var dirs = new List<Tuple<string, string>>();
            string backupDir = null, autoDir = null; bool sameLoc = false;
            try { backupDir = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swBackupDirectory); } catch { }
            try { autoDir = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swAutoSaveDirectory); } catch { }
            try { sameLoc = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSaveBackupFilesInSameLocationAsOriginal); } catch { }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(autoDir) && Directory.Exists(autoDir) && seen.Add(autoDir)) dirs.Add(Tuple.Create(autoDir, "autosave"));
            if (!sameLoc && !string.IsNullOrWhiteSpace(backupDir) && Directory.Exists(backupDir) && seen.Add(backupDir)) dirs.Add(Tuple.Create(backupDir, "backup"));
            return dirs;
        }

        internal static List<AutosaveCandidateRow> Scan(ISldWorks app, string savedPath)
        {
            var rows = new List<AutosaveCandidateRow>();
            string baseNoExt = Path.GetFileNameWithoutExtension(savedPath);
            string ext = Path.GetExtension(savedPath);
            var namePattern = new Regex("^" + Regex.Escape(baseNoExt) + @"(\(\d+\))?" + Regex.Escape(ext) + "$", RegexOptions.IgnoreCase);

            bool sameLoc = false; try { sameLoc = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSaveBackupFilesInSameLocationAsOriginal); } catch { }
            var dirs = ResolveDirs(app);
            if (sameLoc)
            {
                string ownDir = null; try { ownDir = Path.GetDirectoryName(savedPath); } catch { }
                if (!string.IsNullOrWhiteSpace(ownDir) && Directory.Exists(ownDir)) dirs.Add(Tuple.Create(ownDir, "same-location"));
            }

            foreach (var d in dirs)
            {
                string dir = d.Item1, kind = d.Item2;
                AddMatches(rows, dir, kind, namePattern, savedPath);
                try
                {
                    foreach (var sub in Directory.GetDirectories(dir))
                    {
                        if (Path.GetFileName(sub).IndexOf(baseNoExt, StringComparison.OrdinalIgnoreCase) >= 0)
                            AddMatches(rows, sub, kind, namePattern, savedPath);
                    }
                }
                catch { }
            }
            return rows;
        }

        private static void AddMatches(List<AutosaveCandidateRow> rows, string dir, string kind, Regex namePattern, string savedPath)
        {
            string[] files;
            try { files = Directory.GetFiles(dir); } catch { return; }
            foreach (var f in files)
            {
                if (string.Equals(f, savedPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!namePattern.IsMatch(Path.GetFileName(f))) continue;
                DateTime lw; long len;
                try { lw = File.GetLastWriteTimeUtc(f); len = new FileInfo(f).Length; } catch { continue; }
                rows.Add(new AutosaveCandidateRow { Path = f, Kind = kind, LastWriteUtc = lw.ToString("o"), SizeBytes = len });
            }
        }

        // Raw byte hash only — the candidate is NEVER opened in SolidWorks (see class doc: it may be corrupt).
        internal static bool SameContent(string a, string b)
        {
            try
            {
                byte[] ha, hb;
                using (var sha = SHA256.Create()) using (var fs = File.OpenRead(a)) ha = sha.ComputeHash(fs);
                using (var sha = SHA256.Create()) using (var fs = File.OpenRead(b)) hb = sha.ComputeHash(fs);
                return ha.SequenceEqual(hb);
            }
            catch { return false; }
        }
    }
}
