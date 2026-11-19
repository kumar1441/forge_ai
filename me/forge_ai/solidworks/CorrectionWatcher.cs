using System;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// Correction capture — the highest-signal data: the delta between what Forge DID and what the human WANTED.
    /// Masked, API-operation level, never geometry. Two signals:
    ///  - "rephrase": a command that asked/errored, then a DIFFERENT command that worked within ~2 min -> a training pair.
    ///  - "undone": the user Undid a Forge action within ~2 min (they rejected the result) — via the doc's UndoPostNotify.
    /// </summary>
    public static class CorrectionWatcher
    {
        private class Run { public string Handler, Prompt; public DateTime Time; }
        private static Run _lastRun;     // last successfully-executed Forge run
        private static Run _lastFailed;  // last asked/error/refused command (for rephrase pairing)
        private static object _attached; // the doc whose UndoPostNotify we're hooked to
        private const int WindowSec = 120;

        // Called after every command completes. Handles rephrase pairing and arms the undo window (hooks the doc).
        public static void RecordRun(string runId, string handler, string maskedPrompt, string outcome, IModelDoc2 model)
        {
            var now = DateTime.UtcNow;
            if (outcome == "executed" && _lastFailed != null && (now - _lastFailed.Time).TotalSeconds <= WindowSec
                && !string.Equals(_lastFailed.Prompt, maskedPrompt, StringComparison.OrdinalIgnoreCase))
                ForgeData.LogCorrection(handler, maskedPrompt, "rephrase", null, _lastFailed.Prompt);

            _lastFailed = (outcome == "asked" || outcome == "error" || outcome == "refused")
                ? new Run { Handler = handler, Prompt = maskedPrompt, Time = now } : null;

            if (outcome == "executed") { _lastRun = new Run { Handler = handler, Prompt = maskedPrompt, Time = now }; HookUndo(model); }
        }

        private static void HookUndo(IModelDoc2 model)
        {
            try
            {
                if (model == null || ReferenceEquals(model, _attached)) return;
                var asm = model as AssemblyDoc;
                if (asm != null) { asm.UndoPostNotify += OnUndo; _attached = model; }
                var prt = model as PartDoc;
                if (prt != null) { prt.UndoPostNotify += OnUndoPart; _attached = model; }
            }
            catch { }
        }

        private static int OnUndo()
        {
            if (_lastRun != null && (DateTime.UtcNow - _lastRun.Time).TotalSeconds <= WindowSec)
            {
                // The user reverted a Forge action -> the parse did not match their intent. Best teacher there is.
                ForgeData.LogCorrection(_lastRun.Handler, _lastRun.Prompt, "undone", null);
                _lastRun = null;
            }
            return 0;
        }
        private static int OnUndoPart() => OnUndo();
    }
}
