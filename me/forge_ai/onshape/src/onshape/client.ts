/**
 * Minimal Onshape REST client (https://cad.onshape.com/glassworks/explorer is the live API reference).
 *
 * Auth: API-key Basic (base64 accessKey:secretKey) — the zero-backend path: the user pastes THEIR
 * OWN keys (same BYOK doctrine as the LLM providers; no Forge server in the loop). HMAC-signed
 * requests are the hardened variant; OAuth integrated-app is the product path (App Store approval
 * has real lead time — start it early, per the Onshape adapter brief).
 *
 * Keys live in localStorage on the user's machine only. Never logged, never sent anywhere except
 * cad.onshape.com over HTTPS.
 */

export interface OnshapeKeys {
  accessKey: string;
  secretKey: string;
}

export interface OnshapeElement {
  id: string;
  name: string;
  elementType: string; // "PARTSTUDIO" | "ASSEMBLY" | "DRAWING" | ...
}

const API = "https://cad.onshape.com/api";

export class OnshapeClient {
  private readonly auth: string;

  constructor(keys: OnshapeKeys) {
    this.auth = btoa(`${keys.accessKey.trim()}:${keys.secretKey.trim()}`);
  }

  /** Throws OnshapeError with the real status on failure — callers convert to honest CadResults. */
  private async get<T>(path: string): Promise<T> {
    const res = await fetch(`${API}${path}`, {
      headers: { Authorization: `Basic ${this.auth}`, Accept: "application/json" },
    });
    if (!res.ok) {
      const body = await res.text().catch(() => "");
      throw new OnshapeError(res.status, body.slice(0, 300));
    }
    return (await res.json()) as T;
  }

  async getDocumentName(did: string): Promise<string> {
    const d = await this.get<{ name?: string }>(`/documents/${encodeURIComponent(did)}`);
    return d.name ?? "";
  }

  async listElements(did: string, wid: string): Promise<OnshapeElement[]> {
    return this.get<OnshapeElement[]>(
      `/documents/d/${encodeURIComponent(did)}/w/${encodeURIComponent(wid)}/elements`,
    );
  }

  /**
   * Mass properties of a Part Studio. Onshape returns SI (meters/m³/kg); the ADAPTER converts.
   * Response shape: { bodies: { [id]: { mass:[..], volume:[..], centroid:[..], ... } } } — fields
   * are arrays (per mass-type); we read index 0 ("solid/accurate") and sum bodies ourselves so the
   * GroundTruth re-read can do the same independently.
   */
  async getMassProperties(did: string, wid: string, eid: string): Promise<MassPropsResponse> {
    return this.get<MassPropsResponse>(
      `/partstudios/d/${encodeURIComponent(did)}/w/${encodeURIComponent(wid)}/e/${encodeURIComponent(eid)}/massproperties`,
    );
  }
}

export class OnshapeError extends Error {
  constructor(
    public readonly status: number,
    body: string,
  ) {
    super(`Onshape API ${status}: ${body}`);
    this.name = "OnshapeError";
  }
}

export interface MassPropsResponse {
  bodies?: Record<
    string,
    {
      mass?: number[];
      volume?: number[];
      centroid?: number[];
      /** present when a material/density is assigned — mass is only trustworthy then. */
      density?: number[];
    }
  >;
}

/** Parse an Onshape document URL into its binding. Returns null on anything that isn't one. */
export function parseDocumentUrl(url: string): { did: string; wid: string; eid?: string } | null {
  // https://cad.onshape.com/documents/<did>/w/<wid>/e/<eid> (v/ instead of w/ for versions)
  const m = /cad\.onshape\.com\/documents\/([0-9a-f]{24})\/([wv])\/([0-9a-f]{24})(?:\/e\/([0-9a-f]{24}))?/i.exec(
    url.trim(),
  );
  if (!m) return null;
  return { did: m[1], wid: m[3], eid: m[4] };
}
