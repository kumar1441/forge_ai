using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class InsertNewPartInContextResult
    {
        public bool Success;
        public string Plane;
        public string OutputPath;
        public int ErrorCode = -1;
        public int ComponentsBefore = -1;
        public int ComponentsAfter = -1;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// InsertNewPartInContext (tool 230 "insert_new_part_in_context") — top-down design: creates a brand-new PART
    /// document inside the ACTIVE ASSEMBLY, attached to a standard reference plane, so the new part's own sketches
    /// can go on to reference the assembly's existing geometry. Distinct from CreatePart (tool 228, a standalone
    /// new document with no assembly relationship — narrow "new/blank/empty part" phrase, no in-context wording)
    /// and InsertComponent (inserts an EXISTING file by path) — this one both CREATES the file AND positions it
    /// inside the assembly in one call (IAssemblyDoc.InsertNewPart2), confirmed live via reflection:
    /// swInsertNewPartErrorCode_e is 1-BASED (0=ErrorUnknown, 1=NoError, 2=FilePathEmpty, 3=FileAlreadyExists,
    /// 4=FolderDoesNotExist, 5=ExtensionNotSldPrt, 6=NotAFaceOrPlane, 7=CannotSelectFaceOrPlane) — a naive
    /// `err != 0` success check is WRONG (caught live: real success returns 1, not 0). No dialog risk, a real
    /// explicit-path, explicit-plane call, not an interactive "browse for template" prompt — but the second param
    /// ("Face_or_Plane_to_select") needs an ACTUAL SelectionMgr selection object, not a raw Feature handle (passing
    /// the Feature directly threw error 6 NotAFaceOrPlane on the first live run).
    ///
    /// v1 SCOPE LIMIT (documented, not guessed): attaches the new part to a standard ASSEMBLY reference plane
    /// (front/top/right, default front — same plane vocabulary CaptureSection/CaptureViewport already use), not an
    /// arbitrary existing COMPONENT's face. Referencing a component face is a real top-down pattern but a distinct,
    /// larger lift (resolving + selecting a nested-component face) — out of scope for v1, refused honestly.
    ///
    /// Verification (fail closed, Rule #6): the InsertNewPart2 error code alone is not trusted — independently
    /// re-reads the assembly's own component count (before vs after a FRESH GetComponents(false) call, must be
    /// +1) and confirms the new file actually landed on disk. Returns to normal assembly-edit state afterward
    /// (EditAssembly()) rather than leaving the caller stuck mid in-context-edit.
    /// </summary>
    public static class InsertNewPartInContext
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(create|add|insert|make|start)\b");
            bool newPart = Regex.IsMatch(c, @"\bnew\s+part\b");
            if (!verb || !newPart) return false;
            // must be explicitly TOP-DOWN / IN-CONTEXT — otherwise this is plain CreatePart (tool 228), which has
            // a matching exclusion so the two never both claim the same phrase.
            return Regex.IsMatch(c, @"\bin[\s-]?context\b|\bin[\s-]?place\b|\btop[\s-]?down\b|\breferenc(e|ing)\b|\battached?\s+to\b|\bin\s+(the|this)\s+assembly\b");
        }

        public static async Task<InsertNewPartInContextResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new InsertNewPartInContextResult();
            if (model == null) { res.Error = "Open an assembly to insert an in-context part."; return res; }
            int docType = 0; try { docType = model.GetType(); } catch { }
            if (docType != (int)swDocumentTypes_e.swDocASSEMBLY)
            { res.Error = "In-context parts can only be inserted into an ASSEMBLY, not this document type."; return res; }

            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Couldn't access the assembly document."; return res; }

            string planeName = ParsePlaneName(intent);
            res.Plane = planeName;
            var planeFeat = FindFeatureByName(model, planeName);
            if (planeFeat == null) { res.Error = "Couldn't find '" + planeName + "' in the assembly's feature tree to attach to."; return res; }

            int before = CountComponents(asm);
            res.ComponentsBefore = before;

            // Path includes the pre-insert component count: a rerun against the SAME reused assembly doc (the
            // harness's own open-once-per-model perf shape) must land at a DISTINCT path each time. Found live:
            // reusing the identical path a second call landed on lets InsertNewPart2 silently update the SAME
            // already-in-tree component's file in place instead of adding a genuinely new one (component count
            // stayed flat run1->run2 despite a fresh non-zero-byte file appearing on disk) — a real false-success
            // trap, not theorised. The independent GT re-derives this exact suffix from ONLY the live post-call
            // component count (count-1 == the before-count this call used), never trusting the handler's own field.
            string outPath = ResolveOutputPath(intent, before);
            res.OutputPath = outPath;
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }

            await emit("Architect", "creating a new in-context part on the " + planeName, "run", null);

            // InsertNewPart2's "Face_or_Plane_to_select" param needs a REAL selection (SelectionMgr object), not
            // a raw Feature handle — passing the Feature directly throws swInsertNewPartError_NotAFaceOrPlane (6).
            // Same SelectByID2("PLANE",...) shape every other plane-consuming handler (AddSweep/AddLoft/AddHelix/
            // AddRevolve/...) already uses.
            model.ClearSelection2(true);
            bool picked = false;
            try { picked = model.Extension.SelectByID2(planeName, "PLANE", 0, 0, 0, false, 0, null, 0); } catch { }
            if (!picked) { res.Error = "Couldn't select '" + planeName + "' to attach the new part to."; return res; }
            var selMgr = model.SelectionManager as SelectionMgr;
            object selPlane = null;
            try { selPlane = selMgr != null ? selMgr.GetSelectedObject6(1, -1) : null; } catch { }
            if (selPlane == null) { res.Error = "Couldn't resolve the selected '" + planeName + "' object."; return res; }
            string selName = null; try { selName = (selPlane as Feature)?.Name; } catch { }
            object specific = null; string specificType = null;
            try { specific = (selPlane as Feature)?.GetSpecificFeature2(); specificType = specific?.GetType().FullName; } catch { }
            res.Diag = "picked=" + picked + " selType=" + selPlane.GetType().FullName + " selName=" + selName + " specificType=" + specificType + " outPath=" + outPath;

            int err = -1;
            try
            {
                err = asm.InsertNewPart2(outPath, specific ?? selPlane);
            }
            catch (Exception ex) { res.Error = "InsertNewPart2 failed: " + ex.Message; return res; }
            res.ErrorCode = err;
            res.Diag += " err=" + err;

            if (err != (int)swInsertNewPartErrorCode_e.swInsertNewPartError_NoError)
            {
                res.Error = "SolidWorks couldn't create the in-context part (error code " + err + ").";
                await emit("Architect", null, "fail", res.Error);
                return res;
            }

            // InsertNewPart2 leaves the new part active for in-context sketch edit — return to normal assembly
            // editing so the caller's next command operates on the assembly, not a half-finished sketch session.
            try { asm.EditAssembly(); } catch { }
            try { model.ClearSelection2(true); } catch { }

            int after = CountComponents(asm);
            res.ComponentsAfter = after;

            bool fileLanded = false;
            try { fileLanded = File.Exists(outPath); } catch { }

            res.Diag = "err=" + err + " before=" + before + " after=" + after + " fileLanded=" + fileLanded;

            if (after != before + 1)
            {
                res.Error = "InsertNewPart2 reported success but the assembly's component count didn't rise by 1 (before=" + before + ", after=" + after + ").";
                await emit("Architect", null, "fail", res.Error);
                return res;
            }
            if (!fileLanded)
            {
                res.Error = "InsertNewPart2 reported success but no file landed at the expected path.";
                await emit("Architect", null, "fail", res.Error);
                return res;
            }

            res.Success = true;
            res.Info = "Created a new part in-context, attached to the " + planeName + " (component count " + before + "->" + after + "). " +
                "The new part's sketches can now reference the assembly's existing geometry. Forge didn't save.";
            await emit("Architect", null, "done", res.Info);
            return res;
        }

        // Deterministic (pure function of intent text + the pre-insert component count) — a scratch temp path the
        // independent GT can re-derive and check on disk itself, the same shape as CaptureSection.ResolveOutputPath.
        // The count suffix guarantees a rerun against the same reused doc never collides with its own prior file.
        public static string ResolveOutputPath(string intent, int beforeComponentCount)
        {
            string slug = ParsePlaneName(intent).Replace(" ", "").ToLowerInvariant();
            return Path.Combine(Path.GetTempPath(), "forge-incontext-part-" + slug + "-" + beforeComponentCount + ".SLDPRT");
        }

        private static string ParsePlaneName(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\btop\b|\bhorizontal\b")) return "Top Plane";
            if (Regex.IsMatch(c, @"\bright\b|\bside\b")) return "Right Plane";
            return "Front Plane";
        }

        private static Feature FindFeatureByName(IModelDoc2 model, string name)
        {
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (string.Equals(nm, name, StringComparison.OrdinalIgnoreCase)) return f;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return null;
        }

        internal static int CountComponents(AssemblyDoc asm)
        {
            try
            {
                var comps = asm.GetComponents(false) as object[];
                return comps == null ? 0 : comps.Length;
            }
            catch { return -1; }
        }
    }
}
