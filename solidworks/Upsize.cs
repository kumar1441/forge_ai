using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class UpsizeResult
    {
        public string From;              // "M6"
        public string To;                // "M8"
        public int Found;                // matching fasteners (the old size) found
        public int Switched;             // verified swapped to the new-size config (read-back confirmed)
        public int AlreadyNew;           // already at the new size on this run (idempotency)
        public int NoConfig;             // matched, but the part has no new-size config to switch to
        public int LengthKept;           // of the switched, how many kept their original length signature
        public int OldClearanceHoles;    // INDEPENDENT scan: clearance holes still sized for the OLD bolt (offer only)
        public bool RebuildClean = true;
        public string Info;              // one-line verdict + the offer (Rule/Char #5: offer, never do)
        public string Error;
    }

    /// <summary>
    /// Demo #6 - "Replace all M6 bolts with M8, keep lengths." Two phases, one honest report:
    ///   Swap     -> each M6 fastener's size CONFIGURATION is switched to its M8 config (Toolbox parts carry a config
    ///              per size), preferring the config that keeps the SAME length (the "keep lengths" ask).
    ///   Sentinel -> the swap is verified by READING THE CONFIG BACK (fail closed - never trust the set call).
    ///   Caliper  -> an INDEPENDENT geometry scan counts the clearance holes still sized for the OLD bolt, so Forge can
    ///              OFFER to open them - it never touches a hole (Char #5: anticipate the next need, offer, don't do).
    /// Idempotent: a second run sees the fasteners already at the new size and swaps nothing. Never saves the document.
    /// </summary>
    public static class Upsize
    {
        // bolt/screw vocabulary (nuts are handled with their bolt, not "upsized" by this bolt-only command)
        private static readonly string[] BoltHints =
            { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud", "vis", "boulon", "din", "iso", "b18" };
        private static readonly string[] NotBolt =
            { "nut", "ecrou", "washer", "rondelle", "4032", "4034", "4035", "934", "985" };

        private static bool IsBoltName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            foreach (var x in NotBolt) if (n.Contains(x)) return false;
            foreach (var h in BoltHints) if (n.Contains(h)) return true;
            return false;
        }

        // A metric size token: "m6" as a standalone size, tolerant of "M6x30" / "M6 x 30" / "-M6-", rejecting "M60"/"am6".
        private static bool HasSize(string text, string num)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return Regex.IsMatch(text.ToLowerInvariant(), @"(?<![a-z0-9])m" + num + @"(?![0-9])");
        }

        // Length signature = the largest integer token that ISN'T the size number - Toolbox configs put the length
        // (20, 30, 50...) as the biggest number, pitch (1, 1.25) as the smallest. Best-effort, used only to prefer a
        // same-length target so "keep lengths" holds; falls back gracefully when it can't tell.
        private static int LenSig(string cfg, string sizeNum)
        {
            if (string.IsNullOrEmpty(cfg)) return -1;
            int sz; int.TryParse(sizeNum, out sz);
            int best = -1; bool droppedSize = false;
            foreach (Match m in Regex.Matches(cfg, @"\d+"))
            {
                int v; if (!int.TryParse(m.Value, out v)) continue;
                if (!droppedSize && v == sz) { droppedSize = true; continue; }   // skip ONE occurrence of the size itself
                if (v > best) best = v;
            }
            return best;
        }

        // Pick the new-size config, preferring one whose length signature matches the current config's (keep lengths).
        private static string ChooseTargetConfig(string[] cfgs, string toNum, string curCfg)
        {
            if (cfgs == null) return null;
            var matches = new List<string>();
            foreach (var cf in cfgs) if (HasSize(cf, toNum)) matches.Add(cf);
            if (matches.Count == 0) return null;
            int wantLen = LenSig(curCfg, /*old size stripped inside*/ ExtractSizeNum(curCfg) ?? toNum);
            if (wantLen > 0)
                foreach (var cf in matches) if (LenSig(cf, toNum) == wantLen) return cf;
            return matches[0];
        }

        private static string ExtractSizeNum(string cfg)
        {
            if (string.IsNullOrEmpty(cfg)) return null;
            var m = Regex.Match(cfg.ToLowerInvariant(), @"(?<![a-z0-9])m(\d+)(?![0-9])");
            return m.Success ? m.Groups[1].Value : null;
        }

        public static async Task<UpsizeResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new UpsizeResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to upsize fasteners."; return res; }
            var mu = (MathUtility)app.GetMathUtility();

            // ---- parse the from/to sizes (never keyword-match the raw text elsewhere; ask if it's not two sizes) ----
            string cmdLower = (intent ?? "").ToLowerInvariant();
            var sizes = Regex.Matches(cmdLower, @"\bm(\d+)\b");
            if (sizes.Count < 2) { res.Error = "Tell me both sizes, e.g. \"replace all M6 bolts with M8\"."; return res; }
            // "replace M6 with M8" mentions FROM first, TO second — but "make it M8 instead of M6" mentions the
            // TARGET size first and names the CURRENT size second (test-loop wrong-route 684dbf7d0445-class: "make
            // the bolts M8 instead of M6" was read as from=M8/to=M6, so it searched for M8 bolts to swap - none
            // existed - and silently did nothing). "instead of" reverses the mention order relative to the default.
            bool insteadOf = Regex.IsMatch(cmdLower, @"\binstead of\b");
            string fromNum = insteadOf ? sizes[1].Groups[1].Value : sizes[0].Groups[1].Value;
            string toNum = insteadOf ? sizes[0].Groups[1].Value : sizes[1].Groups[1].Value;
            res.From = "M" + fromNum; res.To = "M" + toNum;
            double oldNom = double.Parse(fromNum, CultureInfo.InvariantCulture);
            double newNom = double.Parse(toNum, CultureInfo.InvariantCulture);

            // ---- Swap: find the old-size bolts and switch each to its new-size config (length-preserving) ----
            await emit("Swap", "finding " + res.From + " bolts", "run", null);
            object[] comps = asm.GetComponents(false) as object[];
            var toSwitch = new List<KeyValuePair<Component2, string>>();   // (instance, target config) chosen this run
            string diag = "";
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2;
                if (c == null || c.IsSuppressed()) continue;
                string nm = c.Name2 ?? "";
                if (!IsBoltName(nm)) continue;

                string cur = null; try { cur = c.ReferencedConfiguration; } catch { }
                bool curIsNew = HasSize(cur, toNum);
                bool curIsOld = HasSize(cur, fromNum);
                bool nameOld = HasSize(nm, fromNum);
                if (!(nameOld || curIsOld || curIsNew)) continue;   // not a member of the from/to family - leave it alone

                res.Found++;
                if (curIsNew) { res.AlreadyNew++; continue; }        // idempotency: already the new size, nothing to do

                var pd = c.GetModelDoc2() as IModelDoc2;
                string[] cfgs = pd?.GetConfigurationNames() as string[];
                if (diag.Length == 0) diag = "cfgs=[" + (cfgs != null ? string.Join(",", cfgs) : "none") + "]";
                string target = ChooseTargetConfig(cfgs, toNum, cur);
                if (target == null) { res.NoConfig++; continue; }
                int wantLen = LenSig(cur, fromNum), gotLen = LenSig(target, toNum);
                if (wantLen > 0 && wantLen == gotLen) res.LengthKept++;
                try { c.ReferencedConfiguration = target; toSwitch.Add(new KeyValuePair<Component2, string>(c, target)); } catch { }
            }
            await emit("Swap", null, "done",
                "found " + res.Found + " " + res.From + " bolt" + (res.Found == 1 ? "" : "s") +
                (res.AlreadyNew > 0 ? " | " + res.AlreadyNew + " already " + res.To : "") +
                (toSwitch.Count > 0 ? " | switching " + toSwitch.Count + " -> " + res.To : "") +
                (diag.Length > 0 ? " | " + diag : ""));

            // Rebuild so the swapped configs re-resolve, then force a viewport redraw so the fatter M8 shanks are
            // VISIBLY updated on screen (a config swap can leave the last-drawn geometry cached until a redraw).
            if (toSwitch.Count > 0) { try { model.EditRebuild3(); } catch { } try { model.GraphicsRedraw2(); } catch { } }

            // ---- Sentinel: verify each swap by READING THE CONFIG BACK (fail closed) ----
            if (toSwitch.Count > 0)
            {
                await emit("Sentinel", "verifying the swaps", "run", null);
                foreach (var kv in toSwitch)
                {
                    string back = null; try { back = kv.Key.ReferencedConfiguration; } catch { }
                    if (back == kv.Value && HasSize(back, toNum)) res.Switched++;
                }
                res.RebuildClean = SafeWrong(model) == 0;
                await emit("Sentinel", null, "done",
                    res.Switched + " of " + toSwitch.Count + " confirmed at " + res.To +
                    (res.LengthKept > 0 ? " (lengths kept on " + res.LengthKept + ")" : "") +
                    (res.RebuildClean ? " | rebuild clean" : " | rebuild flagged"));
            }

            // ---- Caliper: INDEPENDENT scan for clearance holes still sized for the OLD bolt (the catch) ----
            await emit("Caliper", "checking the clearance holes", "run", null);
            res.OldClearanceHoles = CountOldClearanceHoles(mu, comps, oldNom, newNom);
            await emit("Caliper", null, "done",
                res.OldClearanceHoles > 0
                    ? res.OldClearanceHoles + " clearance hole" + (res.OldClearanceHoles == 1 ? "" : "s") + " still " + res.From
                    : "clearance holes already clear " + res.To);

            // ---- one honest verdict + a single offer (Char #5: offer, never do the hole work unasked) ----
            if (res.Found == 0) { res.Error = "No " + res.From + " bolts found to upsize."; return res; }

            string swapLine;
            if (res.Switched > 0)
                swapLine = "Swapped " + res.Switched + " bolt" + (res.Switched == 1 ? "" : "s") + " " + res.From + " -> " + res.To +
                           (res.LengthKept == res.Switched ? " (lengths kept)." : ".");
            else if (res.AlreadyNew == res.Found)
                swapLine = "All " + res.Found + " " + res.From + "-family bolts are already " + res.To + " - nothing to swap.";
            else if (res.NoConfig > 0 && res.Switched == 0)
                swapLine = "Found " + res.Found + " " + res.From + " bolts, but " + res.NoConfig + " have no " + res.To + " configuration to switch to.";
            else
                swapLine = "Nothing to swap.";

            string offer = res.OldClearanceHoles > 0
                ? " " + res.OldClearanceHoles + " clearance hole" + (res.OldClearanceHoles == 1 ? " is" : "s are") +
                  " still " + res.From + " - want me to open them to " + res.To + " clearance too?"
                : "";

            res.Info = swapLine + offer;
            return res;
        }

        // Count clearance holes whose diameter still fits the OLD bolt (and is too small for the NEW one) - flange/plate
        // holes only (never a bolt shank or a nut bore). Own cylinder collection (assembly coords), deduped by axis so a
        // hole's split faces count once. Band = just-oversize of the old nominal, capped below the new nominal shank.
        private static int CountOldClearanceHoles(MathUtility mu, object[] comps, double oldNomMm, double newNomMm)
        {
            double lo = oldNomMm * 1.02;                              // clearance holes sit just above nominal
            double hi = Math.Min(oldNomMm + 1.6, newNomMm - 0.2);    // typical loose ceiling, but still too small for the new bolt
            if (hi <= lo) return 0;

            var seen = new HashSet<string>();
            int count = 0;
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2;
                if (c == null || c.IsSuppressed()) continue;
                string nm = c.Name2 ?? "";
                if (IsBoltName(nm)) continue;
                string low = nm.ToLowerInvariant();
                if (low.Contains("nut") || low.Contains("ecrou")) continue;   // a nut bore is not a clearance hole

                foreach (var cy in CollectCyls(mu, c))
                {
                    double dia = cy.R * 2.0 * 1000.0;
                    if (dia < lo || dia > hi) continue;
                    // dedupe by the axis line (foot of perpendicular from world origin) + radius, snapped
                    double proj = cy.O[0] * cy.D[0] + cy.O[1] * cy.D[1] + cy.O[2] * cy.D[2];
                    double fx = cy.O[0] - proj * cy.D[0], fy = cy.O[1] - proj * cy.D[1], fz = cy.O[2] - proj * cy.D[2];
                    string key = Snap(fx) + "|" + Snap(fy) + "|" + Snap(fz) + "|" +
                                 Snap(Math.Abs(cy.D[0])) + "|" + Snap(Math.Abs(cy.D[1])) + "|" + Snap(Math.Abs(cy.D[2])) + "|" +
                                 Math.Round(dia, 1).ToString(CultureInfo.InvariantCulture);
                    if (seen.Add(key)) count++;
                }
            }
            return count;
        }

        private static string Snap(double v) => Math.Round(v * 2000.0).ToString(CultureInfo.InvariantCulture); // 0.5mm grid

        private class UCy { public double[] O; public double[] D; public double R; }

        private static List<UCy> CollectCyls(MathUtility mu, Component2 comp)
        {
            var list = new List<UCy>();
            try
            {
                var xf = comp.Transform2; object bi;
                object[] bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                if (bodies == null) return list;
                foreach (var bo in bodies)
                {
                    var body = bo as Body2; if (body == null) continue;
                    object[] faces = body.GetFaces() as object[]; if (faces == null) continue;
                    foreach (var fo in faces)
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var s = face.GetSurface() as Surface; if (s == null || !s.IsCylinder()) continue;
                        double[] cp = s.CylinderParams as double[]; if (cp == null || cp.Length < 7) continue;
                        var op = (MathPoint)((MathPoint)mu.CreatePoint(new[] { cp[0], cp[1], cp[2] })).MultiplyTransform(xf);
                        var dv = (MathVector)((MathVector)mu.CreateVector(new[] { cp[3], cp[4], cp[5] })).MultiplyTransform(xf);
                        double[] oa = op.ArrayData as double[]; double[] da = dv.ArrayData as double[];
                        double dl = Math.Sqrt(da[0] * da[0] + da[1] * da[1] + da[2] * da[2]); if (dl < 1e-9) continue;
                        list.Add(new UCy { O = new[] { oa[0], oa[1], oa[2] }, D = new[] { da[0] / dl, da[1] / dl, da[2] / dl }, R = cp[6] });
                    }
                }
            }
            catch { }
            return list;
        }

        private static int SafeWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
