using System;

namespace Forge.SolidWorks
{
    /// <summary>
    /// Open-time document state — the ONLY honest signal for a broken/missing reference on this 3DEXPERIENCE build.
    ///
    /// WHY THIS EXISTS (proven 2026-07-22, two mechanisms): when a referenced part file is missing, SolidWorks
    /// AUTO-SUPPRESSES the orphaned components. Post-open the document reports `rebuildErrors=0`,
    /// `unresolvedComponents=0`, `overDefined=0` — it looks perfectly healthy. Suppressing a component behaves the
    /// same way (SW cascades the suppression to the dependent mates). So NO amount of post-open inspection can tell
    /// you the references are broken. The only thing that ever said so was `OpenDoc6`'s error/warning return:
    /// `openErrors=2` on an assembly whose bolt file had been renamed.
    ///
    /// That return value is discarded the moment the open call finishes, so we capture it here at the point of open
    /// and let handlers consult it later. Whoever opens a document (the harness, or the panel) records it; the
    /// classification pre-pass in RedWave reads it back and routes a "red wave" that is really a reference fault to
    /// the repair path instead of wasting ddmin on it.
    ///
    /// Deliberately path-keyed: a stale reading from a DIFFERENT document must never leak into a later run, so
    /// consumers pass the path they care about and get zero unless it matches.
    /// </summary>
    public static class OpenState
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static int _errors;
        private static int _warnings;
        private static DateTime _whenUtc;

        public static void Record(string path, int errors, int warnings)
        {
            lock (Gate)
            {
                _path = path ?? "";
                _errors = errors;
                _warnings = warnings;
                _whenUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Open-time error code for this document, or 0 if we have no reading for it.</summary>
        public static int ErrorsFor(string path)
        {
            lock (Gate)
            {
                if (string.IsNullOrEmpty(_path) || string.IsNullOrEmpty(path)) return 0;
                if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return 0;
                // A reading older than the session is worthless — treat anything stale as "no signal" rather than
                // risk classifying a healthy model as broken on the strength of a previous document's codes.
                if ((DateTime.UtcNow - _whenUtc).TotalMinutes > 30) return 0;
                return _errors;
            }
        }

        public static int WarningsFor(string path)
        {
            lock (Gate)
            {
                if (string.IsNullOrEmpty(_path) || string.IsNullOrEmpty(path)) return 0;
                if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return 0;
                if ((DateTime.UtcNow - _whenUtc).TotalMinutes > 30) return 0;
                return _warnings;
            }
        }
    }
}
