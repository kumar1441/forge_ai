/**
 * Forge for Onshape — walking skeleton.
 * Smallest honest vertical slice: user pastes their OWN API keys + a document URL (zero-backend,
 * BYOK doctrine), we authenticate, bind the document, list its elements, and read REAL mass
 * properties of the first Part Studio — numbers from the model, never from a transcript.
 * The shared chat panel + postMessage bridge replace this shell once wired (src/panel/bridge.ts).
 */

import { OnshapeAdapter } from "./onshape/adapter";
import { OnshapeClient } from "./onshape/client";

const app = document.querySelector<HTMLElement>("#app")!;
const LS_KEYS = "forge.onshape.keys";

function render(el: string): void {
  app.innerHTML = el;
}

function keyForm(message = ""): void {
  render(`
    <h1>Forge <span>for Onshape</span></h1>
    <p class="dim">Your keys stay in this browser (localStorage), calls go straight to Onshape. No Forge server.</p>
    ${message ? `<p class="err">${message}</p>` : ""}
    <label for="ak">Onshape API access key</label>
    <input id="ak" autocomplete="off" placeholder="from the Onshape dev portal" />
    <label for="sk">Secret key</label>
    <input id="sk" type="password" autocomplete="off" />
    <label for="url">Document URL</label>
    <input id="url" placeholder="https://cad.onshape.com/documents/…/w/…/e/…" />
    <button id="go">Connect</button>
  `);
  document.querySelector<HTMLButtonElement>("#go")!.onclick = () => {
    const accessKey = document.querySelector<HTMLInputElement>("#ak")!.value;
    const secretKey = document.querySelector<HTMLInputElement>("#sk")!.value;
    const url = document.querySelector<HTMLInputElement>("#url")!.value;
    if (!accessKey.trim() || !secretKey.trim()) return keyForm("Both keys are required.");
    localStorage.setItem(LS_KEYS, JSON.stringify({ accessKey, secretKey }));
    void connect(url);
  };
}

async function connect(url: string): Promise<void> {
  const saved = localStorage.getItem(LS_KEYS);
  if (!saved) return keyForm();
  const client = new OnshapeClient(JSON.parse(saved));
  const adapter = new OnshapeAdapter(client);

  render(`<h1>Forge <span>for Onshape</span></h1><p class="dim">Connecting…</p>`);

  const opened = await adapter.documents.open(url);
  if (!opened.ok) return keyForm(opened.error);

  const b = adapter.current()!;
  const elements = await client.listElements(b.did, b.wid).catch(() => null);
  const partStudio = elements?.find((e) => e.elementType === "PARTSTUDIO");
  if (partStudio && !b.eid) adapter.bind(b.did, b.wid, partStudio.id, opened.document!.title);

  const mp = await adapter.geometry.massProperties();

  const caps = Object.entries(adapter.capabilities)
    .map(([k, c]) => `<li>${k}: ${c.level === "supported" ? "✅" : c.level === "degraded" ? "🟡" : "❌"}${c.reason ? ` <span class="dim">— ${c.reason}</span>` : ""}</li>`)
    .join("");

  render(`
    <h1>Forge <span>for Onshape</span></h1>
    <div class="card">
      <strong>${opened.document!.title || "(untitled document)"}</strong>
      <div class="dim">${elements ? `${elements.length} elements` : "element list unavailable"}${partStudio ? ` · part studio: ${partStudio.name}` : ""}</div>
    </div>
    <div class="card">
      ${
        mp.ok
          ? `<span class="ok">Measured (REST, from the model):</span>
             <ul>
               <li>volume ${(mp.volumeMm3! / 1000).toFixed(2)} cm³</li>
               <li>mass ${mp.massTrustworthy ? `${mp.massKg!.toFixed(3)} kg` : "unknown (no material density)"}</li>
               <li>centre of mass (${mp.centerOfMassMm!.map((v) => v.toFixed(1)).join(", ")}) mm</li>
             </ul>`
          : `<span class="err">${mp.error}</span>`
      }
    </div>
    <div class="card"><strong>Capabilities</strong><ul>${caps}</ul></div>
    <div class="card dim">Scaffold build — chat panel + generative FeatureScript ops land next (see the multi-CAD spec).</div>
  `);
}

// Boot: with saved keys we still ask for the document URL each session (documents aren't secrets).
const boot = localStorage.getItem(LS_KEYS);
if (boot) {
  render(`
    <h1>Forge <span>for Onshape</span></h1>
    <label for="url">Document URL</label>
    <input id="url" placeholder="https://cad.onshape.com/documents/…/w/…/e/…" />
    <button id="go">Connect</button>
    <p class="dim"><a href="#" id="reset" style="color:#9aa0a6">forget saved keys</a></p>
  `);
  document.querySelector<HTMLButtonElement>("#go")!.onclick = () =>
    void connect(document.querySelector<HTMLInputElement>("#url")!.value);
  document.querySelector<HTMLAnchorElement>("#reset")!.onclick = (e) => {
    e.preventDefault();
    localStorage.removeItem(LS_KEYS);
    keyForm();
  };
} else {
  keyForm();
}
