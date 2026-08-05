using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// Read-only helpers around SolidWorks file references. Forge never opens an original for write and
    /// never calls Save() on one — variants and edits are written ONLY as SaveAs-with-Copy into the
    /// sandbox (see VariantGenerator), which leaves every original byte-for-byte untouched. This file
    /// just enumerates the originals an operation depends on, so the tripwire (OriginalGuard) can prove
    /// they didn't change.
    ///
    /// NOTE: GetDocumentDependencies2 behaviour should be spot-checked on a live SolidWorks install —
    /// it cannot be exercised off a real SW session.
    /// </summary>
    internal static class SafeIO
    {
        /// <summary>
        /// Every original file this operation depends on: the top document plus, for an assembly or
        /// drawing, all referenced parts. The tripwire fingerprints each of these before/after.
        /// </summary>
        public static List<string> GetOriginalPaths(ISldWorks swApp, IModelDoc2 model)
        {
            var paths = new List<string>();
            try
            {
                string top = model.GetPathName();
                if (!string.IsNullOrEmpty(top)) paths.Add(top);

                object depsObj = swApp.GetDocumentDependencies2(top, true, true, false);
                var deps = depsObj as string[];
                if (deps != null)
                    foreach (var d in deps)
                        if (LooksLikeSwFile(d) && !paths.Contains(d))
                            paths.Add(d);
            }
            catch { /* worst case we still fingerprint the top file, which is what matters most */ }

            return paths;
        }

        private static bool LooksLikeSwFile(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            string e = Path.GetExtension(p).ToLowerInvariant();
            return e == ".sldprt" || e == ".sldasm" || e == ".slddrw";
        }
    }
}
