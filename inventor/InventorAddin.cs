using System;
using System.Runtime.InteropServices;
using Inventor;

namespace Forge.Inventor
{
    /// <summary>
    /// Forge Inventor add-in entry point (adapter #2 — docs/kb/multicad.md, brief A-INVENTOR in
    /// docs/platform-architecture.md §12). Inventor loads us via the .addin MANIFEST
    /// (Forge.Inventor.addin), NOT regasm — no COM registration beyond this class's Guid.
    ///
    /// SCAFFOLD (2026-08-17): lifecycle + dockable panel shell. Member names against the Inventor
    /// API (DockableWindows.Add signature, AddChild hosting) are written from the published object
    /// model and MUST be verified against the installed interop on first build (multicad.md §6).
    /// </summary>
    [Guid("5B1E4A7C-2D9F-4C8A-B6E3-1F7D9C2A8E4B")]
    [ComVisible(true)]
    public class InventorAddin : ApplicationAddInServer
    {
        // Shared so the docked panel control can reach the host application.
        // (global:: — our own Forge.Inventor namespace shadows the interop's Inventor namespace.)
        public static global::Inventor.Application InvApp;

        private InventorPanel _panel;
        private Forge.Cad.InventorAdapter _cadAdapter;

        // Inventor calls this when the add-in loads (manifest LoadOnStartUp=1).
        public void Activate(ApplicationAddInSite addInSiteObject, bool firstTime)
        {
            InvApp = addInSiteObject.Application;

            // Multi-CAD: register adapter #2 (scaffold — all capabilities Absent until ported).
            _cadAdapter = new Forge.Cad.InventorAdapter(InvApp);
            Forge.Cad.CadHost.Register(_cadAdapter);

            CreateDockablePanel();
        }

        public void Deactivate()
        {
            Forge.Cad.CadHost.Unregister(_cadAdapter);
            _cadAdapter = null;
            _panel = null;
            if (InvApp != null)
            {
                Marshal.ReleaseComObject(InvApp);
                InvApp = null;
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        // No commands — the panel is the whole surface (same doctrine as the SW add-in).
        public void ExecuteCommand(int commandId) { }
        public object Automation => null;

        private void CreateDockablePanel()
        {
            // Verified against the vendored 2026 (v30) interop by build + reflection:
            // DockableWindows.Add(ClientId, InternalName, Caption) → DockableWindow; host the
            // WinForms control via AddChild(control.Handle); Visible is settable (set_Visible).
            try
            {
                var windows = InvApp.UserInterfaceManager.DockableWindows;
                var window = windows.Add("5B1E4A7C-2D9F-4C8A-B6E3-1F7D9C2A8E4B", "Forge", "Forge");
                _panel = new InventorPanel(InvApp);
                window.AddChild(_panel.Handle);
                window.Visible = true;
            }
            catch (Exception)
            {
                // Panel failure must never take down the add-in load — same doctrine as SW side.
            }
        }
    }
}
