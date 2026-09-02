using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// CreateOnTop — the SECOND step of a compound create ("create X and add Y on top"). Given the part the primary
    /// create handler just built, it fuses ONE extra shape onto that part's TOP face WITHOUT creating a new document:
    /// find the top planar face, sketch the secondary's profile on it (annulus / circle / rect from the secondary
    /// intent's dims or sane defaults), boss-extrude OUTWARD (merge=true), rebuild, and INDEPENDENTLY verify by the
    /// mass-property volume delta (the part's volume must rise by ≈ the added shape's volume) — the exact fail-closed
    /// AddBoss spine (select face → project centroid → circle/rect → FeatureExtrusion3 → measure → max-1 flip retry →
    /// volume cross-check → roll back on anything short). Never saves.
    ///
    /// Buildable secondaries: flange (annulus — outer/bore/thickness), cylinder (solid circle), boss/block (circle when
    /// a single size is stated, rect for an LxWxT spec), plate/block (rect). Everything the parser routes here but this
    /// builder can't fuse (sphere/cone/torus/tube) returns an honest "{ error = "adding X on top isn't supported yet" }"
    /// — never attempted. A curved-top primary (a sphere has no flat face) also fails closed with an honest error.
    /// </summary>
    public static class CreateOnTop
    {
        private const double MM = 0.001;                    // mm -> SW metres
        private const string Tag = "Forge-OnTop";           // feature tag -> one Ctrl+Z / rollback handle

        // the ONLY secondaries this builder can fuse (extrude-based). sphere/cone/torus/tube fall through to an honest error.
        private static readonly string[] Buildable =
        {
            "flange", "cylinder", "boss", "block", "plate"
        };

        /// <summary>
        /// Add one <paramref name="shape"/> feature onto the top face of the existing <paramref name="part"/> (never a new
        /// document). <paramref name="secondaryIntent"/> is the raw "add …" clause ("add a 20mm boss on top") whose dims drive
        /// the profile; sane defaults fill anything unstated. Returns { shape, position, created, verified, error, info,
        /// addedMm3, expectedMm3 }. Fail closed: if the top face can't be found or the extrude can't be verified, the part is
        /// left unchanged and the error is honest.
        /// </summary>
        public static async Task<JObject> AddOnTopFace(ISldWorks app, IModelDoc2 part, string shape, string secondaryIntent,
            Func<string, string, string, string, Task> emit)
        {
            var res = new JObject();
            res["created"] = false;
            res["verified"] = false;
            res["addedMm3"] = -1;
            res["expectedMm3"] = -1;

            string sh = (shape ?? "").Trim().ToLowerInvariant();
            res["shape"] = sh;

            if (!IsBuildable(sh))
            {
                res["error"] = "adding " + (string.IsNullOrEmpty(sh) ? "that shape" : sh) + " on top isn't supported yet";
                return res;
            }

            if (part == null || (int)part.GetType() != (int)swDocumentTypes_e.swDocPART)
            {
                res["error"] = "adding a " + sh + " on top works on a single part — the primary create didn't leave a part document open.";
                return res;
            }

            var partDoc = part as PartDoc;
            object[] bodies = SolidBodies(partDoc);
            if (bodies == null || bodies.Length == 0)
            {
                res["error"] = "No solid body to build on — the primary part has no solid geometry (a surface body or an empty doc has no face to stand a " + sh + " on).";
                return res;
            }

            await Say(emit, "Gauge", "finding the top face of the primary part", "run", null);
            TopFace top = ResolveTopFace(bodies);
            if (top == null)
            {
                // the primary's top is curved (sphere/cone apex/torus) -> no flat landing; honest refusal, never a guess.
                res["error"] = "couldn't find a flat top face to stand the " + sh + " on — the primary's top is a round/curved surface, so adding a " + sh + " on top of that isn't supported yet";
                return res;
            }

            Profile profile = ParseProfile(sh, secondaryIntent ?? "");
            if (profile == null)
            {
                res["error"] = "I couldn't read a usable size for that " + sh + " from \"" + (secondaryIntent ?? "").Trim() + "\" — say e.g. \"add a 20mm " + sh + "\" or \"add a " + sh + " 60mm outer 20mm bore 10mm thick on top\".";
                return res;
            }

            string fit = FitProblem(sh, profile, top);
            if (fit != null)
            {
                res["error"] = fit;
                return res;
            }

            double expectedMm3 = profile.ExpectedVolumeMm3;
            res["expectedMm3"] = expectedMm3;

            await Say(emit, "Builder", "adding a " + profile.Describe(sh) + " on top of the primary", "run", null);

            double volumeBefore = GetVolumeMm3(part);
            var mu = app.GetMathUtility() as MathUtility;

            // A boss must grow AWAY from the solid. SW's default extrude side on a face is not guaranteed, so try one
            // direction, measure whether it ADDED material, and if it merged into the solid instead (volume unchanged),
            // flip ONCE and try the other side (the proven AddBoss/AutoMate max-1 flip-retry). Keep the outward direction.
            Feature feat = null;
            bool grewOutward = false;
            for (int attempt = 0; attempt < 2 && !grewOutward; attempt++)
            {
                bool flip = attempt == 1;      // second attempt = the opposite side
                string err;
                feat = TryAddFeature(part, mu, top, profile, flip, out err);
                if (feat == null)
                {
                    if (attempt == 1)
                    {
                        res["error"] = err ?? "SolidWorks refused the " + sh + " extrude — the part is unchanged.";
                        return res;
                    }
                    // first attempt could not even create the feature → clean up any loose sketch, then flip-retry
                    CleanupLooseSketch(part);
                    continue;
                }

                try { part.ForceRebuild3(false); } catch { }
                double volumeAfter = GetVolumeMm3(part);
                double added = volumeAfter - volumeBefore;
                res["addedMm3"] = added;

                if (added >= expectedMm3 * 0.5)
                {
                    grewOutward = true;         // outward boss ADDS ~the expected volume
                }
                else
                {
                    // wrong side (merged inward, no material added) → delete it and flip to the other side once
                    RollbackFeature(part);
                    feat = null;
                    if (attempt == 1)
                    {
                        res["addedMm3"] = GetVolumeMm3(part) - volumeBefore;
                        res["error"] = "The " + sh + " added no material either way — neither direction grew the solid, so Forge rolled it back; the part is unchanged.";
                        return res;
                    }
                }
            }

            if (feat == null)
            {
                res["error"] = "SolidWorks refused the " + sh + " extrude — the part is unchanged.";
                return res;
            }
            try { feat.Name = Tag; } catch { }

            // ---- rebuild, then INDEPENDENTLY verify by mass-property volume delta (fail closed) ----
            await Say(emit, "Sentinel", "verifying the " + sh + " post-rebuild", "run", null);
            try { part.ForceRebuild3(false); } catch { }
            bool clean = SafeWhatsWrong(part) == 0;
            double volumeFinal = GetVolumeMm3(part);
            double addedFinal = volumeFinal - volumeBefore;
            res["addedMm3"] = addedFinal;

            double tol = Math.Max(1.0, expectedMm3 * 0.02);   // 2% of the expected added volume
            bool deltaRight = volumeFinal > 0 && volumeBefore > 0 && Math.Abs(addedFinal - expectedMm3) <= tol;
            if (!clean || !deltaRight)
            {
                // FAIL CLOSED: never ship an unverifiable/unclean add-on — delete the feature, restore the part.
                RollbackFeature(part);
                res["created"] = false;
                res["verified"] = false;
                res["error"] = clean
                    ? "The " + sh + " volume delta (" + addedFinal.ToString("N0") + " mm³) doesn't match the expected ≈" +
                      Math.Round(expectedMm3).ToString("N0") + " mm³, so Forge rolled it back; the part is unchanged."
                    : "The " + sh + " rebuilt with errors, so Forge rolled it back; the part is unchanged.";
                await Say(emit, "Sentinel", null, "fail", "rolled back — part restored");
                return res;
            }

            res["created"] = true;
            res["verified"] = true;
            await Say(emit, "Sentinel", null, "done",
                sh + " added on top: volume +" + addedFinal.ToString("N0") + " mm³ (expected ≈" + Math.Round(expectedMm3).ToString("N0") +
                "), rebuild " + (clean ? "clean" : "flagged"));
            res["info"] = "Added a " + profile.Describe(sh) + " on top — the part's volume rose by " + addedFinal.ToString("N0") +
                          " mm³ (expected ≈" + Math.Round(expectedMm3).ToString("N0") + ") and the rebuild is clean. One Ctrl+Z removes it; Forge didn't save.";
            return res;
        }

        // ================= shape support =================

        private static bool IsBuildable(string sh)
        {
            foreach (string b in Buildable) if (string.Equals(b, sh, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ================= profile parsing (mm; defaults fill anything unstated) =================

        private sealed class Profile
        {
            public string Kind;        // "annulus" | "circle" | "rect"
            public double A, B, C;     // annulus: outer / bore / thickness · circle: dia / height / - · rect: l / w / t (mm)
            public double DepthMm;     // the boss-extrude depth in mm (thickness or height)
            public string Summary;     // human summary of the parsed size, used in messages

            public double ExpectedVolumeMm3
            {
                get
                {
                    if (Kind == "annulus")
                    {
                        double ro = A / 2.0, ri = B / 2.0;
                        return Math.PI * (ro * ro - ri * ri) * C;
                    }
                    if (Kind == "circle") return Math.PI * Math.Pow(A / 2.0, 2) * B;
                    return A * B * C;   // rect
                }
            }

            public string Describe(string shape)
            {
                return (shape == "flange" ? "flange" : shape) + " (" + Summary + ")";
            }
        }

        // canonical secondary shape -> profile + dims. Mirrors each create handler's own parsing vocabulary so "add a
        // 20mm boss on top", "add a flange on the top" (defaults), "add a flange 80mm outer 30mm bore 12mm thick on top"
        // and "add a 50x50x50 block on top" all resolve deterministically.
        private static Profile ParseProfile(string sh, string clause)
        {
            string c = (clause ?? "").ToLowerInvariant();
            var triple = Regex.Match(c, @"(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)\s*mm?");
            var pair = Regex.Match(c, @"(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)\s*mm");

            switch (sh)
            {
                // annulus: outer rim + central bore extruded to a thickness. A single bare "N mm" is the OUTER diameter
                // (a flange described by one number); labelled outer/bore/thick win when present.
                case "flange":
                {
                    double outer = 60, bore = 20, thick = 10;
                    var outerM = Regex.Match(c, @"\b(?:outer|outside|od|o\.?d\.?|o/d)\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
                    var boreM = Regex.Match(c, @"\b(?:bore|inner|inside|hole|id|i\.?d\.?|i/d)\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
                    var thickM = Regex.Match(c, @"\b(?:thick|thickness)\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
                    if (outerM.Success) double.TryParse(outerM.Groups[1].Value, out outer);
                    if (boreM.Success) double.TryParse(boreM.Groups[1].Value, out bore);
                    if (thickM.Success) double.TryParse(thickM.Groups[1].Value, out thick);
                    if (!(outerM.Success || boreM.Success || thickM.Success))
                    {
                        if (triple.Success)   // outer × bore × thickness
                        {
                            double.TryParse(triple.Groups[1].Value, out outer);
                            double.TryParse(triple.Groups[2].Value, out bore);
                            double.TryParse(triple.Groups[3].Value, out thick);
                        }
                        else if (pair.Success)
                        {
                            double.TryParse(pair.Groups[1].Value, out outer);
                            double.TryParse(pair.Groups[2].Value, out bore);
                        }
                        else
                        {
                            double n = FirstNumber(c);
                            if (n > 0) outer = n;
                        }
                    }
                    if (outer <= bore) bore = outer * 0.25;   // sane: never a bore wider than the rim
                    if (outer <= 0 || thick <= 0) return null;
                    return new Profile { Kind = "annulus", A = outer, B = bore, C = thick, DepthMm = thick,
                                         Summary = Trim(outer) + "mm outer × " + Trim(bore) + "mm bore × " + Trim(thick) + "mm thick" };
                }

                // solid circle boss (cylinder or a single-size boss)
                case "cylinder":
                case "boss":
                {
                    // an LxWxT spec turns a boss/block into a rectangular pad
                    if (sh == "boss" && triple.Success)
                        return RectProfile(ReadNum(triple, 1, 40), ReadNum(triple, 2, 40), ReadNum(triple, 3, 8));

                    double dia = sh == "cylinder" ? 20 : 12;   // sensible defaults when no size is stated
                    double height = 10;
                    var diaM = Regex.Match(c, @"\b(?:diam(?:eter)?|dia|Ø|⌀)\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
                    var hM = Regex.Match(c, @"\b(?:height|tall(?:ness)?|high|deep|long|length)\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
                    if (diaM.Success) double.TryParse(diaM.Groups[1].Value, out dia);
                    if (hM.Success) double.TryParse(hM.Groups[1].Value, out height);
                    if (!(diaM.Success || hM.Success))
                    {
                        if (pair.Success)   // dia × height
                        {
                            double.TryParse(pair.Groups[1].Value, out dia);
                            double.TryParse(pair.Groups[2].Value, out height);
                        }
                        else
                        {
                            double n = FirstNumber(c);
                            if (n > 0) dia = n;
                        }
                    }
                    if (dia <= 0 || height <= 0) return null;
                    return new Profile { Kind = "circle", A = dia, B = height, C = 0, DepthMm = height,
                                         Summary = Trim(dia) + "mm dia × " + Trim(height) + "mm tall" };
                }

                // rect (plate/block/cube)
                case "plate":
                case "block":
                {
                    double l = 100, w = 60, t = 8;
                    if (sh == "block") { l = 40; w = 40; t = 40; }
                    if (triple.Success) { l = ReadNum(triple, 1, l); w = ReadNum(triple, 2, w); t = ReadNum(triple, 3, t); }
                    else if (pair.Success) { l = ReadNum(pair, 1, l); w = ReadNum(pair, 2, w); }
                    else
                    {
                        double n = FirstNumber(c);
                        if (n > 0)
                        {
                            if (sh == "block") { l = n; w = n; t = n; }   // "a 20mm block" -> a 20 cube
                            else l = n;
                        }
                    }
                    if (l <= 0 || w <= 0 || t <= 0) return null;
                    return RectProfile(l, w, t);
                }

                default:
                    return null;
            }
        }

        private static Profile RectProfile(double l, double w, double t)
        {
            return new Profile { Kind = "rect", A = l, B = w, C = t, DepthMm = t,
                                 Summary = Trim(l) + "×" + Trim(w) + "×" + Trim(t) + "mm" };
        }

        // the first "N mm" number in the clause, else 0
        private static double FirstNumber(string c)
        {
            var m = Regex.Match(c, @"(\d+(?:\.\d+)?)\s*mm");
            double v = 0;
            if (m.Success) double.TryParse(m.Groups[1].Value, out v);
            return v;
        }

        private static double ReadNum(Match m, int group, double fallback)
        {
            double v = fallback;
            if (m.Success && group <= m.Groups.Count - 1 && double.TryParse(m.Groups[group].Value, out v) && v > 0) return v;
            return fallback;
        }

        // honest won't-fit guards (a centered feature bigger than its landing face is refused, never shrunk to guess).
        // A FLANGE is allowed to overhang its host (a pipe flange is wider than the pipe), so it has no fit gate.
        private static string FitProblem(string sh, Profile p, TopFace top)
        {
            double small = top.SmallSpanMm, large = top.LargeSpanMm;
            if (small <= 0) return null;

            if (p.Kind == "circle")
            {
                if (p.A >= small)
                    return "A " + Trim(p.A) + "mm " + sh + " is as wide as the top face's shorter side (" + Trim(small) +
                           "mm) — it wouldn't sit on it. Pick a smaller size, say " + Trim(Math.Max(1.0, small * 0.4)) + "mm.";
                return null;
            }
            if (p.Kind == "rect")
            {
                bool fits = (p.A <= large && p.B <= small) || (p.A <= small && p.B <= large);
                if (!fits)
                    return "A " + Trim(p.A) + "×" + Trim(p.B) + "mm " + sh + " is bigger than the top face (" + Trim(large) + "×" + Trim(small) +
                           "mm) in every orientation — it wouldn't sit on it. Pick a smaller size.";
            }
            return null;
        }

        // ================= top-face resolution (the AddBoss face spine, planar only) =================

        private sealed class TopFace
        {
            public Face2 Face;
            public double[] Centroid;      // a point ON the face (model metres)
            public double[] Normal;
            public double SmallSpanMm;
            public double LargeSpanMm;
        }

        // The TOP planar face of the solid: the planar face whose outward normal points most up (+Y/+Z in the SW frame,
        // per the PROVEN AddBoss want-top logic), else the largest planar face. Returns null when the solid has no planar
        // face at all (a sphere, a smooth-blob primary) — the caller then refuses honestly.
        private static TopFace ResolveTopFace(object[] bodies)
        {
            var tops = new System.Collections.Generic.List<TopFace>();
            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    bool plane = false; try { plane = s != null && s.IsPlane(); } catch { }
                    if (!plane) continue;

                    double area = 0; try { area = face.GetArea(); } catch { }
                    if (area <= 0) continue;
                    double[] n = null; try { n = face.Normal as double[]; } catch { }
                    double[] box = null; try { box = face.GetBox() as double[]; } catch { }
                    double[] centroid = CentroidOnFace(face, box);
                    if (centroid == null) continue;

                    double sSpan = 0, lSpan = 0;
                    if (box != null && box.Length >= 6)
                    {
                        double[] d = { Math.Abs(box[3] - box[0]) * 1000.0, Math.Abs(box[4] - box[1]) * 1000.0, Math.Abs(box[5] - box[2]) * 1000.0 };
                        Array.Sort(d);
                        sSpan = d[1];
                        lSpan = d[2];
                    }

                    tops.Add(new TopFace { Face = face, Centroid = centroid, Normal = n, SmallSpanMm = sSpan, LargeSpanMm = lSpan });
                }
            }
            if (tops.Count == 0) return null;

            // the face that genuinely faces up (+Y or +Z, the proven AddBoss upward pick) — for every from-scratch
            // primitive this is the +Z cap the extrude produced.
            TopFace best = null; double bestUp = -2;
            foreach (var t in tops)
            {
                if (t.Normal == null || t.Normal.Length < 3) continue;
                double up = Math.Max(t.Normal[1], t.Normal[2]);
                if (up > bestUp) { bestUp = up; best = t; }
            }
            if (best != null && bestUp > 0.5) return best;   // a genuine flat top landing

            // no clearly-up planar face (cone/tube-side primaries): fall back to the largest planar face so a curved or
            // sideways primary still gets one honest landing.
            TopFace largest = null; double bestArea = -1;
            foreach (var t in tops)
            {
                double a = 0; try { a = t.Face.GetArea(); } catch { }
                if (a > bestArea) { bestArea = a; largest = t; }
            }
            return largest;
        }

        private static double[] CentroidOnFace(Face2 face, double[] box)
        {
            if (box == null || box.Length < 6) return null;
            double[] c = { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };
            try
            {
                double[] p = face.GetClosestPointOn(c[0], c[1], c[2]) as double[];
                if (p != null && p.Length >= 3) return new[] { p[0], p[1], p[2] };
            }
            catch { }
            return c;
        }

        // ================= the write (one attempt, given a flip) — AddBoss mechanics verbatim =================

        // Select the top face, sketch the profile at the projected face centre, boss-extrude BLIND by the depth with
        // merge=true. 'flip' chooses which side material grows on (the caller picks outward by measuring volume). Returns
        // the created extrude Feature, or null (with 'err') if the sketch/extrude could not be created.
        private static Feature TryAddFeature(IModelDoc2 model, MathUtility mu, TopFace top, Profile p, bool flip, out string err)
        {
            err = null;
            try
            {
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = ((Entity)top.Face).Select4(false, null); } catch { }
                if (!sel) { err = "Couldn't select the top face to sketch on — the part geometry may be in an unexpected state."; return null; }

                var sm = model.SketchManager;
                sm.InsertSketch(true);                                   // begin a sketch on the selected face
                var active = sm.ActiveSketch as Sketch;
                double[] sc = ModelToSketchXY(mu, active, top.Centroid);
                if (sc == null)
                {
                    sm.InsertSketch(true);                               // abandon the sketch cleanly
                    CleanupLooseSketch(model);
                    err = "Couldn't project the face centre into the sketch — Forge left the part untouched.";
                    return null;
                }

                DrawProfile(sm, sc[0], sc[1], p);
                sm.InsertSketch(true);                                   // exit the sketch
                try { model.ClearSelection2(true); } catch { }

                var skFeat = model.FeatureByPositionReverse(0) as Feature;
                if (skFeat != null) skFeat.Select2(false, 0);

                // Positive boss: single-ended BLIND extrude, merge=true (ADD to the existing body). The exact positive-
                // extrude call from AddBoss/RecipeExecutor.DoExtrude — Sd=true (one direction), Flip=<flip> chooses the
                // side, T1=Blind for the depth, Merge=true so the add-on fuses to the part instead of a separate body.
                var feat = model.FeatureManager.FeatureExtrusion3(
                    true, flip, false,
                    (int)swEndConditions_e.swEndCondBlind, 0,
                    p.DepthMm * MM, 0,
                    false, false, false, false, 0, 0,
                    false, false, false, false,
                    true, true, true, 0, 0, false) as Feature;
                try { model.ClearSelection2(true); } catch { }

                if (feat == null) { CleanupLooseSketch(model); err = "SolidWorks refused the " + p.Kind + " extrude — the profile may not have landed on the solid."; return null; }
                try { feat.Name = Tag; } catch { }   // tag immediately so the flip-retry/rollback can reach it
                return feat;
            }
            catch (Exception ex)
            {
                err = "The extrude couldn't be created (" + ex.GetType().Name + ") — Forge rolled back and left the part unchanged.";
                return null;
            }
        }

        private static void DrawProfile(ISketchManager sm, double cx, double cy, Profile p)
        {
            if (p.Kind == "annulus")
            {
                sm.CreateCircleByRadius(cx, cy, 0, p.A / 2.0 * MM);   // outer rim
                sm.CreateCircleByRadius(cx, cy, 0, p.B / 2.0 * MM);   // central bore (nested -> void)
            }
            else if (p.Kind == "circle")
            {
                sm.CreateCircleByRadius(cx, cy, 0, p.A / 2.0 * MM);
            }
            else // rect
            {
                sm.CreateCornerRectangle(cx - p.A / 2.0 * MM, cy - p.B / 2.0 * MM, 0, cx + p.A / 2.0 * MM, cy + p.B / 2.0 * MM, 0);
            }
        }

        // ================= sketch-space projection =================

        private static double[] ModelToSketchXY(MathUtility mu, Sketch sk, double[] p3)
        {
            if (mu == null || sk == null || p3 == null || p3.Length < 3) return null;
            try
            {
                var xform = sk.ModelToSketchTransform as MathTransform;
                if (xform == null) return null;
                var mp = mu.CreatePoint(new[] { p3[0], p3[1], p3[2] }) as MathPoint;
                if (mp == null) return null;
                var sp = mp.MultiplyTransform(xform) as MathPoint;
                double[] a = sp?.ArrayData as double[];
                if (a == null || a.Length < 2) return null;
                return new[] { a[0], a[1] };
            }
            catch { return null; }
        }

        // ================= verification / rollback helpers =================

        private static double GetVolumeMm3(IModelDoc2 model)
        {
            try { var mp = model.Extension.CreateMassProperty(); return mp == null ? -1 : mp.Volume * 1e9; }
            catch { return -1; }
        }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        private static object[] SolidBodies(PartDoc part)
        { try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { return null; } }

        // delete the tagged add-on and rebuild — restores the part so a failed/wrong-way extrude never ships a broken solid
        private static void RollbackFeature(IModelDoc2 model)
        {
            try
            {
                var f = FindFeatureByName(model, Tag);
                if (f == null) return;
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = f.Select2(false, 0); } catch { }
                if (sel) { try { model.EditDelete(); } catch { } }
                try { model.ForceRebuild3(false); } catch { }
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

        // a refused extrude leaves the just-drawn sketch loose — delete it so the part isn't littered
        private static void CleanupLooseSketch(IModelDoc2 model)
        {
            try
            {
                var sf = model.FeatureByPositionReverse(0) as Feature;
                string tn = null; try { tn = sf?.GetTypeName2(); } catch { }
                if (sf != null && tn != null && tn.IndexOf("Sketch", StringComparison.OrdinalIgnoreCase) >= 0)
                { sf.Select2(false, 0); model.EditDelete(); }
                try { model.ClearSelection2(true); } catch { }
            }
            catch { }
        }

        // emit may be null in some callers — never let a missing listener crash the build
        private static async Task Say(Func<string, string, string, string, Task> emit, string agent, string gloss, string state, string result)
        {
            if (emit == null) return;
            try { await emit(agent, gloss, state, result); } catch { }
        }

        private static string Trim(double v) => v.ToString("0.###");
    }
}
