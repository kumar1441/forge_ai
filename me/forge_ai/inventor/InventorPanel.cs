using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
// NOTE: no "using Inventor;" — Inventor.Path/Inventor.File collide with System.IO. The one interop
// type used here (Application) is referenced as global::Inventor.Application.

namespace Forge.Inventor
{
    /// <summary>
    /// Forge panel hosted in an Inventor DockableWindow. Hosts THE shared bundle (panel/panel.html —
    /// never forked, ui-design-system rule) in WebView2. SCAFFOLD: loads the bundle; the intent
    /// bridge (panel → Tier-2/canonical tools) is the porting work that follows first build-green.
    /// The SW add-in's ForgePanel is the reference implementation for the bridge shape.
    /// </summary>
    public class InventorPanel : UserControl
    {
        private readonly global::Inventor.Application _app;
        private WebView2 _web;

        public InventorPanel(global::Inventor.Application app)
        {
            _app = app;
            Dock = DockStyle.Fill;
            Load += OnLoad;
        }

        private async void OnLoad(object sender, EventArgs e)
        {
            try
            {
                _web = new WebView2 { Dock = DockStyle.Fill };
                Controls.Add(_web);
                await _web.EnsureCoreWebView2Async();

                // The shared bundle sits next to the DLL (csproj links ..\panel\panel.html in).
                string html = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location), "panel.html");
                if (File.Exists(html))
                    _web.CoreWebView2.Navigate(new Uri(html).AbsoluteUri);

                // TODO(port): wire the bridge — window.chrome.webview messages → intent pipeline.
                // SW reference: solidworks/ForgePanel.cs. Bridge shape per ui-design-system:
                // only the postMessage shim differs per host; the bundle is not forked.
            }
            catch (Exception)
            {
                // A panel that can't init must say so in the add-in, not crash Inventor.
            }
        }
    }
}
