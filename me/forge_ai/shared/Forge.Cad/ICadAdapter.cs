namespace Forge.Cad
{
    /// <summary>
    /// One CAD host. Op groups the adapter doesn't implement are null + Absent in Capabilities
    /// (multicad.md §2). Canonical tools (Tier 1) are written against this interface only; host API
    /// types never cross this boundary.
    /// </summary>
    public interface ICadAdapter
    {
        string HostId { get; }              // "solidworks" | "inventor" | "onshape" | "freecad" | "fusion"
        CadCapabilities Capabilities { get; }

        IDocumentOps Documents { get; }
        ISketchOps Sketch { get; }
        IFeatureOps Features { get; }
        IGeometryOps Geometry { get; }
        IAssemblyOps Assembly { get; }
        IDrawingOps Drawing { get; }
        IDataOps Data { get; }
        IExportOps Export { get; }
    }
}
