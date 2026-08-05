using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for recover_autosave (tool 253). Re-derives the SAME live-resolved backup/
    /// autosave directories via its OWN separate GetUserPreferenceStringValue/Toggle calls, its own
    /// Directory.EnumerateFiles walk (not the handler's Directory.GetFiles), and its own SHA-256 diff — sharing
    /// no code with RecoverAutosave.cs. The filesystem + SW's own reported preferences are the ground truth.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureRecoverAutosave(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            string path = null; try { path = model.GetPathName(); } catch { }
            mo["path"] = path;
            if (string.IsNullOrWhiteSpace(path)) { mo["expectedRecoverable"] = false; mo["expectedCandidateCount"] = 0; return mo; }

            string baseNoExt = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            var pattern = new Regex("^" + Regex.Escape(baseNoExt) + @"(\(\d+\))?" + Regex.Escape(ext) + "$", RegexOptions.IgnoreCase);

            string backupDir = null, autoDir = null; bool sameLoc = false;
            try { backupDir = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swBackupDirectory); } catch { }
            try { autoDir = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swAutoSaveDirectory); } catch { }
            try { sameLoc = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swSaveBackupFilesInSameLocationAsOriginal); } catch { }

            var searchDirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(autoDir) && Directory.Exists(autoDir)) searchDirs.Add(autoDir);
            if (!sameLoc && !string.IsNullOrWhiteSpace(backupDir) && Directory.Exists(backupDir)) searchDirs.Add(backupDir);
            if (sameLoc)
            {
                string ownDir = null; try { ownDir = Path.GetDirectoryName(path); } catch { }
                if (!string.IsNullOrWhiteSpace(ownDir) && Directory.Exists(ownDir)) searchDirs.Add(ownDir);
            }

            var found = new List<Tuple<string, DateTime>>();
            foreach (var dir in searchDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                CollectMatches(dir, pattern, path, found);
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        if (Path.GetFileName(sub).IndexOf(baseNoExt, StringComparison.OrdinalIgnoreCase) >= 0)
                            CollectMatches(sub, pattern, path, found);
                }
                catch { }
            }

            mo["expectedCandidateCount"] = found.Count;
            if (found.Count == 0) { mo["expectedRecoverable"] = false; return mo; }

            var newest = found.OrderByDescending(t => t.Item2).First();
            DateTime savedUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            bool newer = newest.Item2 > savedUtc;
            mo["expectedNewestPath"] = newest.Item1;
            mo["expectedRecoverable"] = newer && HashesDiffer(path, newest.Item1);
            return mo;
        }

        private static void CollectMatches(string dir, Regex pattern, string savedPath, List<Tuple<string, DateTime>> found)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); } catch { return; }
            foreach (var f in files)
            {
                if (string.Equals(f, savedPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!pattern.IsMatch(Path.GetFileName(f))) continue;
                DateTime lw;
                try { lw = File.GetLastWriteTimeUtc(f); } catch { continue; }
                found.Add(Tuple.Create(f, lw));
            }
        }

        private static bool HashesDiffer(string a, string b)
        {
            try
            {
                byte[] ha, hb;
                using (var sha = SHA256.Create()) using (var fs = File.OpenRead(a)) ha = sha.ComputeHash(fs);
                using (var sha = SHA256.Create()) using (var fs = File.OpenRead(b)) hb = sha.ComputeHash(fs);
                return !ha.SequenceEqual(hb);
            }
            catch { return true; }
        }
    }
}
