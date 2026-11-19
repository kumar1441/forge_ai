using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddNoteResult
    {
        public bool Verified;
        public bool AlreadyDone;
        public bool NeedsConfirm;
        public string Question;
        public string Text;
        public int NotesBefore;
        public int NotesAfter;
        public string Error;
        public string Info;
    }

    /// <summary>
    /// AddNote — tool 112 (WRITE). "Add a note saying 'Do not scale drawing'" / "put a note that says REV A on the
    /// sheet". Places free text directly on the CURRENT drawing sheet, independent of any view or dimension.
    ///
    /// If no drawing is open yet, bootstraps a BARE one via CreateDrawing.Run (a note needs no views, unlike
    /// InsertStandardViews/SetViewScale's bootstrap) — same reuse-not-duplicate pattern as every other drawing-
    /// family handler this session. Also falls back to app.IActiveDoc2 when the handed-in model isn't a drawing
    /// (the same stale-handle fix DeleteView needed for a chained "create X, then note it" command).
    ///
    /// The note TEXT is the one thing Forge cannot guess — genuinely ambiguous input is a real Rule #2 ask, not a
    /// default. Parsed from a quoted substring first ('...'/"...") or the text after saying/that says/reading/
    /// stating; no match at all means asking what the note should say.
    ///
    /// IDEMPOTENT (Rule #5): if a note with this EXACT text already exists on the sheet, nothing is added again —
    /// reports AlreadyDone. FAIL CLOSED (Rule #6): re-reads the drawing's own annotation list
    /// (IModelDocExtension.GetAnnotations(), filtered to swNote) AFTER the write — a completely different path than
    /// trusting the Note handle ICreateText2 itself returned — and only counts it verified when a note with the
    /// exact text is found there.
    /// </summary>
    public static class AddNote
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bnote\b|\bnotes\b")) return false;
            return Regex.IsMatch(c, @"\b(add|insert|create|make|put)\b");
        }

        public static async Task<AddNoteResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddNoteResult();
            if (model == null) { res.Error = "Open a part, assembly, or drawing first."; return res; }

            string text = ParseNoteText(intent);
            if (text == null)
            {
                res.NeedsConfirm = true;
                res.Question = "What should the note say?";
                return res;
            }
            res.Text = text;

            // Same stale-handle resolution DeleteView needed: the passed model can lag the true active document
            // (e.g. a chained "create a drawing, then add a note").
            IModelDoc2 drawingDoc = model;
            bool isDrawing = false;
            try { isDrawing = (int)drawingDoc.GetType() == (int)swDocumentTypes_e.swDocDRAWING; } catch { }
            if (!isDrawing)
            {
                IModelDoc2 active = null;
                try { active = app.IActiveDoc2 as IModelDoc2; } catch { }
                bool activeIsDrawing = false;
                try { activeIsDrawing = active != null && (int)active.GetType() == (int)swDocumentTypes_e.swDocDRAWING; } catch { }
                if (activeIsDrawing) { drawingDoc = active; isDrawing = true; }
            }

            if (!isDrawing)
            {
                await emit("Drafter", "no drawing open — creating one first", "run", null);
                var cd = await CreateDrawing.Run(app, model, intent, emit);
                if (cd.Error != null) { res.Error = "Couldn't create a drawing to hold the note: " + cd.Error; return res; }
                drawingDoc = app.IActiveDoc2 as IModelDoc2;
                if (drawingDoc == null || (int)drawingDoc.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                { res.Error = "A drawing was reported created, but it isn't the active document — can't add a note."; return res; }
            }

            var existing = EnumerateNoteTexts(drawingDoc);
            res.NotesBefore = existing.Count;
            foreach (var t in existing)
            {
                if (string.Equals(t, text, StringComparison.Ordinal))
                {
                    res.AlreadyDone = true;
                    res.Verified = true;
                    res.NotesAfter = res.NotesBefore;
                    res.Info = "A note with that exact text is already on the sheet — not adding a duplicate.";
                    return res;
                }
            }

            await emit("Scribe", "adding the note", "run", null);
            var dd = drawingDoc as DrawingDoc;
            Note created = null;
            try { created = dd.ICreateText2(text, 0.02, 0.02, 0, 0.005, 0); } catch { }
            if (created == null) { res.Error = "SolidWorks would not create the note."; return res; }

            try { drawingDoc.ForceRebuild3(false); } catch { }

            // ---- FAIL CLOSED: re-read the drawing's own annotation list, don't trust the returned handle alone ----
            await emit("Sentinel", "verifying", "run", null);
            var after = EnumerateNoteTexts(drawingDoc);
            res.NotesAfter = after.Count;
            bool found = false;
            foreach (var t in after) { if (string.Equals(t, text, StringComparison.Ordinal)) { found = true; break; } }
            res.Verified = found && res.NotesAfter == res.NotesBefore + 1;

            if (!res.Verified)
            {
                res.Error = "The note doesn't appear in the sheet's own annotation list after the write (before=" +
                            res.NotesBefore + ", after=" + res.NotesAfter + ").";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Added a note (\"" + text + "\") to the sheet. Forge didn't save.";
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }

        // Notes are enumerated PER VIEW on this interop (IDrawingDoc has no document-wide note list, and
        // IModelDocExtension.GetAnnotations() does not surface sheet-level free text — confirmed empirically: a
        // freshly created note read back as 0 annotations). Walks dd.GetViews()'s nested groups (index 0 of each
        // group is the SHEET's own view, where a sheet-level note lives) and each View's GetNotes() array. Kept as
        // an ARRAY walk, independent of GT's linked-list (GetFirstNote/GetNext) walk, so a bug in one enumeration
        // style can't hide behind the other.
        private static List<string> EnumerateNoteTexts(IModelDoc2 model)
        {
            var texts = new List<string>();
            var dd = model as DrawingDoc;
            if (dd == null) return texts;
            object[] perSheet = null;
            try { perSheet = dd.GetViews() as object[]; } catch { return texts; }
            if (perSheet == null) return texts;
            foreach (var so in perSheet)
            {
                var group = so as object[];
                if (group == null) continue;
                foreach (var vo in group) // index 0 = the sheet's own view; 1+ = real drawing views — notes can sit on either
                {
                    var v = vo as View;
                    if (v == null) continue;
                    object notesRaw = null;
                    try { notesRaw = v.GetNotes(); } catch { continue; }
                    var notesArr = notesRaw as object[];
                    if (notesArr != null)
                    {
                        foreach (var no in notesArr)
                        {
                            var note = no as Note;
                            if (note == null) continue;
                            string txt = null; try { txt = note.GetText(); } catch { }
                            if (txt != null) texts.Add(txt);
                        }
                    }
                    else
                    {
                        var single = notesRaw as Note;
                        if (single != null) { string txt = null; try { txt = single.GetText(); } catch { } if (txt != null) texts.Add(txt); }
                    }
                }
            }
            return texts;
        }

        // quoted text first ('...'/"..."), else the text after saying/that says/reading/stating.
        private static string ParseNoteText(string intent)
        {
            string raw = intent ?? "";
            var q = Regex.Match(raw, "[\"']([^\"']+)[\"']");
            if (q.Success) return q.Groups[1].Value.Trim();
            var m = Regex.Match(raw, @"\b(?:saying|that says|reading|stating)\s+(.+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string t = m.Groups[1].Value.Trim().TrimEnd('.', '!', '?');
                if (t.Length > 0) return t;
            }
            return null;
        }
    }
}
