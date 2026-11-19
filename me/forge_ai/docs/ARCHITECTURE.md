# Forge — Architecture

Forge is a SolidWorks add-in (net48 C#, COM interop, WebView2 panel) that takes a plain-English CAD intent and executes it as verified SolidWorks actions. The codebase is split into six modules with strict boundaries, so each layer can be reasoned about, tested, and replaced independently.

## Module boundaries

| Module | Owns | Never does |
|---|---|---|
| `Forge.Core` | route table, handler registry, intent pipeline | HTTP to LLM providers |
| `Forge.Providers` | `IProviderClient` adapters, key storage (DPAPI) | knows about SolidWorks |
| `Forge.Handlers` | one class per handler, `IForgeHandler` | static state, direct `SldWorks` refs (use `ISwAdapter`) |
| `Forge.Sw` | `ISwAdapter` over the SW API, RCW discipline | intent logic |
| `Forge.Telemetry` | trace schema, consent, buffer, upload | blocks or alters function |
| `Forge.Ui` | WebView2 pane, settings UI | business logic |

The dependency direction runs downward: `Ui` → `Handlers` → `Sw`/`Providers`, with `Core` coordinating. `Providers` never touches SolidWorks; `Sw` never reasons about intent; `Telemetry` is opt-in and its uploader is never even instantiated when consent is `off`.

## Intent pipeline

Every user intent flows through one pipeline: **parse** (turn the raw request into a structured action + targets), **resolve** (match the action to a handler via the route table, choosing the LLM, keyless regex, or local path), **confirm-or-ask** (if the intent is ambiguous or the handler needs a choice, ask rather than guess), **execute** (run the handler against the model), and **verify** (re-measure the result with an independent check that is separate from the code that performed the mutation — the same truth the GroundTruth regression suite uses). Any failure at any stage surfaces as an honest message and a fallback, never as a fabricated action.

## Handler catalog

The catalog holds **~260 handlers**, each a single self-contained class implementing `IForgeHandler` — one per atomic CAD capability (fastener mating, explode, isolate, material, resize, simplify, dimension edits, pattern operations, and many more). Adding a capability means adding one handler class, one route-table row, and one test; the rest of the pipeline is untouched.

## GroundTruth doctrine

Every mutation Forge makes is independently re-measured before it is reported as done: the verification never reuses the code path that performed the edit, and the expected result is derived by a separate `GroundTruth.*` pass from the raw model state — never from Forge's own claims. If the independent measurement does not match the claim, Forge fails closed: it says so, rolls back or leaves the model untouched, and never presents an unverified change as a success.
