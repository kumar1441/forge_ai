/**
 * Panel bridge shim (scaffold). The shared bundle (panel/panel.html — never forked) talks to its
 * host through a per-tool shim: window.chrome.webview on WebView2 hosts (SW/Inventor), and
 * window.postMessage here — this app runs inside an Onshape document-tab iframe.
 *
 * Rules from the Onshape adapter brief: origin-lock BOTH directions (Onshape's origin only),
 * token-in-message auth (iframe third-party cookie rules), idempotent event handling.
 *
 * TODO(port): mount the shared bundle in an inner iframe, translate its postMessage protocol to
 * the OnshapeAdapter ops, and push context (bound did/wid/eid) on load.
 */

export const ONSHAPE_ORIGIN = "https://cad.onshape.com";

export function startBridge(onMessage: (msg: unknown) => void): void {
  window.addEventListener("message", (e) => {
    if (e.origin !== ONSHAPE_ORIGIN) return; // origin-locked, inbound
    onMessage(e.data);
  });
}

export function postToHost(target: Window, msg: unknown): void {
  target.postMessage(msg, ONSHAPE_ORIGIN); // origin-locked, outbound
}
