using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class InsertComponentResult
    {
        public string InsertPath;
        public int BeforeTotal;
        public int AfterTotal;
        public int Inserted;             // net components added (independent recount)
        public double[] At = new double[] { 0, 0, 0 };
        public bool Verified;
        public int RebuildErrors;
        public bool NeedsConfirm;
        public string Question;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// InsertComponent (tool #29 insert_component, WRITE) — add a part into the active assembly at a point (origin by
    /// default). "insert C:\parts\bracket.SLDPRT", "add the washer from <path> at the origin". NOT idempotent by design —
    /// each call adds one instance, which is the honest primitive behavior (two calls = two instances).
    ///
    /// API (proven-live via the fixture generator's MakeAssembly): AssemblyDoc.AddComponent5(file, CurrentSelectedConfig,
    /// ...) — BUT the part must be pre-opened (OpenDoc6) or it silently no-ops, and the returned Component2 cast can be
    /// null even on success, so success is verified by an INDEPENDENT tree RECOUNT (Rule #6), never the returned object.
    /// UNDO is sacred (Rule #7); Forge never saves.
    /// </summary>
    public static class InsertComponent
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(insert|add|place|drop in|bring in)\b")) return false;
            // must reference a FILE — this separates it from add_boss/add_hole/add_pocket (feature adds carry no file)
            bool hasFile = Regex.IsMatch(c, "[\"'][^\"']+[\"']") || Regex.IsMatch(c, @"\.sld(prt|asm)\b");
            if (!hasFile) return false;
            // a component/part word OR just a bare path is fine; but exclude feature words to be safe
            if (Regex.IsMatch(c, @"\b(boss|extrude|fillet|chamfer|pocket|hole|rib|draft|shell|thread|equation|dimension|mate)\b")) return false;
            return true;
        }

        public static async Task<InsertComponentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new InsertComponentResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to insert a component."; return res; }

            string cmd = intent ?? "";
            await emit("Gauge", "resolving the part to insert", "run", null);

            string path = null;
            var q = Regex.Match(cmd, "[\"']([^\"']+)[\"']");
            if (q.Success) path = q.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(path)) { var mp = Regex.Match(cmd, @"([A-Za-z]:\\[^""']+?\.sld(?:prt|asm))", RegexOptions.IgnoreCase); if (mp.Success) path = mp.Groups[1].Value.Trim(); }
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                res.NeedsConfirm = true;
                res.Question = "Which part should I insert? Give the full path to the .SLDPRT.";
                await emit("Gauge", null, "ask", "insert path missing/not found");
                return res;
            }
            res.InsertPath = path;

            // optional placement: "at 10,0,0" (mm) — default origin
            var pm = Regex.Match(cmd, @"at\s+(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            if (pm.Success) res.At = new[] { double.Parse(pm.Groups[1].Value), double.Parse(pm.Groups[2].Value), double.Parse(pm.Groups[3].Value) };

            res.BeforeTotal = CountComps(asm);

            // ---- pre-open the part (AddComponent5 silently no-ops otherwise on this build) ----
            int oe = 0, ow = 0;
            try { app.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow); } catch { }
            try { model.ClearSelection2(true); } catch { }

            await emit("Gauge", null, "done", "inserting " + Path.GetFileName(path) + " at (" + string.Join(",", res.At) + ")mm");
            await emit("Scribe", "adding the component", "run", null);

            double m = 0.001;
            try { asm.AddComponent5(path, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, "", false, "", res.At[0] * m, res.At[1] * m, res.At[2] * m); }
            catch (Exception ex) { res.Error = "AddComponent5 threw (" + ex.GetType().Name + ") — the assembly is unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { model.ClearSelection2(true); } catch { }
            try { model.EditRebuild3(); } catch { try { model.ForceRebuild3(false); } catch { } }
            res.RebuildErrors = SafeWhatsWrong(model);

            // ---- Sentinel: FAIL CLOSED — recount + confirm an instance of the file is present ----
            await emit("Sentinel", "verifying the component was added", "run", null);
            res.AfterTotal = CountComps(asm);
            res.Inserted = res.AfterTotal - res.BeforeTotal;
            bool filePresent = false;
            string want = Norm(path);
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                string p = null; try { p = c.GetPathName(); } catch { }
                if (Norm(p) == want) { filePresent = true; break; }
            }
            res.Verified = res.Inserted == 1 && filePresent && res.RebuildErrors == 0;
            res.Diag = "before=" + res.BeforeTotal + " after=" + res.AfterTotal + " inserted=" + res.Inserted + " filePresent=" + filePresent + " rebuildErr=" + res.RebuildErrors;

            if (!res.Verified)
            {
                res.Error = res.Inserted != 1 ? "Expected the count to rise by 1, it changed by " + res.Inserted + ". " + res.Diag
                          : res.RebuildErrors > 0 ? "The insert left " + res.RebuildErrors + " rebuild error(s). " + res.Diag
                          : "The inserted file wasn't found in the tree. " + res.Diag;
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Inserted " + Path.GetFileName(path) + " (" + res.AfterTotal + " components now). One Ctrl+Z removes it; Forge didn't save.";
            await emit("Sentinel", null, "done", "added · " + res.AfterTotal + " components · clean");
            return res;
        }

        private static int CountComps(AssemblyDoc asm)
        {
            int n = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (!sup) n++;
            }
            return n;
        }

        private static string Norm(string p) { return string.IsNullOrEmpty(p) ? "" : p.Trim().ToLowerInvariant().Replace('/', '\\'); }
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
