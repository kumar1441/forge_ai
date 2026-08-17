using Forge.Cad;

namespace Forge.Cad
{
    /// <summary>
    /// Adapter #2 — Inventor behind the canonical Forge.Cad interface (multicad.md §2).
    /// SCAFFOLD: every op group is Absent until ported against the INSTALLED interop (§6 kb-seed:
    /// units are cm — convert ×10 to canonical mm at this boundary; constraints ≠ SW mates;
    /// parameters are first-class/named — prefer them over sketch-dim spelunking; Vault check before
    /// file ops; transient B-rep for math; never cache raw COM objects across calls).
    ///
    /// Port order (§5 gates): Documents → Geometry (mass props) → Export (STEP) → Sketch → Features
    /// → Assembly → Data → Drawing, one group at a time, gate after each. SolidWorksAdapter is the
    /// reference implementation; its Tier-2 reuse pattern (call the proven tool, don't reimplement)
    /// maps to Inventor's named-parameters strength.
    /// </summary>
    public class InventorAdapter : ICadAdapter
    {
        private readonly Inventor.Application _app;

        public InventorAdapter(Inventor.Application app) { _app = app; }

        public string HostId => "inventor";

        private const string Pending = "scaffold — port pending against installed Inventor interop";

        private static readonly CadCapabilities Caps = new CadCapabilities
        {
            Documents = CadCapability.AbsentBecause(Pending),
            Sketch = CadCapability.AbsentBecause(Pending),
            Features = CadCapability.AbsentBecause(Pending),
            Geometry = CadCapability.AbsentBecause(Pending),
            Assembly = CadCapability.AbsentBecause(Pending),
            Drawing = CadCapability.AbsentBecause(Pending),
            Data = CadCapability.AbsentBecause(Pending),
            Export = CadCapability.AbsentBecause(Pending),
        };
        public CadCapabilities Capabilities => Caps;

        public IDocumentOps Documents => null;
        public ISketchOps Sketch => null;
        public IFeatureOps Features => null;
        public IGeometryOps Geometry => null;
        public IAssemblyOps Assembly => null;
        public IDrawingOps Drawing => null;
        public IDataOps Data => null;
        public IExportOps Export => null;
    }
}
