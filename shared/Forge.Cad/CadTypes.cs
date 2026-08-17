using System;

namespace Forge.Cad
{
    /// <summary>Document kind, host-agnostic.</summary>
    public enum CadDocKind { Unknown, Part, Assembly, Drawing }

    /// <summary>Support level for one op group. Semantics = platform-architecture §12.5 (✅/🟡/❌).</summary>
    public enum CadSupport { Supported, Degraded, Absent }

    /// <summary>
    /// One capability declaration. Degraded/Absent MUST carry the reason — it is shown to the user,
    /// never swallowed (fail-closed doctrine). "Absent" on an adapter means the ADAPTER hasn't
    /// implemented it (or the host honestly can't) — reason says which.
    /// </summary>
    public class CadCapability
    {
        public CadSupport Level;
        public string Reason;

        public static readonly CadCapability Yes = new CadCapability { Level = CadSupport.Supported };
        public static CadCapability DegradedBecause(string reason) => new CadCapability { Level = CadSupport.Degraded, Reason = reason };
        public static CadCapability AbsentBecause(string reason) => new CadCapability { Level = CadSupport.Absent, Reason = reason };

        public bool Usable => Level == CadSupport.Supported || Level == CadSupport.Degraded;
    }

    /// <summary>Host-agnostic document identity. Paths may be empty for unsaved/cloud docs.</summary>
    public class CadDocInfo
    {
        public CadDocKind Kind;
        public string Title;
        public string Path;
    }

    /// <summary>Base result — honest, never fabricated. Expected failures set Error, never throw.</summary>
    public class CadResult
    {
        public bool Ok;
        public string Error;

        public static T Fail<T>(string error) where T : CadResult, new() => new T { Ok = false, Error = error };
    }

    public class CadDocumentResult : CadResult
    {
        public CadDocInfo Document;
    }

    /// <summary>
    /// Mass properties in CANONICAL units: mm³, mm², mm, kg. Adapters convert at the boundary
    /// (SW = meters, Inventor = cm). Mass honesty: hosts that can't apply material density honestly
    /// (SW landmine: IMassProperty.Mass assumes water) return MassKg = -1 with MassTrustworthy = false.
    /// </summary>
    public class CadMassPropsResult : CadResult
    {
        public double VolumeMm3 = -1;
        public double SurfaceAreaMm2 = -1;
        public double[] CenterOfMassMm;     // {x,y,z} mm
        public double MassKg = -1;
        public bool MassTrustworthy;        // false => mass unknown, NOT a water-density guess
        public string Note;                 // e.g. "no material assigned" — the honest sentence
    }

    public class CadExportResult : CadResult
    {
        public string Path;
        public long BytesWritten;
        public string Verification;         // fail-closed evidence (e.g. "CARTESIAN_POINT count = 412")
    }

    /// <summary>Canonical sketch planes. Named/host planes come later — keep the enum minimal.</summary>
    public enum CadPlane { XY, YZ, XZ }
}
