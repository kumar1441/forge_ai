/**
 * Adapter #3 — Onshape behind the canonical Forge.Cad interface (TypeScript).
 *
 * Scaffold scope: Documents (bind by URL) + Geometry (mass properties) are SUPPORTED;
 * everything else is Absent with honest reasons. Generative ops (sketch/feature) arrive via
 * FeatureScript — "features-as-code", the best generative fit after SolidWorks per the program
 * spec. GroundTruth doctrine: every future WRITE op ships with an independent REST re-read
 * (re-fetch mass/feature state and compare), never trust the mutation response alone.
 */

import type { ICadAdapter, IDocumentOps, IGeometryOps } from "../cad/adapter";
import {
  cap,
  type CadCapabilities,
  type CadDocumentResult,
  type CadMassPropsResult,
} from "../cad/types";
import { OnshapeClient, OnshapeError, parseDocumentUrl } from "./client";

const PENDING_FS = "port pending — generative ops arrive via FeatureScript (features-as-code)";

const CAPS: CadCapabilities = {
  documents: cap.yes(),
  geometry: cap.yes(),
  sketch: cap.absent(PENDING_FS),
  features: cap.absent(PENDING_FS),
  assembly: cap.absent("port pending — assemblies are separate REST elements; mates read via assembly endpoints"),
  drawing: cap.degraded("drawings via REST are limited vs desktop hosts"),
  data: cap.absent("port pending — configuration/variable endpoints"),
  export: cap.absent("port pending — translation API (STEP/Parasolid/glTF native)"),
};

export class OnshapeAdapter implements ICadAdapter {
  readonly hostId = "onshape";
  readonly capabilities = CAPS;

  readonly documents: IDocumentOps;
  readonly geometry: IGeometryOps;

  private bound: { did: string; wid: string; eid?: string; title?: string } | null = null;

  constructor(client: OnshapeClient) {
    this.documents = new OnshapeDocumentOps(client, this);
    this.geometry = new OnshapeGeometryOps(client, this);
  }

  bind(did: string, wid: string, eid?: string, title?: string): void {
    this.bound = { did, wid, eid, title };
  }

  current(): { did: string; wid: string; eid?: string; title?: string } | null {
    return this.bound;
  }
}

class OnshapeDocumentOps implements IDocumentOps {
  constructor(
    private readonly client: OnshapeClient,
    private readonly adapter: OnshapeAdapter,
  ) {}

  async activeDocument(): Promise<CadDocumentResult> {
    const b = this.adapter.current();
    if (!b) return { ok: false, error: "No document bound — paste an Onshape document URL first." };
    return {
      ok: true,
      document: {
        kind: b.eid ? "part" : "unknown",
        title: b.title ?? "",
        path: "",
        did: b.did,
        wid: b.wid,
        eid: b.eid,
      },
    };
  }

  async open(url: string): Promise<CadDocumentResult> {
    const parsed = parseDocumentUrl(url);
    if (!parsed) return { ok: false, error: "That isn't an Onshape document URL." };
    try {
      const title = await this.client.getDocumentName(parsed.did);
      this.adapter.bind(parsed.did, parsed.wid, parsed.eid, title);
      return {
        ok: true,
        document: { kind: "unknown", title, path: "", did: parsed.did, wid: parsed.wid, eid: parsed.eid },
      };
    } catch (e) {
      return fail(e, "Couldn't open that document — check the URL and that your API key has access.");
    }
  }

  async createPart(): Promise<CadDocumentResult> {
    return { ok: false, error: "createPart not ported yet — document creation is a POST /documents call." };
  }
}

class OnshapeGeometryOps implements IGeometryOps {
  constructor(
    private readonly client: OnshapeClient,
    private readonly adapter: OnshapeAdapter,
  ) {}

  async massProperties(): Promise<CadMassPropsResult> {
    const b = this.adapter.current();
    if (!b?.eid) return { ok: false, error: "Bind to a Part Studio tab first (URL with /e/...)." };
    try {
      const mp = await this.client.getMassProperties(b.did, b.wid, b.eid);
      const bodies = Object.values(mp.bodies ?? {});
      if (bodies.length === 0) return { ok: false, error: "No solid bodies in this Part Studio." };

      // Sum bodies ourselves so a GroundTruth re-read does the same independently.
      // Onshape = SI (m³/m/kg) → canonical mm. mass is only trustworthy with a real density.
      let volM3 = 0;
      let massKg = 0;
      let anyDensityMissing = false;
      let cx = 0,
        cy = 0,
        cz = 0;
      for (const body of bodies) {
        const v = body.volume?.[0] ?? 0;
        const m = body.mass?.[0] ?? 0;
        volM3 += v;
        massKg += m;
        if (!body.density || body.density[0] === undefined) anyDensityMissing = true;
        const c = body.centroid ?? [0, 0, 0];
        cx += (c[0] ?? 0) * v;
        cy += (c[1] ?? 0) * v;
        cz += (c[2] ?? 0) * v;
      }
      if (volM3 <= 0) return { ok: false, error: "Zero volume — no solid body to measure." };
      const com: [number, number, number] = [
        (cx / volM3) * 1000,
        (cy / volM3) * 1000,
        (cz / volM3) * 1000,
      ];
      return {
        ok: true,
        volumeMm3: volM3 * 1e9,
        centerOfMassMm: com,
        massKg,
        massTrustworthy: !anyDensityMissing,
        note: anyDensityMissing ? "some bodies have no material density — mass as-reported, not guaranteed" : undefined,
      };
    } catch (e) {
      return fail(e, "Mass properties failed.");
    }
  }
}

function fail(e: unknown, prefix: string): { ok: false; error: string } {
  if (e instanceof OnshapeError) {
    if (e.status === 401 || e.status === 403)
      return { ok: false, error: `${prefix} — API key rejected (${e.status}). Check access key + secret.` };
    if (e.status === 404) return { ok: false, error: `${prefix} — not found (404). Wrong document/workspace id?` };
    return { ok: false, error: `${prefix} ${e.message}` };
  }
  return { ok: false, error: `${prefix} ${e instanceof Error ? e.message : String(e)}` };
}
