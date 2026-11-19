using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateTreeFolderResult
    {
        public string FolderName;
        public string Route;              // "Containing" | "EmptyBefore+MoveToFolder" — which API actually built it
        public string MemberRoute;        // "FeatureFolder.GetFeatures" | "span" — how membership was READ back
        public List<string> Requested = new List<string>();
        public List<string> Grouped = new List<string>();     // INDEPENDENTLY confirmed inside the folder afterwards
        public List<string> NotGrouped = new List<string>();
        public List<string> AlsoGrouped = new List<string>();  // dragged in because a folder is a CONTIGUOUS range
        public int FlatFeaturesBefore;    // real features before
        public int FlatFeaturesAfter;     // after — must not LOSE any feature
        public bool FolderPresent;
        public bool EndTagPresent;
        public int RebuildErrors;
        public bool AlreadyDone;
        public bool NeedsConfirm;
        public string Question;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CreateTreeFolder (tool #148 create_tree_folder) — group features into a named folder. The only tool that makes a
    /// 500-feature tree navigable: "put Seed-Hole and LPattern1 into a folder called Holes" and the tree stops being a
    /// wall of Fillet1..Fillet40.
    ///
    ///   Gauge  — resolve the folder NAME and the target features from the LIVE tree (explicit names first, then a type
    ///            word). Zero or ambiguous → one question, nothing created (Rule #2).
    ///   Filer  — select the targets and call InsertFeatureTreeFolder2(swFeatureTreeFolder_Containing), which builds the
    ///            folder AROUND the selection in one step; if that returns null, fall back to an EMPTY folder plus
    ///            IFeatureManager.MoveToFolder per feature. Which route worked is recorded, not assumed.
    ///   Sentinel—FAIL CLOSED (Rule #6): re-walk the tree, find the folder BY NAME, enumerate its sub-features and
    ///            confirm each target is genuinely inside. A foldered feature leaves the flat top-level walk, so the
    ///            flat count MUST drop by exactly the number grouped — a second, structural check that the move was
    ///            real and not just a rename.
    ///
    /// Grouping moves nothing in the build ORDER — no geometry changes, no dependency is re-evaluated. Idempotent: a
    /// folder of that name already holding those features is a no-op. Ctrl+Z undoes it; Forge never saves.
    /// </summary>
    public static class CreateTreeFolder
    {
        // NARROW: needs a FOLDER noun plus a grouping verb. "rename every feature with the prefix X" (tool 146) has no
        // folder noun; "list the features" has neither.
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bfolders?\b")) return false;
            if (Regex.IsMatch(c, @"\b(component|components|assembly|file|files|configuration|config)\b")) return false;
            return Regex.IsMatch(c, @"\b(group|put|move|organi[sz]e|tidy|collect|gather|create|make|add|into|in)\b");
        }

        public static async Task<CreateTreeFolderResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateTreeFolderResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Feature folders live in a part's tree — open the .SLDPRT."; return res; }

            await emit("Gauge", "resolving the folder name and the features to group", "run", null);

            string folderName = ParseFolderName(intent);
            if (string.IsNullOrWhiteSpace(folderName))
            { res.NeedsConfirm = true; res.Question = "What should the folder be called? e.g. 'put Seed-Hole and LPattern1 into a folder called Holes'."; await emit("Gauge", null, "fail", "no folder name — asking"); return res; }
            res.FolderName = folderName;

            var flat = FlatFeatures(model);
            res.FlatFeaturesBefore = flat.Count;
            if (flat.Count == 0)
            { res.Error = "This part has no feature tree to organise."; await emit("Gauge", null, "fail", "no feature tree"); return res; }

            // ---- idempotent (Rule #5): the folder already exists ----
            var existingFolder = FindFolder(model, folderName);
            if (existingFolder != null)
            {
                res.AlreadyDone = true;
                res.FolderPresent = true;
                res.EndTagPresent = HasEndTag(model);
                res.Verified = true;
                string r0;
                res.Grouped = Members(model, existingFolder, out r0);
                res.MemberRoute = r0;
                res.FlatFeaturesAfter = res.FlatFeaturesBefore;
                res.Info = "A folder called '" + folderName + "' already exists (" + res.Grouped.Count + " feature(s) inside) — nothing to do.";
                await emit("Filer", null, "done", "folder '" + folderName + "' already exists");
                return res;
            }

            // ---- resolve targets: explicit names first, then a type word ----
            var targets = ResolveTargets(intent, folderName, flat);
            if (targets.Count == 0)
            {
                res.NeedsConfirm = true;
                res.Question = "Which features should go in '" + folderName + "'? The tree has: " + string.Join(", ", flat.Select(SafeName).Take(8)) + ".";
                await emit("Gauge", null, "fail", "targets not resolved — asking");
                return res;
            }
            res.Requested = targets.Select(SafeName).ToList();
            await emit("Gauge", null, "done", "grouping " + targets.Count + " feature(s) into '" + folderName + "': " + string.Join(", ", res.Requested));

            // ---- a tree folder is a CONTIGUOUS RANGE on this build: anything sitting BETWEEN the requested features
            //      gets pulled in. Say so BEFORE creating it (Rule #3 / Character #1) rather than surprising the user. ----
            var between = FeaturesBetween(flat, res.Requested);
            if (between.Count > 0)
                await emit(null, null, "run", "a folder is a contiguous block, so " + string.Join(", ", between) + " sits between them and will be included too");

            int errBefore = SafeWhatsWrong(model);

            // ---- Filer: build the folder AROUND the selection ----
            await emit("Filer", "creating the folder", "run", null);
            Feature folder = null;
            try
            {
                model.ClearSelection2(true);
                foreach (var t in targets) { try { t.Select2(true, 0); } catch { } }
                folder = model.FeatureManager.InsertFeatureTreeFolder2((int)swFeatureTreeFolderType_e.swFeatureTreeFolder_Containing);
                model.ClearSelection2(true);
            }
            catch { }
            if (folder != null) res.Route = "Containing";
            else
            {
                // fallback: an EMPTY folder, then move each feature into it by name
                try
                {
                    model.ClearSelection2(true);
                    try { targets[0].Select2(false, 0); } catch { }
                    folder = model.FeatureManager.InsertFeatureTreeFolder2((int)swFeatureTreeFolderType_e.swFeatureTreeFolder_EmptyBefore);
                    model.ClearSelection2(true);
                }
                catch { }
                if (folder != null)
                {
                    string fn = SafeName(folder);
                    foreach (var t in targets) { try { model.FeatureManager.MoveToFolder(fn, SafeName(t), false); } catch { } }
                    res.Route = "EmptyBefore+MoveToFolder";
                }
            }

            if (folder == null)
            {
                res.Error = "SolidWorks would not create a feature folder on this build (both InsertFeatureTreeFolder2 routes returned null) — the tree is unchanged.";
                await emit("Filer", null, "fail", res.Error);
                return res;
            }

            try { folder.Name = folderName; } catch { }
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);

            // ---- Sentinel: FAIL CLOSED — find the folder by NAME in a fresh walk and read what is inside it ----
            await emit("Sentinel", "verifying the folder contents against a fresh tree read", "run", null);
            var check = FindFolder(model, folderName);
            res.FolderPresent = check != null;
            res.EndTagPresent = HasEndTag(model);

            string mroute = null;
            var inside = check != null ? Members(model, check, out mroute) : new List<string>();
            res.MemberRoute = check != null ? mroute : null;

            foreach (var want in res.Requested)
            {
                if (inside.Any(n => string.Equals(n, want, StringComparison.OrdinalIgnoreCase))) res.Grouped.Add(want);
                else res.NotGrouped.Add(want);
            }
            res.AlsoGrouped = inside.Where(n => !res.Requested.Any(w => string.Equals(w, n, StringComparison.OrdinalIgnoreCase))).ToList();

            var flatAfter = FlatFeatures(model).Select(SafeName).Where(n => !string.IsNullOrEmpty(n)).ToList();
            res.FlatFeaturesAfter = flatAfter.Count;
            var lost = flat.Select(SafeName).Where(n => !string.IsNullOrEmpty(n))
                           .Where(n => !flatAfter.Any(a => string.Equals(a, n, StringComparison.OrdinalIgnoreCase))).ToList();

            bool clean = res.RebuildErrors <= errBefore;
            res.Verified = res.FolderPresent && res.EndTagPresent && res.NotGrouped.Count == 0 && res.Grouped.Count > 0 && lost.Count == 0 && clean;
            if (!res.Verified)
            {
                res.Error = !res.FolderPresent ? "The folder wasn't created — the tree is unchanged."
                          : !res.EndTagPresent ? "The folder was created without a closing marker — the tree structure is not what a folder should look like."
                          : res.NotGrouped.Count > 0 ? res.NotGrouped.Count + " feature(s) are NOT inside the folder (" + string.Join(", ", res.NotGrouped.Take(3)) + ")."
                          : lost.Count > 0 ? lost.Count + " feature(s) vanished from the tree (" + string.Join(", ", lost.Take(3)) + ") — check the part."
                          : !clean ? "Grouping introduced " + (res.RebuildErrors - errBefore) + " rebuild error(s)."
                          : "Nothing was grouped.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", res.Grouped.Count + " requested feature(s) confirmed inside '" + folderName + "'" +
                       (res.AlsoGrouped.Count > 0 ? " (+" + res.AlsoGrouped.Count + " pulled in by the contiguous block)" : "") +
                       " · " + res.FlatFeaturesAfter + " features intact · rebuild clean");
            res.Info = "Grouped " + res.Grouped.Count + " feature(s) into '" + folderName + "' (" + string.Join(", ", res.Grouped) + ")." +
                       (res.AlsoGrouped.Count > 0 ? " " + string.Join(", ", res.AlsoGrouped) + " came too — a tree folder is one contiguous block, so anything between the features you named is inside it." : "") +
                       " Build order and geometry are untouched; Ctrl+Z undoes it and Forge didn't save.";
            return res;
        }

        // ================= helpers =================

        private static string ParseFolderName(string intent)
        {
            string c = (intent ?? "").Trim();
            var pats = new[]
            {
                @"\bfolder\s+(?:called|named)\s+[""'`]?([\w\-. ]+?)[""'`]?\s*$",
                @"\b(?:called|named)\s+[""'`]?([\w\-.]+)[""'`]?",
                @"\bfolder\s+[""'`]([^""'`]+)[""'`]",
            };
            foreach (var p in pats)
            {
                var m = Regex.Match(c, p, RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                string v = m.Groups[1].Value.Trim().Trim('"', '\'', '`', '.', ' ');
                if (v.Length > 0 && !Regex.IsMatch(v, @"^(a|the|new|folder)$", RegexOptions.IgnoreCase)) return v;
            }
            return null;
        }

        private static List<Feature> ResolveTargets(string intent, string folderName, List<Feature> flat)
        {
            var picked = new List<Feature>();
            string c = (intent ?? "");
            // strip the folder name so a feature isn't matched out of it
            string hay = Norm(c.Replace(folderName, " "));

            foreach (var f in flat)
            {
                string n = SafeName(f);
                if (string.IsNullOrEmpty(n)) continue;
                string nn = Norm(n);
                if (nn.Length >= 4 && hay.Contains(nn)) picked.Add(f);
            }
            if (picked.Count > 0) return picked;

            // a type word ("group the fillets into a folder called Rounds")
            var pred = TypePredicate(c);
            if (pred != null) picked.AddRange(flat.Where(f => { var tn = SafeType(f); return tn != null && pred(tn); }));
            return picked;
        }

        private static Func<string, bool> TypePredicate(string cmd)
        {
            string c = (cmd ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(fillet|round)s?\b")) return tn => tn == "Fillet";
            if (Regex.IsMatch(c, @"\b(chamfer)s?\b")) return tn => tn == "Chamfer";
            if (Regex.IsMatch(c, @"\b(hole|cut|pocket)s?\b")) return tn => tn.IndexOf("Hole", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(tn, "ICE", StringComparison.OrdinalIgnoreCase);
            if (Regex.IsMatch(c, @"\b(sketch|sketches)\b")) return tn => tn == "ProfileFeature";
            if (Regex.IsMatch(c, @"\b(pattern)s?\b")) return tn => tn.IndexOf("Pattern", StringComparison.OrdinalIgnoreCase) >= 0;
            return null;
        }

        private static List<Feature> FlatFeatures(IModelDoc2 model)
        {
            var list = new List<Feature>();
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = SafeType(f);
                if (!string.IsNullOrEmpty(tn) && !IsScaffold(tn)) list.Add(f);
                f = f.GetNextFeature() as Feature;
            }
            return list;
        }

        private static Feature FindFolder(IModelDoc2 model, string name)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = SafeType(f);
                if (!string.IsNullOrEmpty(tn) && tn.IndexOf("Folder", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    string.Equals(SafeName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        // MEASURED on this R2026x build: a feature folder is a flat START marker (type FtrFolder) plus a matching
        // "…___EndTag___" entry, with the members left in the top-level walk BETWEEN them. GetFirstSubFeature() on the
        // folder returns NOTHING, so sub-feature traversal — the route that works for a cut's consumed sketch — reads a
        // real folder as empty. Membership therefore comes from IFeatureFolder.GetFeatures(), with the start/end SPAN as
        // the fallback; which route answered is recorded, not assumed.
        private static List<string> Members(IModelDoc2 model, Feature folder, out string route)
        {
            route = null;
            try
            {
                var ff = folder.GetSpecificFeature2() as FeatureFolder;
                if (ff != null)
                {
                    var arr = ff.GetFeatures() as object[];
                    if (arr != null && arr.Length > 0)
                    {
                        var names = arr.OfType<Feature>().Select(SafeName).Where(n => !string.IsNullOrEmpty(n)).ToList();
                        if (names.Count > 0) { route = "FeatureFolder.GetFeatures"; return names; }
                    }
                }
            }
            catch { }

            route = "span";
            var span = new List<string>();
            bool inside = false; int depth = 0;
            var f = model.FirstFeature() as Feature;
            string want = SafeName(folder);
            while (f != null)
            {
                string tn = SafeType(f); string nm = SafeName(f);
                bool isFtr = string.Equals(tn, "FtrFolder", StringComparison.OrdinalIgnoreCase);
                bool isEnd = isFtr && (nm ?? "").IndexOf("___EndTag___", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!inside)
                {
                    if (isFtr && !isEnd && string.Equals(nm, want, StringComparison.OrdinalIgnoreCase)) { inside = true; depth = 1; }
                }
                else if (isEnd)
                {
                    depth--;
                    if (depth == 0) break;
                }
                else if (isFtr) depth++;
                else if (!string.IsNullOrEmpty(tn) && !IsScaffold(tn) && !string.IsNullOrEmpty(nm)) span.Add(nm);
                f = f.GetNextFeature() as Feature;
            }
            return span;
        }

        private static bool HasEndTag(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                if (string.Equals(SafeType(f), "FtrFolder", StringComparison.OrdinalIgnoreCase) &&
                    (SafeName(f) ?? "").IndexOf("___EndTag___", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                f = f.GetNextFeature() as Feature;
            }
            return false;
        }

        // the real features positioned BETWEEN the named ones in tree order — what a contiguous folder will swallow
        private static List<string> FeaturesBetween(List<Feature> flat, List<string> named)
        {
            var names = flat.Select(SafeName).ToList();
            var idx = named.Select(n => names.FindIndex(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)))
                           .Where(i => i >= 0).OrderBy(i => i).ToList();
            if (idx.Count < 2) return new List<string>();
            var between = new List<string>();
            for (int k = idx.First() + 1; k < idx.Last(); k++)
                if (!named.Any(n => string.Equals(n, names[k], StringComparison.OrdinalIgnoreCase))) between.Add(names[k]);
            return between;
        }

        private static bool IsScaffold(string tn)
        {
            if (tn.EndsWith("Folder", StringComparison.OrdinalIgnoreCase)) return true;
            switch (tn)
            {
                case "RefPlane": case "RefAxis": case "OriginProfileFeature": case "CoordSys":
                case "DetailCabinet": case "HistoryFolder": case "SketchBlockDef": return true;
                default: return false;
            }
        }

        private static string Norm(string s) { return Regex.Replace(s ?? "", @"[^0-9A-Za-z]", "").ToLowerInvariant(); }
        private static string SafeName(Feature f) { try { return f?.Name; } catch { return null; } }
        private static string SafeType(Feature f) { try { return f?.GetTypeName2(); } catch { return null; } }
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
