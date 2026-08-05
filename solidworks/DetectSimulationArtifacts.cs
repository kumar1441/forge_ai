using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class SimArtifactRow
    {
        public string Name;
        public string TypeName;
        public string Kind;   // "weld-bead" | "belt-chain" (v1 scope)
    }

    public class DetectSimulationArtifactsResult
    {
        public bool Success;
        public int TotalFeatures;
        public int ArtifactCount;
        public List<SimArtifactRow> Artifacts = new List<SimArtifactRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// DetectSimulationArtifacts (tool 256, READ, guard) — "sim features, weld beads, belts/chains, motion
    /// elements in trees: classify and exclude from geometry ops." Never modifies anything it finds — this is a
    /// pure classifier so a follow-up geometry op (mirror/pattern/defeature/etc.) can route around these
    /// features instead of trying to treat them as ordinary solid-modifying geometry.
    ///
    /// v1 SCOPE: classifies by `GetTypeName2()` substring match for "WeldBead" / "BeltChain" — inferred from the
    /// `CosmeticWeldBeadFeatureData`/`BeltChainFeatureData` COM class names reflection confirmed exist in the
    /// type library, NOT yet live-confirmed against a real inserted feature (see the test fixture generator:
    /// two live-instrumented `InsertCosmeticWeldBead2` attempts this session both returned null, so no positive
    /// fixture exists yet to read the exact string back from). The negative/clean branch (0 artifacts on a plain
    /// part) is real and live-verified; the classifier logic itself is a documented best-guess shell, ready to
    /// fire once a future session's positive fixture confirms the exact type-name string. SIMULATION/MOTION
    /// STUDY elements do NOT live in the same `IFeature` tree this handler walks at all — Motion/Simulation
    /// studies are a SEPARATE object model (`IMotionStudyManager`/`IMotionStudy`, their own tab, not
    /// `FirstFeature`/`GetNextFeature`) — honestly out of scope rather than guessed at. `HandleUnknownFeatures`
    /// (243) already owns the generic third-party `"MacroFeature"` type-name signal; this tool is the NATIVE-
    /// feature-type equivalent for weld/motion/etc. artifacts specifically, disjoint by TYPE, not by vocabulary.
    /// </summary>
    public static class DetectSimulationArtifacts
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool artifactWord = Regex.IsMatch(c, @"\b(weld\s*bead|belt.?chain|simulation artifact|sim artifact|motion (element|study)|weldment artifact)s?\b");
            bool verbWord = Regex.IsMatch(c, @"\b(detect|find|check|classify|list|exclude|are there|any)\b");
            return artifactWord && verbWord;
        }

        public static async Task<DetectSimulationArtifactsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DetectSimulationArtifactsResult();
            if (model == null) { res.Error = "Open a part or assembly to check for simulation artifacts."; return res; }

            await emit("Sentinel", "scanning the feature tree for weld-bead / belt-chain artifacts", "run", null);

            Feature f = null;
            try { f = model.FirstFeature() as Feature; } catch { }
            int total = 0;
            while (f != null)
            {
                total++;
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                string kind = Classify(tn);
                if (kind != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    res.Artifacts.Add(new SimArtifactRow { Name = nm, TypeName = tn, Kind = kind });
                }
                Feature next = null; try { next = f.GetNextFeature() as Feature; } catch { }
                f = next;
            }
            res.TotalFeatures = total;
            res.ArtifactCount = res.Artifacts.Count;
            res.Success = true;
            res.Info = res.ArtifactCount == 0
                ? "No weld-bead/belt-chain artifacts in the tree — safe to run geometry ops without special-casing anything."
                : res.ArtifactCount + " simulation/weldment artifact" + (res.ArtifactCount == 1 ? "" : "s") + " found (" + string.Join(", ", res.Artifacts.ConvertAll(a => a.Kind)) + ") — exclude these from geometry ops (mirror/pattern/defeature), never modify them directly.";
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }

        internal static string Classify(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            if (typeName.IndexOf("WeldBead", StringComparison.OrdinalIgnoreCase) >= 0) return "weld-bead";
            if (typeName.IndexOf("BeltChain", StringComparison.OrdinalIgnoreCase) >= 0) return "belt-chain";
            return null;
        }
    }
}
