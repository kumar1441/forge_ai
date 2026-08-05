using System;
using System.IO;

namespace Forge.SolidWorks
{
    /// <summary>
    /// THE single place that decides where Forge may write. Every file write / SaveAs / delete /
    /// rename in the entire add-in MUST resolve its target through here. Anything outside the
    /// sandbox hard-aborts. Core guarantee: Forge is physically incapable of writing a user's
    /// original files. A failed edit is acceptable; a corrupted original never is.
    ///
    /// Sandbox root is per-user, always-writable, and never inside a PDM vault:
    ///   %LOCALAPPDATA%\Forge\Sandbox\
    /// Because we NEVER write next to the original, we sidestep vault / permission / network landmines.
    /// </summary>
    internal static class SafePaths
    {
        private static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge", "Sandbox");

        public static string SandboxRoot { get { return Root; } }

        /// <summary>First-run check: the sandbox must be creatable AND writable. Throws if not.</summary>
        public static void EnsureUsable()
        {
            Directory.CreateDirectory(Root);
            string probe = Path.Combine(Root, ".forge_write_test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }

        /// <summary>A fresh, unique folder for one operation: %LOCALAPPDATA%\Forge\Sandbox\yyyyMMdd_HHmmss_fff\</summary>
        public static string NewRunFolder()
        {
            string f = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(f);
            return f;
        }

        public static bool IsInsideSandbox(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>The guard. Call before ANY write/save/delete/rename. Aborts if outside the sandbox.</summary>
        public static void AssertInSandbox(string path)
        {
            if (!IsInsideSandbox(path))
                throw new UnauthorizedAccessException(
                    "Forge refused a write outside its sandbox. Your original files are untouched.");
        }
    }
}