using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT read of a drawing's structure, via a DIFFERENT API route than the handler.
        //
        //   handler : IDrawingDoc.GetViews()          -> a nested array, one sub-array per sheet, sheet at index 0
        //   here    : IDrawingDoc.GetFirstView()/GetNextView() -> the flat LINKED LIST of every view in the document
        //
        // The two must agree on the roster. They cannot agree by accident: the linked list has no sheet grouping at
        // all, so sheets are identified here by membership in GetSheetNames() — a third call the handler never makes.
        // Nothing is classified for the harness: the RAW chain is published (name, type code, referenced model) and
        // run-harness re-derives the sheet/view split in PowerShell, so the handler's structural rule is checked by
        // code that shares neither its API nor its language.
        public static JObject MeasureGetDrawingViews(IModelDoc2 model)
        {
            var res = new JObject();
            var dd = model as DrawingDoc;
            res["isDrawing"] = dd != null;
            if (dd == null) return res;

            var sheetNames = new JArray();
            var sheetSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var names = dd.GetSheetNames() as string[];
                if (names != null) foreach (var n in names) { sheetNames.Add(n); sheetSet.Add(n); }
            }
            catch { }
            res["sheetNames"] = sheetNames;

            var chain = new JArray();
            try
            {
                var v = dd.GetFirstView() as IView;
                int guard = 0;
                while (v != null && guard++ < 5000)
                {
                    var e = new JObject();
                    string nm = null; try { nm = v.Name; } catch { }
                    e["name"] = nm;
                    int tc = -1; try { tc = v.Type; } catch { }
                    e["typeCode"] = tc;
                    e["typeName"] = tc >= 0 ? (Enum.GetName(typeof(swDrawingViewTypes_e), tc) ?? ("type " + tc)) : null;
                    e["isSheet"] = nm != null && sheetSet.Contains(nm);
                    string refm = null; try { refm = v.GetReferencedModelName(); } catch { }
                    e["referencedModel"] = refm;
                    int dims = -1; try { var d = v.GetDisplayDimensions() as object[]; dims = d == null ? 0 : d.Length; } catch { }
                    e["dimensionCount"] = dims;
                    chain.Add(e);
                    v = v.GetNextView() as IView;
                }
            }
            catch (Exception ex) { res["chainError"] = ex.Message; }
            res["chain"] = chain;
            return res;
        }
    }
}
