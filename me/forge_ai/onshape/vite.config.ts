import { defineConfig } from "vite";

// Relative base so the built bundle can be iframed from any static host path
// (Onshape integrated apps load our page inside a document-tab iframe).
export default defineConfig({
  base: "./",
  build: { outDir: "dist", target: "es2020" },
});
