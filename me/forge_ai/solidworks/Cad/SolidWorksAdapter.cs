using System;
using System.IO;
using System.Threading.Tasks;
using Forge.Cad;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks.Cad
{
    /// <summary>
    /// Adapter #1 — SolidWorks behind the canonical Forge.Cad interface (multicad.md §2).
    /// Proof-set scope: Documents / Geometry / Export are Supported and reuse the add-in's proven
    /// live paths (CreatePart's template logic, OpenDoc6 with real error codes, GetMassProps' honest
    /// mass handling, fail-closed STEP export). Sketch/Features/Assembly/Drawing/Data stay Absent
    /// here until ported — the 299 Tier-2 tools cover those on this host meanwhile.
    /// SW COM units are METERS — converted to canonical mm at this boundary, never beyond it.
    /// </summary>
    public class SolidWorksAdapter : ICadAdapter
    {
        private readonly ISldWorks _app;

        public SolidWorksAdapter(ISldWorks app) { _app = app; }

        public string HostId => "solidworks";

        private static readonly CadCapabilities Caps = new CadCapabilities
        {
            Documents = CadCapability.Yes,
            Geometry = CadCapability.Yes,
            Export = CadCapability.Yes,
            Sketch = CadCapability.AbsentBecause("adapter port pending — Tier-2 SW tools cover sketches"),
            Features = CadCapability.AbsentBecause("adapter port pending — Tier-2 SW tools cover features"),
            Assembly = CadCapability.AbsentBecause("adapter port pending — Tier-2 SW tools cover assemblies"),
            Drawing = CadCapability.AbsentBecause("adapter port pending — Tier-2 SW tools cover drawings"),
            Data = CadCapability.AbsentBecause("adapter port pending — Tier-2 SW tools cover properties"),
        };
        public CadCapabilities Capabilities => Caps;

        public IDocumentOps Documents => new SwDocumentOps(_app);
        public ISketchOps Sketch => null;
        public IFeatureOps Features => null;
        public IGeometryOps Geometry => new SwGeometryOps(_app);
        public IAssemblyOps Assembly => null;
        public IDrawingOps Drawing => null;
        public IDataOps Data => null;
        public IExportOps Export => new SwExportOps(_app);

        private static CadDocKind KindOf(IModelDoc2 doc)
        {
            try
            {
                switch (doc.GetType())
                {
                    case (int)swDocumentTypes_e.swDocPART: return CadDocKind.Part;
                    case (int)swDocumentTypes_e.swDocASSEMBLY: return CadDocKind.Assembly;
                    case (int)swDocumentTypes_e.swDocDRAWING: return CadDocKind.Drawing;
                }
            }
            catch { }
            return CadDocKind.Unknown;
        }

        private static CadDocInfo InfoOf(IModelDoc2 doc)
        {
            var info = new CadDocInfo { Kind = KindOf(doc) };
            try { info.Title = doc.GetTitle(); } catch { }
            try { info.Path = doc.GetPathName(); } catch { }
            return info;
        }

        private class SwDocumentOps : IDocumentOps
        {
            private readonly ISldWorks _app;
            public SwDocumentOps(ISldWorks app) { _app = app; }

            public CadDocumentResult ActiveDocument()
            {
                IModelDoc2 doc = null;
                try { doc = _app.IActiveDoc2; } catch { }
                if (doc == null) return CadResult.Fail<CadDocumentResult>("No active document — open a part or assembly first.");
                return new CadDocumentResult { Ok = true, Document = InfoOf(doc) };
            }

            public CadDocumentResult Open(string path)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return CadResult.Fail<CadDocumentResult>("File not found: " + path);

                int type;
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".sldprt": type = (int)swDocumentTypes_e.swDocPART; break;
                    case ".sldasm": type = (int)swDocumentTypes_e.swDocASSEMBLY; break;
                    case ".slddrw": type = (int)swDocumentTypes_e.swDocDRAWING; break;
                    default: return CadResult.Fail<CadDocumentResult>("Not a native SolidWorks file: " + Path.GetExtension(path));
                }

                // Proven path throughout the codebase. errs are the ONLY trustworthy open-time signal
                // on this build (OpenState landmine) — fail closed on any non-zero error code.
                int errs = 0, warns = 0;
                IModelDoc2 doc = null;
                try
                {
                    doc = _app.OpenDoc6(path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns) as IModelDoc2;
                }
                catch (Exception ex) { return CadResult.Fail<CadDocumentResult>("Open failed (" + ex.GetType().Name + "): " + ex.Message); }
                if (doc == null || errs != 0)
                {
                    string named = errs != 0 ? ((swFileLoadError_e)errs).ToString() : "unknown error";
                    return CadResult.Fail<CadDocumentResult>("Couldn't open " + Path.GetFileName(path) + " — " + named + ".");
                }
                return new CadDocumentResult { Ok = true, Document = InfoOf(doc) };
            }

            public CadDocumentResult CreatePart()
            {
                // CreatePart.cs's proven template resolution: user default part template, else the
                // stock part.prtdot next to the SW install. Never saves.
                string template = null;
                try { template = _app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart); } catch { }
                if (string.IsNullOrEmpty(template) || !File.Exists(template))
                {
                    string exeDir = null; try { exeDir = Path.GetDirectoryName(_app.GetExecutablePath()); } catch { }
                    string fallback = !string.IsNullOrEmpty(exeDir) ? Path.Combine(exeDir, @"..\data\templates\part.prtdot") : null;
                    if (fallback != null && File.Exists(fallback)) template = fallback;
                }
                if (string.IsNullOrEmpty(template) || !File.Exists(template))
                    return CadResult.Fail<CadDocumentResult>("Couldn't find a part template on this install.");

                IModelDoc2 doc = null;
                try { doc = _app.NewDocument(template, 0, 0, 0) as IModelDoc2; }
                catch (Exception ex) { return CadResult.Fail<CadDocumentResult>("NewDocument threw (" + ex.GetType().Name + "): " + ex.Message); }
                if (doc == null) return CadResult.Fail<CadDocumentResult>("NewDocument returned nothing — the template may be invalid.");
                if (KindOf(doc) != CadDocKind.Part) return CadResult.Fail<CadDocumentResult>("The new document isn't a part.");
                return new CadDocumentResult { Ok = true, Document = InfoOf(doc) };
            }
        }

        private class SwGeometryOps : IGeometryOps
        {
            private readonly ISldWorks _app;
            public SwGeometryOps(ISldWorks app) { _app = app; }

            public CadMassPropsResult MassProperties()
            {
                IModelDoc2 doc = null;
                try { doc = _app.IActiveDoc2; } catch { }
                if (doc == null) return CadResult.Fail<CadMassPropsResult>("No active document — open a part or assembly first.");

                // REUSE the proven honest path (tool #22): volume/area/COM are reliable; mass is only
                // reported when a database-resolved material exists — never a water-density guess
                // (landmines.md: IMassProperty.Mass ignores material density on this build).
                MassPropsResult r;
                try { r = GetMassProps.Run(_app, doc, "", (a, b, c, d) => Task.CompletedTask).GetAwaiter().GetResult(); }
                catch (Exception ex) { return CadResult.Fail<CadMassPropsResult>("Mass properties failed (" + ex.GetType().Name + ")."); }
                if (!string.IsNullOrEmpty(r.Error)) return CadResult.Fail<CadMassPropsResult>(r.Error);

                return new CadMassPropsResult
                {
                    Ok = true,
                    VolumeMm3 = r.VolumeMm3,
                    SurfaceAreaMm2 = r.SurfaceAreaMm2,
                    CenterOfMassMm = r.CenterOfMassMm,
                    MassKg = r.MaterialAssigned ? r.MassKg : -1,
                    MassTrustworthy = r.MaterialAssigned && r.MassKg > 0,
                    Note = r.MaterialAssigned ? null : "no material assigned — mass withheld, not guessed",
                };
            }
        }

        private class SwExportOps : IExportOps
        {
            private readonly ISldWorks _app;
            public SwExportOps(ISldWorks app) { _app = app; }

            public CadExportResult ExportStep(string path)
            {
                IModelDoc2 doc = null;
                try { doc = _app.IActiveDoc2; } catch { }
                if (doc == null) return CadResult.Fail<CadExportResult>("No active document — open a part or assembly first.");
                if (string.IsNullOrWhiteSpace(path)) return CadResult.Fail<CadExportResult>("No output path given.");
                if (!string.Equals(Path.GetExtension(path), ".step", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetExtension(path), ".stp", StringComparison.OrdinalIgnoreCase))
                    path = Path.ChangeExtension(path, ".STEP");

                string dir = null;
                try { dir = Path.GetDirectoryName(Path.GetFullPath(path)); } catch { }
                if (dir == null || !Directory.Exists(dir))
                    return CadResult.Fail<CadExportResult>("Output folder doesn't exist: " + (dir ?? path));

                // Same SaveAs engine BatchConvertFiles proved live; same fail-closed doctrine — a
                // true return is not trusted, the file's real geometry records are counted instead.
                int errs = 0, warns = 0;
                bool saved = false;
                try { saved = doc.Extension.SaveAs(path, 0, 0, null, ref errs, ref warns); }
                catch (Exception ex) { return CadResult.Fail<CadExportResult>("STEP export threw (" + ex.GetType().Name + "): " + ex.Message); }
                if (!saved || !File.Exists(path))
                    return CadResult.Fail<CadExportResult>("STEP export failed" + (errs != 0 ? " — " + ((swFileSaveError_e)errs).ToString() : " — SaveAs returned false") + ".");

                var fi = new FileInfo(path);
                int points = 0;
                try
                {
                    string text = File.ReadAllText(path);
                    int idx = 0;
                    while ((idx = text.IndexOf("CARTESIAN_POINT", idx, StringComparison.Ordinal)) >= 0) { points++; idx += 15; }
                }
                catch (Exception ex) { return CadResult.Fail<CadExportResult>("STEP written but unreadable for verification: " + ex.Message); }
                if (points < 10)
                    return CadResult.Fail<CadExportResult>("STEP export looks empty — only " + points + " geometry records in " + fi.Length + " bytes.");

                return new CadExportResult
                {
                    Ok = true,
                    Path = path,
                    BytesWritten = fi.Length,
                    Verification = "CARTESIAN_POINT count = " + points,
                };
            }
        }
    }
}
