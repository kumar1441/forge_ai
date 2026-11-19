using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for add_note: Notes are enumerated PER VIEW on this interop (no document-wide note
        // list). The handler walks dd.GetViews()'s nested groups + each View's GetNotes() ARRAY; this walks the
        // FLAT view linked list instead (IDrawingDoc.GetFirstView()/View.GetNextView(), the same traversal style
        // drawing-views-real-knee-joint's GT uses for views themselves) and each view's own note LINKED LIST
        // (View.GetFirstNote()/Note.GetNext()) — a completely different code path, so a bug in one enumeration
        // style can't hide behind the other.
        public static JObject MeasureAddNote(ISldWorks app)
        {
            var res = new JObject();
            IModelDoc2 active = null;
            try { active = app.IActiveDoc2 as IModelDoc2; } catch { }
            var dd = active as DrawingDoc;
            var texts = new JArray();
            if (dd != null)
            {
                object viewObj = null;
                try { viewObj = dd.GetFirstView(); } catch { }
                var view = viewObj as View;
                while (view != null)
                {
                    object noteObj = null;
                    try { noteObj = view.GetFirstNote(); } catch { }
                    var note = noteObj as Note;
                    while (note != null)
                    {
                        string txt = null; try { txt = note.GetText(); } catch { }
                        texts.Add(txt);
                        object nextObj = null; try { nextObj = note.GetNext(); } catch { }
                        note = nextObj as Note;
                    }
                    object nextViewObj = null; try { nextViewObj = view.GetNextView(); } catch { }
                    view = nextViewObj as View;
                }
            }
            res["noteCount"] = texts.Count;
            res["notes"] = texts;
            return res;
        }
    }
}
