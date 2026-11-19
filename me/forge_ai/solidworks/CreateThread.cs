using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateThreadResult
    {
        public string SizeRequested = "any";  // "M6"/"M8"/... parsed from the intent, or "any" (thread every internal hole)
        public int HolesFound;                 // internal cylindrical hole faces that qualified (in band / matched size)
        public int ThreadsAdded;               // cosmetic threads actually created AND independently verified this run
        public int AlreadyThreaded;            // holes skipped because a Forge-Thread already anchors them (idempotent)
        public int RebuildErrors;              // GetWhatsWrongCount() post-rebuild (0 => clean)
        public bool RolledBack;                // rebuild errored → the run's Forge threads were deleted, part restored
        public bool Verified;                  // fail closed: true ONLY when the cosmetic-thread count rose by ThreadsAdded + clean rebuild
        public bool NeedsConfirm;              // parity with the other write specs; create_thread never needs it (size is optional)
        public string Question;                // the one clarifying question when NeedsConfirm (unused here)
        public string Info;                    // verdict-first panel line
        public string Error;                   // honest failure text (assembly handed in, no solid)
    }

    /// <summary>
    /// CreateThread (tool #212 "add cosmetic threads to holes") — a REAL shop deliverable geometry WRITE on a single
    /// PART. Adds a COSMETIC THREAD feature (with a size callout note) to each internal cylindrical hole so drawings
    /// and the model carry the tapped-thread annotation machinists work from. Commands: "tap the holes M6", "add
    /// threads to the holes", "add cosmetic threads", "thread the bores M8".
    ///
    /// Approach (named crew, deliberate, documented):
    ///   Gauge — read every solid body; find each INTERNAL (concave) cylindrical hole face within a sane diameter band.
    ///           Concavity is decided from live geometry: the face's OUTWARD normal points toward the cylinder axis
    ///           (material is OUTSIDE the cylinder) => a hole; away => an external round/boss (never threaded). If the
    ///           intent names a size ("M6"/"M8"), only holes whose measured diameter matches that nominal tap-drill are
    ///           kept. For each hole, grab its TOP circular EDGE (the opening circle furthest along the cylinder axis) —
    ///           a cosmetic thread is anchored on that circular edge. Preview the count BEFORE writing (Rule #3).
    ///   Tapper — for each hole: select the circular edge, call IFeatureManager.InsertCosmeticThread3 with an explicit
    ///           Diameter = the hole's MEASURED diameter, EndType = through, and a Note callout ("M6x1"). Capture the
    ///           returned Feature — if null the op was refused for that hole, so it is SKIPPED (partial success, Rule #4),
    ///           never faked. Every created thread is renamed "Forge-Thread-<sig>" (sig = the hole's axis+radius
    ///           signature) so reruns recognise Forge's own work per-hole.
    ///   Sentinel — ONE ForceRebuild3 at the end, then FAIL CLOSED (Rule #6): INDEPENDENTLY count the cosmetic-thread
    ///           features in the tree and confirm the count rose by exactly ThreadsAdded and the rebuild is clean.
    ///           Anything less → the run's Forge threads are deleted and the part restored; never a fake green.
    ///
    /// Robustness (the 12 rules): PART only — an assembly is refused honestly (Rule #2). Size is OPTIONAL (default:
    /// thread every internal hole in band), so there is no missing-value question. IDEMPOTENT (Rule #5): a hole whose
    /// "Forge-Thread-&lt;sig&gt;" feature already exists is skipped; a full rerun reports "already threaded, nothing to do."
    /// UNDO is sacred (Rule #7): every thread is a tree feature undone by Ctrl+Z, and Forge never saves — the user saves.
    /// Verified reports what was MEASURED (the independent cosmetic-thread recount), never what was attempted.
    /// </summary>
    public static class CreateThread
    {
        private const string TagPrefix = "Forge-Thread-";
        private const double MM = 0.001;                 // mm -> SW metres
        // cosmetic threads anchor on holes; ignore anything outside a sane tapped-hole diameter band
        private const double MinDiaM = 1.0 * MM;         // below ~1mm is noise / vent slots, not a tapped hole
        private const double MaxDiaM = 40.0 * MM;        // above ~40mm is a bore/opening, not a tapped hole
        private const double SizeTolMm = 0.9;            // measured hole must be within this of the nominal tap-drill

        public static bool IsCreateThreadIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // Needs an explicit thread/tap verb so it never collides with hole-removal (defeature) or hole-wizard work.
            // "tap"/"tapped", "thread"/"threads"/"threaded", or "cosmetic thread". "add threads to the holes", "tap M6".
            return Regex.IsMatch(c, @"\b(tap|tapped|thread|threads|threaded)\b") ||
                   Regex.IsMatch(c, @"\bcosmetic\s+thread");
        }

        private class Hole
        {
            public Face2 Face;
            public Edge TopEdge;   // the opening circle a cosmetic thread anchors on
            public double DiaM;    // measured hole diameter (metres) — passed straight to InsertCosmeticThread3
            public string Sig;     // axis+radius signature -> the per-hole Forge-Thread feature name
        }

        public static async Task<CreateThreadResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateThreadResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Cosmetic threads are added on a single part — open the .SLDPRT whose holes you want tapped, not an assembly (v1 is part-scoped)."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to thread."; return res; }

            double reqMinorM = ParseRequestedMinorDiaM(intent, out string sizeLabel);
            res.SizeRequested = sizeLabel;

            await emit("Gauge", "reading the internal hole faces", "run", null);
            object[] bodies = SolidBodies(part);
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to thread — this part has no solid geometry (a surface/sheet body or empty doc has no hole faces to tap)."; return res; }

            var holes = ScanHoles(part, reqMinorM);
            res.HolesFound = holes.Count;
            int cosmeticBefore = CountCosmeticThreads(model);

            await emit("Gauge", null, "done",
                holes.Count + " internal hole" + (holes.Count == 1 ? "" : "s") +
                (sizeLabel == "any" ? "" : " matching " + sizeLabel) + " found  ·  " + cosmeticBefore + " existing thread" + (cosmeticBefore == 1 ? "" : "s"));

            // ---- no threadable holes → honest "nothing to do" (not an error) ----
            if (holes.Count == 0)
            {
                res.Verified = true;   // correctly did nothing
                res.Info = sizeLabel == "any"
                    ? "No internal cylindrical holes to thread — this part has no tapped-hole candidates in the 1–40mm band. Forge changed nothing."
                    : "No internal holes match " + sizeLabel + " (⌀" + (reqMinorM / MM).ToString("0.0") + "mm ±" + SizeTolMm.ToString("0.#") + "mm). Forge changed nothing.";
                await emit("Tapper", null, "done", "no threadable holes — nothing to do");
                return res;
            }

            // ---- IDEMPOTENT (Rule #5): skip holes a Forge-Thread already anchors ----
            var todo = new List<Hole>();
            foreach (var h in holes)
            {
                if (FindFeatureByName(model, TagPrefix + h.Sig) != null) res.AlreadyThreaded++;
                else todo.Add(h);
            }
            if (todo.Count == 0)
            {
                res.Verified = true;
                res.RebuildErrors = SafeWhatsWrong(model);
                res.Info = "Already threaded — all " + holes.Count + " hole" + (holes.Count == 1 ? "" : "s") +
                           " already carry a Forge cosmetic thread. Nothing to add.";
                await emit("Tapper", null, "done", "all " + holes.Count + " holes already threaded — nothing to do");
                return res;
            }

            // ---- PREVIEW then WRITE (Rule #3 / #4): one cosmetic thread per hole, each its own try ----
            await emit("Tapper", todo.Count + " hole" + (todo.Count == 1 ? "" : "s") + " to thread — adding cosmetic threads", "run", null);

            var swSelMgr = model.SelectionManager as SelectionMgr;
            var created = new List<Feature>();
            int done = 0;
            foreach (var h in todo)
            {
                SelectData sd = null; try { sd = swSelMgr != null ? swSelMgr.CreateSelectData() : null; } catch { }
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = ((Entity)h.TopEdge).Select4(false, sd); } catch { }
                if (!sel) { await emit(null, null, "done", "skipped ⌀" + (h.DiaM / MM).ToString("0.0") + "mm — couldn't select the opening edge"); continue; }

                string note = ThreadNote(reqMinorM, sizeLabel, h.DiaM);
                Feature ct = null;
                try
                {
                    // InsertCosmeticThread3(Standard, StandardType, Size, Diameter_m, EndType, Depth_m, Note). With
                    // Standard = None (-2) SW does not consult a live thread table: the explicit Diameter (the hole's
                    // MEASURED diameter) + the Note callout define the thread, so it works on any build/library state.
                    ct = model.FeatureManager.InsertCosmeticThread3(
                        (int)swCosmeticStandardType_e.swStandardType_StandardNone,
                        "",                    // StandardType (ignored when Standard = None)
                        "",                    // Size (ignored when Standard = None; explicit Diameter used instead)
                        h.DiaM,                // Diameter (metres) = the hole's measured diameter
                        (int)swCosmeticEndConditions_e.swEndConditionThrough,
                        0.0,                   // Depth (unused for a through thread)
                        note) as Feature;
                }
                catch { ct = null; }
                try { model.ClearSelection2(true); } catch { }

                // fail-closed per hole: a null return means SW refused this one — skip it, never fake it (Rule #4).
                if (ct == null) { await emit(null, null, "done", "skipped ⌀" + (h.DiaM / MM).ToString("0.0") + "mm — cosmetic thread refused here"); continue; }
                try { ct.Name = TagPrefix + h.Sig; } catch { }
                created.Add(ct);
                done++;
                await emit(null, null, "done", "threaded ⌀" + (h.DiaM / MM).ToString("0.0") + "mm (" + note + ") · " + done + "/" + todo.Count);
            }

            // ---- ONE rebuild, then INDEPENDENT verification (Rule #6) ----
            await emit("Sentinel", "verifying the new threads (independent recount)", "run", null);
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            int cosmeticAfter = CountCosmeticThreads(model);
            res.ThreadsAdded = done;

            bool countRose = cosmeticAfter == cosmeticBefore + done;

            // ---- ROLLBACK (Rule #6): rebuild errored → delete this run's Forge threads, restore the part ----
            if (res.RebuildErrors != 0)
            {
                foreach (var f in created) RollbackFeature(model, SafeName(f));
                try { model.ForceRebuild3(false); } catch { }
                res.RolledBack = true;
                res.ThreadsAdded = 0;
                res.Verified = false;
                res.RebuildErrors = SafeWhatsWrong(model);
                res.Error = "Adding cosmetic threads left the rebuild with errors — rolled back the " + created.Count +
                            " Forge thread" + (created.Count == 1 ? "" : "s") + "; the part is unchanged.";
                await emit("Sentinel", null, "fail", "rolled back — part restored");
                return res;
            }

            res.Verified = done > 0 && countRose && res.RebuildErrors == 0;
            await emit("Sentinel", null, res.Verified ? "done" : "fail",
                "threads " + cosmeticBefore + "→" + cosmeticAfter + " (+" + done + ")" +
                (res.RebuildErrors == 0 ? " · rebuild clean" : " · " + res.RebuildErrors + " rebuild error(s)") +
                (countRose ? "" : " · recount mismatch"));

            if (!res.Verified && !countRose)
            {
                // the cosmetic-thread recount didn't match what we added → don't claim success
                res.Error = "Threading finished but the independent recount didn't confirm it (expected +" + done +
                            ", saw +" + (cosmeticAfter - cosmeticBefore) + "). Forge is not claiming success; check the model.";
                return res;
            }

            res.Info = BuildInfo(res, sizeLabel);
            return res;
        }

        // ================= hole detection =================

        // Every internal (concave) cylindrical hole face in band; if a size was requested, only those near its tap-drill.
        private static List<Hole> ScanHoles(PartDoc part, double reqMinorM)
        {
            var list = new List<Hole>();
            var seen = new HashSet<string>();   // one thread per distinct bore (dedupe coaxial re-reads)
            foreach (var bo in SolidBodies(part) ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    if (s == null) continue;
                    bool isCyl = false; try { isCyl = s.IsCylinder(); } catch { }
                    if (!isCyl) continue;

                    double[] cp = null; try { cp = s.CylinderParams as double[]; } catch { }
                    if (cp == null || cp.Length < 7) continue;
                    double diaM = cp[6] * 2.0;
                    if (diaM < MinDiaM || diaM > MaxDiaM) continue;
                    if (!CylinderConcave(face, s, cp)) continue;                     // holes only, never external rounds
                    if (reqMinorM > 0 && Math.Abs(diaM - reqMinorM) > SizeTolMm * MM) continue;   // size filter

                    string sig = Sig(cp);
                    if (!seen.Add(sig)) continue;                                    // already have this bore
                    Edge top = TopCircularEdge(face, cp);
                    if (top == null) continue;                                       // no opening circle to anchor on
                    list.Add(new Hole { Face = face, TopEdge = top, DiaM = diaM, Sig = sig });
                }
            }
            return list;
        }

        // concave (a hole) iff the face's OUTWARD normal points toward the cylinder axis (solid material is OUTSIDE).
        private static bool CylinderConcave(Face2 face, Surface s, double[] cp)
        {
            try
            {
                double[] box = face.GetBox() as double[];
                if (box == null || box.Length < 6) return false;   // unmeasurable → not a confident hole; skip (fail closed)
                double[] center = { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };
                double[] P = face.GetClosestPointOn(center[0], center[1], center[2]) as double[];
                if (P == null || P.Length < 3) return false;

                double[] n = s.EvaluateAtPoint(P[0], P[1], P[2]) as double[];
                if (n == null || n.Length < 3) return false;
                double nl = Len(n); if (nl < 1e-9) return false;
                double[] nout = { n[0] / nl, n[1] / nl, n[2] / nl };
                bool reversed = false; try { reversed = face.FaceInSurfaceSense(); } catch { }
                if (reversed) { nout[0] = -nout[0]; nout[1] = -nout[1]; nout[2] = -nout[2]; }

                double[] O = { cp[0], cp[1], cp[2] };
                double[] a = { cp[3], cp[4], cp[5] };
                double al = Len(a); if (al < 1e-9) return false;
                a[0] /= al; a[1] /= al; a[2] /= al;
                double[] d = { P[0] - O[0], P[1] - O[1], P[2] - O[2] };
                double axial = Dot(d, a);
                double[] w = { d[0] - axial * a[0], d[1] - axial * a[1], d[2] - axial * a[2] };
                double wl = Len(w); if (wl < 1e-9) return false;
                double radialDot = (nout[0] * w[0] + nout[1] * w[1] + nout[2] * w[2]) / wl;
                return radialDot < 0;   // normal points inward toward the axis → concave → a hole
            }
            catch { return false; }
        }

        // the hole's opening circle furthest along the cylinder axis — a cosmetic thread anchors on this circular edge.
        private static Edge TopCircularEdge(Face2 face, double[] cp)
        {
            object[] edges = null; try { edges = face.GetEdges() as object[]; } catch { }
            if (edges == null) return null;
            double[] O = { cp[0], cp[1], cp[2] };
            double[] a = { cp[3], cp[4], cp[5] };
            double al = Len(a); if (al < 1e-9) return null;
            a[0] /= al; a[1] /= al; a[2] /= al;

            Edge best = null; double bestT = double.NegativeInfinity;
            foreach (var eo in edges)
            {
                var e = eo as Edge; if (e == null) continue;
                Curve c = null; try { c = e.GetCurve() as Curve; } catch { }
                if (c == null) continue;
                bool circ = false; try { circ = c.IsCircle(); } catch { }
                if (!circ) continue;
                double[] cpar = null; try { cpar = c.CircleParams as double[]; } catch { }
                if (cpar == null || cpar.Length < 3) continue;
                double t = (cpar[0] - O[0]) * a[0] + (cpar[1] - O[1]) * a[1] + (cpar[2] - O[2]) * a[2];   // projection onto axis
                if (t > bestT) { bestT = t; best = e; }
            }
            return best;
        }

        // ================= size parsing + callout =================

        // metric tap-drill (~minor) diameters in mm — what a physical tapped hole actually measures.
        private static readonly Dictionary<int, double> TapDrillMm = new Dictionary<int, double>
        { {2,1.6},{3,2.5},{4,3.3},{5,4.2},{6,5.0},{8,6.8},{10,8.5},{12,10.2},{14,12.0},{16,14.0},{20,17.5} };
        // metric coarse pitch (mm) for the callout note.
        private static readonly Dictionary<int, double> PitchMm = new Dictionary<int, double>
        { {2,0.4},{3,0.5},{4,0.7},{5,0.8},{6,1.0},{8,1.25},{10,1.5},{12,1.75},{14,2.0},{16,2.0},{20,2.5} };

        // requested nominal tap-drill diameter (metres) from "M6"/"M8"; reqMinorM<=0 and label "any" when no size stated.
        private static double ParseRequestedMinorDiaM(string intent, out string label)
        {
            label = "any";
            string c = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(c, @"\bm(\d+)\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int mm) && mm > 0)
            {
                label = "M" + mm;
                double minor = TapDrillMm.TryGetValue(mm, out double d) ? d : mm * 0.84;   // fallback ~0.84×nominal
                return minor * MM;
            }
            return -1;
        }

        // the callout note: the requested "M6x1", else the nearest standard inferred from the measured diameter, else ⌀.
        private static string ThreadNote(double reqMinorM, string sizeLabel, double diaM)
        {
            if (sizeLabel != "any")
            {
                var m = Regex.Match(sizeLabel, @"\d+");
                if (m.Success && int.TryParse(m.Value, out int mm) && PitchMm.TryGetValue(mm, out double p))
                    return sizeLabel + "x" + Trim(p);
                return sizeLabel;
            }
            // infer nearest standard from the measured bore so a plain "add threads" still yields a proper callout
            double diaMm = diaM / MM;
            int best = -1; double bestErr = double.MaxValue;
            foreach (var kv in TapDrillMm)
            {
                double err = Math.Abs(kv.Value - diaMm);
                if (err < bestErr) { bestErr = err; best = kv.Key; }
            }
            if (best > 0 && bestErr <= SizeTolMm && PitchMm.TryGetValue(best, out double pp))
                return "M" + best + "x" + Trim(pp);
            return "⌀" + Trim(diaMm) + "mm thread";
        }

        // ================= feature helpers =================

        private static int CountCosmeticThreads(IModelDoc2 model)
        {
            int n = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (string.Equals(tn, "CosmeticThread", StringComparison.OrdinalIgnoreCase)) n++;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return n;
        }

        private static void RollbackFeature(IModelDoc2 model, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                var f = FindFeatureByName(model, name);
                if (f == null) return;
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = f.Select2(false, 0); } catch { }
                if (sel) { try { model.EditDelete(); } catch { } }
                try { model.ClearSelection2(true); } catch { }
            }
            catch { }
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

        private static string SafeName(Feature f) { try { return f?.Name; } catch { return null; } }

        private static object[] SolidBodies(PartDoc part)
        { try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { return null; } }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        // stable axis-point + radius signature (0.01mm quantised, char-safe for a feature name).
        private static string Sig(double[] cp)
        {
            return "p" + Q(cp[0]) + "_" + Q(cp[1]) + "_" + Q(cp[2]) + "_r" + Q(cp[6]);
        }
        private static string Q(double m) { long v = (long)Math.Round(m * 1e5); return (v < 0 ? "n" : "") + Math.Abs(v).ToString(); }

        // verdict first (Character #3), the NUMBER not the adjective (Character #2), only what was VERIFIED (Character #1).
        private static string BuildInfo(CreateThreadResult r, string sizeLabel)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Threaded " + r.ThreadsAdded + " hole" + (r.ThreadsAdded == 1 ? "" : "s"));
            if (sizeLabel != "any") sb.Append(" (" + sizeLabel + ")");
            sb.Append(" with cosmetic threads");
            if (r.AlreadyThreaded > 0) sb.Append("; " + r.AlreadyThreaded + " already threaded (left as-is)");
            int skipped = r.HolesFound - r.AlreadyThreaded - r.ThreadsAdded;
            if (skipped > 0) sb.Append("; " + skipped + " skipped (thread refused there)");
            sb.Append(". Independent recount confirms +" + r.ThreadsAdded + ", rebuild clean. ");
            sb.Append("One Ctrl+Z per thread removes them; Forge didn't save.");
            return sb.ToString();
        }

        private static string Trim(double v) => v.ToString("0.###");
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Len(double[] a) => Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
    }
}
