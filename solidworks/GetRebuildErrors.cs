using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetRebuildErrorsResult
    {
        public int Total;         // features flagged by GetWhatsWrong (errors + warnings)
        public int ErrorCount;    // IsWarning == false
        public int WarningCount;  // IsWarning == true
        public List<string> Items = new List<string>();
        public int WalkErr;       // independent per-feature GetErrorCode2 walk (a DIFFERENT API)
        public int WalkWarn;
        public int WhatsWrongCount; // scalar cross-ref
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 96 — get_rebuild_errors (READ). Enumerates the features SolidWorks reports as failed or warned on the last
    /// rebuild, with each feature's name and the specific error/warning code — the detail behind a red rebuild flag.
    /// Distinct from fix_red_wave (the fixer) and the assembly doctor (a full health scan): this just LISTS the problems.
    /// Primary API: IModelDocExtension.GetWhatsWrong(out names, out codes, out warnings). Read-only; the independent GT
    /// re-derives the count from a per-feature IFeature.GetErrorCode2 walk (a genuinely different API path).
    /// </summary>
    public static class GetRebuildErrors
    {
        // NARROW: an error-noun tied to rebuild/feature vocabulary, excluding the fix verbs (RedWave) and the
        // verification-on-rebuild switch (SetRebuildVerification, which has no error noun anyway).
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(fix|repair|clear|resolve|remove|clean)\b")) return false;
            if (System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(rebuild error|rebuild errors|feature error|feature errors|rebuild failure|rebuild failures|failed feature|failed features)\b"))
                return true;
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(list|show|what|which|any|how many)\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\berrors?\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(rebuild|feature|features)\b");
        }

        public static async Task<GetRebuildErrorsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetRebuildErrorsResult();
            if (model == null) { res.Error = "Open a part or assembly to list its rebuild errors."; return res; }
            try { model.ForceRebuild3(false); } catch { }

            await emit("Sentinel", "reading rebuild status", "run", null);

            object feats = null, codes = null, warns = null;
            bool apiRet = false;
            try { apiRet = model.Extension.GetWhatsWrong(out feats, out codes, out warns); }
            catch (Exception ex) { res.Diag = "GetWhatsWrong threw: " + ex.Message; }

            object[] fa = feats as object[];
            object[] ca = codes as object[];
            object[] wa = warns as object[];

            int n = fa == null ? 0 : fa.Length;
            for (int i = 0; i < n; i++)
            {
                string nm = fa[i] == null ? "(unnamed)" : fa[i].ToString();
                int code = 0; try { if (ca != null && i < ca.Length && ca[i] != null) code = Convert.ToInt32(ca[i]); } catch { }
                bool warn = false; try { if (wa != null && i < wa.Length && wa[i] != null) warn = Convert.ToBoolean(wa[i]); } catch { }
                if (warn) res.WarningCount++; else res.ErrorCount++;
                res.Items.Add(nm + " — " + CodeName(code) + (warn ? " (warning)" : " (error)"));
            }
            res.Total = n;

            // INDEPENDENT cross-read: per-feature GetErrorCode2 walk — a different API than GetWhatsWrong.
            int walkErr = 0, walkWarn = 0;
            try { WalkFeatureErrors(model, out walkErr, out walkWarn); } catch { }
            res.WalkErr = walkErr; res.WalkWarn = walkWarn;
            int wwCount = 0; try { wwCount = model.Extension.GetWhatsWrongCount(); } catch { }
            res.WhatsWrongCount = wwCount;
            res.Diag = "getWhatsWrong=" + n + " whatsWrongCount=" + wwCount + " walkErr=" + walkErr + " walkWarn=" + walkWarn + " apiRet=" + apiRet;

            await emit("Sentinel", null, "done", res.Total + " rebuild problem(s) — " + res.ErrorCount + " error(s), " + res.WarningCount + " warning(s)");

            if (res.Total == 0) { res.Info = "No rebuild errors or warnings — the model rebuilds clean."; return res; }
            var sb = new StringBuilder(res.Total + " rebuild problem" + (res.Total == 1 ? "" : "s") + " (" +
                res.ErrorCount + " error" + (res.ErrorCount == 1 ? "" : "s") + ", " +
                res.WarningCount + " warning" + (res.WarningCount == 1 ? "" : "s") + "):");
            int shown = 0;
            foreach (var it in res.Items) { if (shown++ >= 20) { sb.Append("\n… (" + (res.Total - 20) + " more)"); break; } sb.Append("\n• " + it); }
            res.Info = sb.ToString();
            return res;
        }

        private static void WalkFeatureErrors(IModelDoc2 model, out int err, out int warn)
        {
            err = 0; warn = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                CheckFeat(f, ref err, ref warn);
                var s = f.GetFirstSubFeature() as Feature;
                while (s != null) { CheckFeat(s, ref err, ref warn); s = s.GetNextSubFeature() as Feature; }
                f = f.GetNextFeature() as Feature;
            }
        }

        private static void CheckFeat(Feature f, ref int err, ref int warn)
        {
            int code = 0; bool isWarn = false;
            try { code = f.GetErrorCode2(out isWarn); } catch { return; }
            if (code == (int)swFeatureError_e.swFeatureErrorNone) return;
            if (isWarn) warn++; else err++;
        }

        private static string CodeName(int code)
        {
            try { string n = Enum.GetName(typeof(swFeatureError_e), code); return n ?? ("code " + code); }
            catch { return "code " + code; }
        }
    }
}
