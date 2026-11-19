namespace Forge.Cad
{
    /// <summary>
    /// The canonical op groups (multicad.md §2). Kept deliberately small — an op joins here only when
    /// two hosts can do it honestly. An adapter leaves a group null when it can't; the group is then
    /// Absent in Capabilities. All canonical units: mm, degrees, kg. All results honest (CadResult).
    /// </summary>
    public interface IDocumentOps
    {
        CadDocumentResult ActiveDocument();
        CadDocumentResult Open(string path);          // fail closed on host open-errors; never silent-reuse
        CadDocumentResult CreatePart();               // host default template; never saves (Forge never saves by default)
    }

    public interface ISketchOps
    {
        CadResult BeginSketch(CadPlane plane);
        CadResult Line(double x1, double y1, double x2, double y2);        // sketch-space mm
        CadResult Circle(double cx, double cy, double radiusMm);
        CadResult EndSketch();
    }

    public interface IFeatureOps
    {
        CadResult ExtrudeBoss(double depthMm);        // on the active/last sketch profile
        CadResult ExtrudeCut(double depthMm);         // depthMm <= 0 => through-all
        CadResult Fillet(double radiusMm);            // target resolution is host tier-2 for now; all-edges default
    }

    public interface IGeometryOps
    {
        CadMassPropsResult MassProperties();          // active document
    }

    public interface IAssemblyOps
    {
        CadResult Components();                       // census; per-component detail grows with a second host
    }

    public interface IDrawingOps
    {
        CadResult CreateFromActiveModel();            // host default drawing template + standard views
    }

    public interface IDataOps
    {
        CadResult SetParameter(string name, double valueMm);   // named, first-class where the host allows
        CadResult GetParameter(string name);
    }

    public interface IExportOps
    {
        CadExportResult ExportStep(string path);      // active document → STEP, fail-closed content check
    }
}
