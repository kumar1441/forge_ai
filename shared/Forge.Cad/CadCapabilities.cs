namespace Forge.Cad
{
    /// <summary>
    /// What an adapter supports, per op group (docs/kb/multicad.md §3). The router/pipeline consults
    /// this BEFORE planning; Absent => fail closed with the honest sentence. This in-code declaration
    /// is the seed for the cloud `tool_capabilities` table (platform-architecture Gap 2).
    /// </summary>
    public class CadCapabilities
    {
        public CadCapability Documents = CadCapability.AbsentBecause("not declared");
        public CadCapability Sketch = CadCapability.AbsentBecause("not declared");
        public CadCapability Features = CadCapability.AbsentBecause("not declared");
        public CadCapability Geometry = CadCapability.AbsentBecause("not declared");
        public CadCapability Assembly = CadCapability.AbsentBecause("not declared");
        public CadCapability Drawing = CadCapability.AbsentBecause("not declared");
        public CadCapability Data = CadCapability.AbsentBecause("not declared");
        public CadCapability Export = CadCapability.AbsentBecause("not declared");

        /// <summary>The honest one-liner for a user-facing fail-closed message.</summary>
        public static string Sentence(string hostId, string opGroup, CadCapability cap)
        {
            if (cap == null) return opGroup + " isn't available on " + hostId + " yet.";
            if (cap.Level == CadSupport.Absent)
                return opGroup + " isn't available on " + hostId + (string.IsNullOrEmpty(cap.Reason) ? "." : " — " + cap.Reason);
            if (cap.Level == CadSupport.Degraded)
                return opGroup + " on " + hostId + " is limited" + (string.IsNullOrEmpty(cap.Reason) ? "." : " — " + cap.Reason);
            return null;
        }
    }
}
