/**
 * Canonical CAD model — TypeScript mirror of shared/Forge.Cad (C#).
 * The multi-CAD spec is the source of truth; keep semantics identical:
 * canonical units are ALWAYS mm, degrees, kg. Onshape REST returns meters — convert at
 * the adapter boundary, never beyond it.
 */

export type CadDocKind = "unknown" | "part" | "assembly" | "drawing";

/** Support level for one op group (✅/🟡/❌ semantics from the capability matrix). */
export type CadSupport = "supported" | "degraded" | "absent";

export interface CadCapability {
  level: CadSupport;
  /** Required for degraded/absent — shown to the user, never swallowed (fail-closed). */
  reason?: string;
}

export const cap = {
  yes: (): CadCapability => ({ level: "supported" }),
  degraded: (reason: string): CadCapability => ({ level: "degraded", reason }),
  absent: (reason: string): CadCapability => ({ level: "absent", reason }),
};

export interface CadCapabilities {
  documents: CadCapability;
  sketch: CadCapability;
  features: CadCapability;
  geometry: CadCapability;
  assembly: CadCapability;
  drawing: CadCapability;
  data: CadCapability;
  export: CadCapability;
}

export interface CadDocInfo {
  kind: CadDocKind;
  title: string;
  /** Onshape: no filesystem path — the did/wid/eid binding is the identity (remote-doc doctrine). */
  path: string;
  /** Onshape binding: document/workspace/element ids. Empty on hosts that don't need them. */
  did?: string;
  wid?: string;
  eid?: string;
}

export interface CadResult {
  ok: boolean;
  error?: string;
}

export interface CadDocumentResult extends CadResult {
  document?: CadDocInfo;
}

export interface CadMassPropsResult extends CadResult {
  volumeMm3?: number;
  surfaceAreaMm2?: number;
  centerOfMassMm?: [number, number, number];
  massKg?: number;
  /** false => mass unknown, NOT a default-density guess (GroundTruth honesty doctrine). */
  massTrustworthy?: boolean;
  note?: string;
}

export interface CadExportResult extends CadResult {
  path?: string;
  bytesWritten?: number;
  verification?: string;
}

export type CadPlane = "XY" | "YZ" | "XZ";
