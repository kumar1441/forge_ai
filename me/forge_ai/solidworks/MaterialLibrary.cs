using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class MaterialMatch
    {
        public string Name;        // resolved SolidWorks material name (from the live library)
        public string Database;    // "SOLIDWORKS Materials" etc.
        public bool Ambiguous;     // true -> ask the user, do not apply
        public List<string> Options = new List<string>(); // concrete options from the actual library
        public string Note;
    }

    /// <summary>
    /// Material name -> live SolidWorks library resolution (Robustness Rule #2 + Ravi #4). Enumerates the real .sldmat
    /// databases (never a hardcoded dict), maps a requested word to the nearest AVAILABLE material, and flags an
    /// ambiguity with concrete options when the word maps to many ("plastic" -> ABS / Nylon / PE) or none.
    /// </summary>
    public static class MaterialLibrary
    {
        private class Mat { public string Name; public string Category; public string Db; }
        private static List<Mat> _cache;

        // word -> search hints against real library names/categories (hints only; the NAME comes from the library)
        private static readonly Dictionary<string, string[]> Hints = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "aluminum", new[]{ "6061","aluminium","aluminum","alloy" } }, { "aluminium", new[]{ "6061","aluminium","aluminum","alloy" } }, { "alu", new[]{ "6061","aluminium","aluminum" } },
            { "steel", new[]{ "1020","plain carbon","steel" } }, { "mild steel", new[]{ "1020","plain carbon" } },
            { "stainless", new[]{ "stainless","304","316" } }, { "ss", new[]{ "stainless","304" } },
            { "brass", new[]{ "brass" } }, { "copper", new[]{ "copper" } }, { "cu", new[]{ "copper" } }, { "bronze", new[]{ "bronze" } },
            { "titanium", new[]{ "titanium" } }, { "ti", new[]{ "titanium" } },
            { "iron", new[]{ "cast iron","gray cast","malleable" } }, { "cast iron", new[]{ "cast iron","gray cast" } },
            { "nylon", new[]{ "nylon" } }, { "abs", new[]{ "abs" } }, { "acrylic", new[]{ "acrylic" } },
            { "glass", new[]{ "glass" } }, { "rubber", new[]{ "rubber" } },
            { "fiberglass", new[]{ "glass","fiberglass","fibreglass","frp","gfrp" } }, { "fibreglass", new[]{ "glass","fiberglass","fibreglass","frp","gfrp" } },
            { "plastic", new[]{ "abs","nylon","polyethylene","polycarbonate","pvc","pom","acrylic" } },
            { "gold", new[]{ "gold" } }, { "silver", new[]{ "silver" } }, { "zinc", new[]{ "zinc" } }, { "lead", new[]{ "lead" } }, { "tin", new[]{ "tin" } }
        };

        private static List<Mat> Load(ISldWorks app)
        {
            if (_cache != null) return _cache;
            var list = new List<Mat>();
            try
            {
                object[] dbs = app.GetMaterialDatabases() as object[];
                foreach (var o in dbs ?? new object[0])
                {
                    string path = o as string; if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
                    string dbName = System.IO.Path.GetFileNameWithoutExtension(path);
                    try
                    {
                        var doc = XDocument.Load(path);
                        foreach (var cls in doc.Descendants().Where(e => e.Name.LocalName == "classification"))
                        {
                            string cat = (string)cls.Attribute("name") ?? "";
                            foreach (var m in cls.Elements().Where(e => e.Name.LocalName == "material"))
                            {
                                string nm = (string)m.Attribute("name");
                                if (!string.IsNullOrEmpty(nm)) list.Add(new Mat { Name = nm, Category = cat, Db = dbName });
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            _cache = list;
            return list;
        }

        public static MaterialMatch Resolve(ISldWorks app, string word)
        {
            var res = new MaterialMatch();
            var lib = Load(app);
            string w = (word ?? "").Trim().ToLowerInvariant();
            if (w.Length == 0) { res.Ambiguous = true; res.Note = "no material named"; return res; }
            if (lib.Count == 0) { res.Name = word; res.Note = "library not readable — passing name through"; return res; } // fail-open to SetMaterial

            // CANONICAL DEFAULT (test-loop hedge fix, flange-05-material-steel, the regression corpus 246abf32808c): "change all parts to steel" was asked to disambiguate among 5 obscure DIN case-hardening
            // steels AND a bronze alloy (2.1020 CuSn6 — its name merely shares the digits "1020" with "AISI 1020" via
            // the fuzzy hint scan below) instead of just applying the well-known engineering default. A bare,
            // unqualified word that already has a deliberate common-sense default (Materializer.Mat: steel -> AISI
            // 1020, aluminum -> 6061 Alloy, etc.) resolves straight to that default — checked BEFORE the fuzzy scan —
            // as long as the exact name is actually present in this SW install's live library (fail-open: if it's
            // missing, fall through to the fuzzy scan below rather than erroring).
            if (Materializer.Mat.TryGetValue(w, out var canonical))
            {
                var exact = lib.FirstOrDefault(m => string.Equals(m.Name, canonical, StringComparison.OrdinalIgnoreCase));
                if (exact != null) { res.Name = exact.Name; res.Database = exact.Db; return res; }
            }

            // COMPOUND alloy/spec phrasing (test-loop hedge fix, chain-change-material-and-weight, the regression corpus
            // regression corpus 8a75567937c0): "aluminum 6061" (alloy family + temper/spec number in one phrase)
            // isn't a canonical-dict KEY itself (only the bare word "aluminum" is), so the exact-match above misses
            // it entirely and it fell through to the fuzzy library scan, which found zero name/category hits for
            // the literal phrase "aluminum 6061" and asked to disambiguate among unrelated EN-AW alloys instead of
            // just applying the well-known "aluminum" default. Retry as a CONTAINS match against the canonical
            // keywords (longest key first, so "cast iron" wins over the shorter "iron") before the fuzzy scan.
            foreach (var kv in Materializer.Mat.OrderByDescending(k => k.Key.Length))
            {
                if (!w.Contains(kv.Key)) continue;
                var exact2 = lib.FirstOrDefault(m => string.Equals(m.Name, kv.Value, StringComparison.OrdinalIgnoreCase));
                if (exact2 != null) { res.Name = exact2.Name; res.Database = exact2.Db; return res; }
                break;
            }

            string[] hints = Hints.TryGetValue(w, out var h) ? h : new[] { w };

            // score each library material: exact name match > name contains hint > category contains hint
            var hitNames = new List<Mat>();
            foreach (var m in lib)
            {
                string nmL = m.Name.ToLowerInvariant(), catL = (m.Category ?? "").ToLowerInvariant();
                if (nmL == w) { res.Name = m.Name; res.Database = m.Db; return res; } // perfect
                if (hints.Any(x => nmL.Contains(x)) || hints.Any(x => catL.Contains(x))) hitNames.Add(m);
            }
            var distinct = hitNames.GroupBy(m => m.Name).Select(g => g.First()).ToList();
            if (distinct.Count == 0) { res.Ambiguous = true; res.Note = "no '" + word + "' in the material library"; res.Options = ClosestCategories(lib, w); return res; }
            if (distinct.Count == 1) { res.Name = distinct[0].Name; res.Database = distinct[0].Db; return res; }

            // several — if the word is a broad category (plastic), or matches spread across categories, ASK with options
            var byCat = distinct.Select(m => m.Category).Distinct().Count();
            if (w == "plastic" || byCat > 1 || distinct.Count > 4)
            {
                res.Ambiguous = true;
                res.Note = "several '" + word + "' materials — which one?";
                res.Options = distinct.Select(m => m.Name).Take(5).ToList();
                return res;
            }
            // same category, a few close variants -> pick the shortest/most canonical name
            var best = distinct.OrderBy(m => m.Name.Length).First();
            res.Name = best.Name; res.Database = best.Db;
            return res;
        }

        private static List<string> ClosestCategories(List<Mat> lib, string w)
        {
            // offer a few real material names as a hint when nothing matched
            return lib.Where(m => (m.Category ?? "").ToLowerInvariant().Contains(w.Substring(0, Math.Min(3, w.Length))))
                      .Select(m => m.Name).Distinct().Take(4).ToList();
        }
    }
}
