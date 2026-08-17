/**
 * ICadAdapter — TypeScript mirror of shared/Forge.Cad/ICadAdapter.cs.
 * Op groups an adapter doesn't implement are absent + declared in capabilities.
 * All canonical units: mm, degrees, kg. All results honest — expected failures
 * set error, never throw; never fabricate.
 */

import type {
  CadCapabilities,
  CadDocumentResult,
  CadExportResult,
  CadMassPropsResult,
  CadPlane,
  CadResult,
} from "./types";

export interface IDocumentOps {
  activeDocument(): Promise<CadDocumentResult>;
  open(urlOrPath: string): Promise<CadDocumentResult>;
  createPart(): Promise<CadDocumentResult>;
}

export interface ISketchOps {
  beginSketch(plane: CadPlane): Promise<CadResult>;
  line(x1: number, y1: number, x2: number, y2: number): Promise<CadResult>;
  circle(cx: number, cy: number, radiusMm: number): Promise<CadResult>;
  endSketch(): Promise<CadResult>;
}

export interface IFeatureOps {
  extrudeBoss(depthMm: number): Promise<CadResult>;
  extrudeCut(depthMm: number): Promise<CadResult>; // depthMm <= 0 => through-all
  fillet(radiusMm: number): Promise<CadResult>;
}

export interface IGeometryOps {
  massProperties(): Promise<CadMassPropsResult>;
}

export interface IAssemblyOps {
  components(): Promise<CadResult>;
}

export interface IDrawingOps {
  createFromActiveModel(): Promise<CadResult>;
}

export interface IDataOps {
  setParameter(name: string, valueMm: number): Promise<CadResult>;
  getParameter(name: string): Promise<CadResult>;
}

export interface IExportOps {
  exportStep(path: string): Promise<CadExportResult>;
}

export interface ICadAdapter {
  readonly hostId: string; // "solidworks" | "inventor" | "onshape" | "freecad" | "fusion"
  readonly capabilities: CadCapabilities;

  readonly documents?: IDocumentOps;
  readonly sketch?: ISketchOps;
  readonly features?: IFeatureOps;
  readonly geometry?: IGeometryOps;
  readonly assembly?: IAssemblyOps;
  readonly drawing?: IDrawingOps;
  readonly data?: IDataOps;
  readonly export?: IExportOps;
}
