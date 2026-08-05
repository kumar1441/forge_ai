using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class InsertBomTableResult
    {
        public bool AlreadyDone;
        public bool Verified;
        public string ViewName;
        public int Rows;
        public int Columns;
        public int ComponentTypes;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// InsertBomTable (tool #113 insert_bom_table, WRITE) — for the ACTIVE drawing, find the view whose referenced
    /// model is an assembly and attach a bill-of-materials table to it, linked live to the assembly (edits to the
    /// assembly's components/quantities update the table on rebuild — this is a native linked BOM, not a static
    /// snapshot). Zero prior art for this tool in the codebase: `IView.InsertBomTable2` confirmed live via redist
    /// DLL reflection (same technique used to confirm tool 114's `ReplaceViewModel`) — `IDrawingDoc` itself has no
    /// Bom-named method; the call lives on `IView`.
    ///
    /// Template: SolidWorks ships several (`bom-standard.sldbomtbt` etc.) under `<install>\lang\<language>\`; no
    /// single API call reports the active one directly, so resolved the same way `CreateDrawing.cs` resolves its
    /// drawing template — a registry preference first (`swFileLocationsBOMTemplates`, a SEARCH-FOLDER list, not a
    /// filename), then the install-relative fallback.
    ///
    /// FAIL CLOSED: Verified requires the returned object to actually expose row/column counts (a `TableAnnotation`
    /// cast, not just InsertBomTable2's non-null return) with rows > 1 (header + >=1 component row) after a
    /// rebuild. IDEMPOTENT (Rule #5): found live that `IView.GetBomTable()` never reflects a just-inserted table
    /// on this build (not even after rebuild+save+a fresh View re-fetch — a dead accessor, not a timing lag), so
    /// AlreadyDone instead scans the document's FEATURE TREE for a `"BomFeat"` feature (a completely different API
    /// surface, confirmed live as the signal that actually moves 0→1 and catches a real bug: the first cut used
    /// the dead accessor for this check too and stacked a second table on every rerun).
    /// </summary>
    public static class InsertBomTable
    {
        // Requires an insert-ish verb AND the "bom"/"bill of materials" noun. No other handler in this build
        // claims "bom" (checked: no matcher references the word), so this needs no drawing-scope qualifier to stay
        // disjoint — narrow verb+noun pairing is enough.
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(insert|add|create|put|generate|make)\b")) return false;
            return Regex.IsMatch(c, @"\bbom\b|\bbill\s+of\s+materials\b");
        }

        public static async Task<InsertBomTableResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new InsertBomTableResult();
            var dd = model as DrawingDoc;
            if (dd == null) { res.Error = "Open the drawing you want the BOM on."; return res; }

            await emit("Scout", "finding a view of an assembly to attach the BOM to", "run", null);
            View targetView = null; string targetViewName = null;
            var v = dd.GetFirstView() as IView; bool first = true;
            while (v != null)
            {
                if (!first)
                {
                    string rm = null; try { rm = v.GetReferencedModelName(); } catch { }
                    if (!string.IsNullOrEmpty(rm) && rm.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase))
                    { targetView = v as View; targetViewName = v.GetName2(); break; }
                }
                first = false;
                v = v.GetNextView() as IView;
            }
            if (targetView == null)
            { res.Error = "No view of an assembly found on this drawing — a BOM needs an assembly view."; return res; }
            res.ViewName = targetViewName;
            await emit("Scout", null, "done", "assembly view: " + targetViewName);

            // IDEMPOTENCY CHECK: found live that IView.GetBomTable() never reflects a just-inserted table (returns
            // null even after ForceRebuild3+Save3+a fresh View re-fetch — a dead accessor for this build, not a
            // timing lag), so it cannot be trusted for AlreadyDone either. A BOM insert lands as its own feature-
            // tree entry (confirmed live: "BomFeat:Bill of Materials1"), so that's the real, working signal.
            bool already = HasBomFeature(model);
            if (already)
            {
                res.AlreadyDone = true; res.Verified = true;
                res.Info = "This drawing already has a BOM table — nothing to insert.";
                return res;
            }

            string template = ResolveTemplate(app);
            if (template == null) { res.Error = "Couldn't find a BOM table template on this install."; return res; }

            await emit("Scribe", "inserting the BOM table", "run", null);
            object raw = null;
            try
            {
                raw = (targetView as IView).InsertBomTable2(true, 0, 0,
                    (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopLeft,
                    (int)swBomType_e.swBomType_Indented, "", template);
            }
            catch (Exception ex) { res.Error = "InsertBomTable2 threw (" + ex.GetType().Name + ")"; return res; }
            if (raw == null) { res.Error = "InsertBomTable2 returned nothing — the template or view may be unusable."; return res; }

            try { model.ForceRebuild3(true); } catch { }
            int se = 0, sw = 0; try { model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref se, ref sw); } catch { }

            // ---- FAIL CLOSED: read the table back through the generic ITableAnnotation surface (independent of
            // InsertBomTable2's own non-null return alone) so a table that was created but is somehow empty
            // doesn't pass. ----
            var ta = raw as TableAnnotation;
            if (ta == null) { res.Error = "Table created but could not be read back as a TableAnnotation."; return res; }
            int rows = 0, cols = 0; try { rows = ta.RowCount; } catch { } try { cols = ta.ColumnCount; } catch { }
            res.Rows = rows; res.Columns = cols;

            var bta = raw as BomTableAnnotation;
            int compTypes = 0;
            if (bta != null)
            {
                for (int r = 1; r < rows; r++)
                {
                    int c = 0; try { c = bta.GetComponentsCount(r); } catch { }
                    if (c > 0) compTypes++;
                }
            }
            res.ComponentTypes = compTypes;

            res.Diag = "rows=" + rows + " cols=" + cols + " componentTypes=" + compTypes;
            res.Verified = rows > 1 && cols > 0 && compTypes > 0;
            await emit("Scribe", null, res.Verified ? "done" : "fail",
                res.Verified ? "BOM table on \"" + targetViewName + "\": " + rows + " row(s), " + compTypes + " component type(s)" : res.Diag);

            if (!res.Verified)
            { res.Error = "Inserted, but couldn't independently verify a populated table (" + res.Diag + ")."; return res; }

            res.Info = "Inserted a BOM table on \"" + targetViewName + "\" (" + compTypes + " component type(s), " + rows + " row(s)).";
            return res;
        }

        private static bool HasBomFeature(IModelDoc2 model)
        {
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "BomFeat") return true;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return false;
        }

        // swFileLocationsBOMTemplates (a STRING preference despite the plural name — one folder, or several
        // delimited by ';'/',') — scan for a *.sldbomtbt, preferring bom-standard.sldbomtbt. Falls back to the
        // install's own lang\english folder (sibling of sldworks.exe), same install-relative-fallback shape
        // CreateDrawing.cs uses for its drawing template.
        private static string ResolveTemplate(ISldWorks app)
        {
            try
            {
                string raw = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsBOMTemplates);
                if (!string.IsNullOrEmpty(raw))
                {
                    foreach (var f in raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (string.IsNullOrEmpty(f) || !Directory.Exists(f)) continue;
                        var std = Path.Combine(f, "bom-standard.sldbomtbt");
                        if (File.Exists(std)) return std;
                        var any = Directory.GetFiles(f, "*.sldbomtbt").FirstOrDefault();
                        if (any != null) return any;
                    }
                }
            }
            catch { }
            try
            {
                string exeDir = Path.GetDirectoryName(app.GetExecutablePath());
                var fallback = Path.Combine(exeDir, "lang", "english", "bom-standard.sldbomtbt");
                if (File.Exists(fallback)) return fallback;
            }
            catch { }
            return null;
        }
    }
}
