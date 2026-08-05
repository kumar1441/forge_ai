using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// Forge SolidWorks add-in entry point. Implements ISwAddin so SolidWorks
    /// loads it, then mounts the Forge task pane (WebView2 panel).
    /// </summary>
    [Guid("8f3c9e21-4b6a-4c2d-9e1f-2a7b5c8d3e40")]
    [ComVisible(true)]
    public class SwAddin : ISwAddin
    {
        // Shared so the task pane control (created by SolidWorks via ProgID) can reach the app.
        public static ISldWorks SwApp;

        private int _addinCookie;
        private ITaskpaneView _taskpaneView;
        private ForgePanel _panel;

        private const string PanelProgId = "Forge.ForgePanel";

        // SolidWorks loads us via /codebase, but resolves our dependencies (Newtonsoft, WebView2)
        // against SolidWorks's own folder — where they don't exist. Redirect resolution to our folder.
        static SwAddin()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string dir = Path.GetDirectoryName(typeof(SwAddin).Assembly.Location);
                string dll = Path.Combine(dir, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(dll) ? Assembly.LoadFrom(dll) : null;
            };
        }

        public bool ConnectToSW(object thisSw, int cookie)
        {
            SwApp = (ISldWorks)thisSw;
            _addinCookie = cookie;
            SwApp.SetAddinCallbackInfo2(0, this, cookie);

            // Correction capture (undo) is armed per-doc inside CorrectionWatcher.RecordRun after each Forge run.
            // Crash/hang recovery: if the last run never finished, the panel surfaces it on first command.

            // Session memory is per-doc and never touches disk — clear a doc's memory when it closes.
            try { ((SldWorks)SwApp).FileCloseNotify += OnFileClose; } catch { }

            // Trial: stamp first-run / machine id and verify the sandbox is writable before anything runs.
            TrialInit.Init();
            string ver = null; try { ver = SwApp.RevisionNumber(); } catch { }
            Telemetry.Log(TrialInit.Ready ? "session_start" : "crash", success: TrialInit.Ready,
                swVersion: ver, errorCode: TrialInit.Ready ? null : "trial_init_failed");

            CreateTaskPane();
            return true;
        }

        public bool DisconnectFromSW()
        {
            Telemetry.Log("session_end");
            if (_taskpaneView != null)
            {
                _taskpaneView.DeleteView();
                Marshal.ReleaseComObject(_taskpaneView);
                _taskpaneView = null;
            }
            _panel = null;
            SwApp = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        // Doc closed -> drop its session memory (in-memory only; never persisted).
        private int OnFileClose(string fileName, int reason)
        {
            try { SessionMemory.Clear(SessionMemory.KeyFor(fileName)); } catch { }
            return 0;
        }

        private void CreateTaskPane()
        {
            // Empty icon path is allowed; SolidWorks shows a default tab.
            _taskpaneView = SwApp.CreateTaskpaneView2("", "Forge");
            _panel = (ForgePanel)_taskpaneView.AddControl(PanelProgId, "");
        }

        #region COM Registration
        // Written by `regasm /codebase Forge.SolidWorks.dll`. SolidWorks reads these keys to
        // discover and auto-load the add-in.

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            string guid = "{" + t.GUID.ToString().ToUpper() + "}";

            using (RegistryKey hklm = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SolidWorks\Addins\" + guid))
            {
                hklm.SetValue(null, 1, RegistryValueKind.DWord); // 1 = load by default
                hklm.SetValue("Description", "Forge — AI variant generation for SolidWorks");
                hklm.SetValue("Title", "Forge");
            }

            using (RegistryKey hkcu = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\" + guid))
            {
                hkcu.SetValue(null, 1, RegistryValueKind.DWord); // 1 = enabled at startup for this user
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            string guid = "{" + t.GUID.ToString().ToUpper() + "}";
            Registry.LocalMachine.DeleteSubKey(@"SOFTWARE\SolidWorks\Addins\" + guid, false);
            Registry.CurrentUser.DeleteSubKey(@"Software\SolidWorks\AddInsStartup\" + guid, false);
        }
        #endregion
    }
}
