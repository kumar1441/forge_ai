using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Forge.SolidWorks
{
    /// <summary>
    /// The tripwire. Snapshots every ORIGINAL input file (SHA-256 + last-write time) before an
    /// operation, and re-checks after. If any original changed, a guardrail bug let a write through —
    /// the caller must refuse to report success and fire a guardrail_violation event. This is the
    /// self-check that catches our own mistakes before the customer does.
    /// </summary>
    internal sealed class OriginalGuard
    {
        private readonly Dictionary<string, string> _fingerprints =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static OriginalGuard Capture(IEnumerable<string> originalPaths)
        {
            var g = new OriginalGuard();
            if (originalPaths != null)
                foreach (var p in originalPaths)
                    if (!string.IsNullOrEmpty(p) && File.Exists(p) && !g._fingerprints.ContainsKey(p))
                        g._fingerprints[p] = Fingerprint(p);
            return g;
        }

        /// <summary>True if EVERY captured original is byte-for-byte unchanged.</summary>
        public bool AllIntact()
        {
            foreach (var kv in _fingerprints)
            {
                if (!File.Exists(kv.Key)) return false;         // vanished / renamed
                if (Fingerprint(kv.Key) != kv.Value) return false; // content changed
            }
            return true;
        }

        public int Count { get { return _fingerprints.Count; } }

        // Content SHA-256 is the guarantee. Open with FileShare.ReadWrite so a file that SolidWorks has
        // OPEN (and holds a lock on) still reads deterministically — otherwise a locked-but-unchanged
        // original would false-trip the tripwire. Falls back to size+timestamp (deterministic) only if
        // the bytes genuinely can't be read — never a random value.
        private static string Fingerprint(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
            }
            catch
            {
                try { var fi = new FileInfo(path); return "meta:" + fi.Length + ":" + fi.LastWriteTimeUtc.Ticks; }
                catch { return "missing"; }
            }
        }
    }
}