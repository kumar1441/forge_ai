# Kilo Parity

Forge AI is a SolidWorks-panel fork carrying Kilo's chat/agent UX into CAD. This document is a ledger of what is ported from Kilo, how close the shipped behavior is, and what is deliberately out of scope.

**Status key:** `Full` = same behavior; `Close` = same intent, adapted to the CAD host or with known small differences; `Partial` = core behavior present but a documented limitation remains.

**"Where" names symbols** (classes, methods, DOM ids, config keys), never line numbers — line references rot on every rebuild. Everything below was read out of the current source; no behavior is inferred from older documentation.

## 1. Composer, keyboard & input ledger

| Feature | Kilo behavior | Forge behavior | Status | Where (symbols) |
|---|---|---|---|---|
| Single-box composer | One input box with inline actions | One `.composer` box: drag handle (`.askgrip`), textarea (`#intent`, grows to `50vh`, drag to `70vh` via `askMaxPx`/`grow`), inline bottom bar (`.askrow`): `🛡` `#shield`, `📎` `#attach`, `✨` `#enhance`, `↩` `#enhanceUndo`, `▲` `#modelpick` (with `#effortpill`), `↻` `#regen`, `➤`/`■` `#send`. `Esc` interrupts via staged `closePops` → `markInterrupted` → `post({action:"stop"})`. | Close | `.composer`, `.askgrip`, `askMaxPx`, `grow`, `#intent`, `#shield`, `#attach`, `#enhance`, `#enhanceUndo`, `#modelpick`, `#effortpill`, `#regen`, `#send`, `setWorking`, `markInterrupted` |
| Enter / Shift+Enter | Enter sends, Shift+Enter newline | Same; `intent.addEventListener("keydown", …)` calls `submitText()` only when `!e.shiftKey`, and `grow()` keeps the box sized. | Full | `intent` `keydown` handler, `submitText`, `grow` |
| Prompt history ↑/↓ | Recall sent prompts, preserve draft | `histList`/`hIdx`/`draftStash`; ↑ only at first line, ↓ only at last line so multiline caret movement survives; persisted as `forge_history` (last 100). | Full | `histList`, `hIdx`, `draftStash`, `forge_history` |
| Transcript find | Ctrl/Cmd+F find with match count | `#findbar` with `#findinput`, `#fcount` (`n/m` or `none`), `#fcase` match-case toggle, `#fprev`/`#fnext`, `#fx`; Ctrl/Cmd+F and Esc handled at document level; `runFind` caps highlights at `FIND_MAX` 400. | Full | `#findbar`, `runFind`, `FIND_MAX`, document `keydown` handler |
| Slash autocomplete | `/` dropdown with matching commands | `#slashpop` populated by `slashCheck` from `SLASH_CMDS`; Enter/Tab accepts, arrows/Home/End move, Esc closes; keyboard routing owns Enter while `slashOpen`. | Close | `SLASH_CMDS`, `slashCheck`, `slashAccept`, `slashPaint`, `#slashpop` |
| @-mention file picker | `@` opens file chips | `menOpen` picker owned by the composer keydown handler; host searches open documents then the active document folder; picks become chips sent as `paths` on generate. | Full | `menOpen`, `menAccept`, `renderChips`, `submitText`, host `DoAttach`/mention search |
| Chat draft survival | Unsent draft kept across actions | `draftStash`/`preEnhance` survive popovers and enhance undo; cleared on send. | Full | `draftStash`, `preEnhance`, `submitText` |
| Themes | Dark/light | `applyTheme`/`setTheme` persisted as `forge_theme`, follows OS when unset; `/themes` and Settings `data-theme-set` buttons switch palette. | Full | `applyTheme`, `setTheme`, `forge_theme`, `/themes` dispatch, `data-theme-set` |

## 2. Streaming, turns, queue & sessions ledger

| Feature | Kilo behavior | Forge behavior | Status | Where (symbols) |
|---|---|---|---|---|
| Send → stop morph | Send button becomes stop while running | `setWorking(true)` flips `#send` text to `■`, title to Stop, and sets `data-stop="1"`; click or Esc posts `stop`. | Full | `setWorking`, `#send`, `markInterrupted`, `post({action:"stop"})` |
| Live thinking | Streaming reasoning blocks | `thinkStart`/`think`/`thinkEnd` messages in `handle`; a 500 ms interval (`setInterval(…, 500)`) updates `live · Ns`; blocks expand past ~4k chars and cap at 50k with a truncation note. | Close | `handle` (`m.type === "thinkStart"/"think"/"thinkEnd"`), `thinkTimer` tick, `thinkEl._len` |
| Inactivity watchdog / cancel | Reasoning survives a stalled or cancelled run | `armThinkTimeout` re-armed on every delta with a 120 s silence window; on timeout/interrupt the header becomes `Interrupted — partial thinking kept (click to expand)` and partial text is retained; `runInterrupted` + `dataset.interrupted` mark the turn. | Close | `armThinkTimeout`, `clearThinkTimeout`, `markInterrupted`, `runInterrupted` |
| Per-turn action row | Revert / resend / copy / fork / continue on a turn | `buildTurnActions` renders `↩ revert`, `↻ resend`, `⧉ copy`, `⑂ fork`, and (only when interrupted) `⤷ continue`. Resend calls `revertBookkeeping` then `run(q)` (revert-then-regenerate, no duplicate user turn). Fork posts `{action:"fork"}`; Continue posts `{action:"continue"}`. | Close | `buildTurnActions`, `revertBookkeeping`, `turnIndexOf`, `ForkSession`, host `continue`/`fork` handlers |
| Regen / undo / redo | Re-run last prompt, revert, restore revert | `/regen` re-runs `lastPrompt`; `/undo` clicks the last turn's revert control; `/redo` = `/unrevert` restores `lastReverted` (same chat only). Composer `↻` is the same regen. | Full | `/regen` dispatch, `/undo`, `unrevert`, `lastReverted`, `#regen` |
| Follow-up queue | Queue messages during a run | While `working`, `submitText` → `enqueueText` → host `QueueAdd`. Strip shows `✎` edit, `↑`/`↓` reorder, `➤` send-now, `✕` delete, `⏸ Pause`/`▶ Resume`; cap 50. Persisted per sid as `%APPDATA%\Forge\agent\<sid>.queue.json` via `PersistQueue`; `RunGenerateTracked` finally → `DispatchNextQueued` auto-dispatches after each run. | Close | `queueRender`, `enqueueText`, `QueueAdd`, `QueueEdit`, `QueueRemove`, `QueueMove`, `QueueSendNow`, `PersistQueue`, `DispatchNextQueued`, `RunGenerateTracked`, `SidQueue` |
| Continue after stop | Resume an interrupted turn | Host `continue` reuses `LastIntent(csid)`; when the last turn was interrupted and was an MCP/agent turn it calls `McpBuild.StripLastUserTurn` before re-running, so the user turn is not duplicated. Panel `beginContinuation` renders a `bubble user cont` marker. Note: the `/continue` **slash command** opens chat history; the resume action is the `⤷ continue` button. | Close | host `continue` handler, `LastIntent`, `McpBuild.StripLastUserTurn`, `beginContinuation`, `/continue` history branch |
| Session history | Persisted chats, searchable | `forge_sessions` (newest 30 chats, 100 turns each); `renderHistoryList` sorts most-recent-first by `updatedAt`, shows `ago()` relative time + turn count + saved model, searches titles and prompt text, with inline rename (`✎`) and delete (`✕`) and `+ New chat`. | Close | `saveSessions`, `loadSessions`, `renderHistoryList`, `applyRename`, `clearPanel`, `forge_sessions` |
| Quota-safe persistence | History keeps saving under storage pressure | `safeSet` tries the full value, then `degradedCandidates` progressively trims to 8 turns, to 10 chats, then strips `.think` stubs (`stripThinkStubs`); on total failure it keeps the session in `memStore` and `noteQuota` shows a "Local storage is full" warning. | Close | `safeSet`, `degradedCandidates`, `stripThinkStubs`, `noteQuota`, `memStore` |
| Host transcript rescue | Restore conversation when cache is missing | `loadHistory` → `historyLoaded` with `turns_data` from `BuildHostTurns`; panel renders up to 30 turns only when the localStorage cache had no turns. | Close | host `loadHistory` handler, `BuildHostTurns`, `historyLoaded`, `handle` (`m.turns_data`) |
| Enhance prompt | Cheap-model rewrite with one-step undo | Host `enhance` action (small-model routing, fail-closed); `✨`/`↩` in the composer and Ctrl/Cmd+Z restore `preEnhance`; typing clears undo state. | Full | host `enhance` action, `ProviderFactory.GetSmallModel`, `preEnhance`, `#enhanceUndo` |
| In-chat + OS notifications | Cards and desktop toasts | In-chat cards plus WinForms `NotifyIcon.ShowBalloonTip` for approvals, done/failed and teammate events; Settings `Notifications` toggle persists and defaults ON. | Full | `McpBuild.NotifyHook`, host toast core, `notifyGet`/`notifySet`, `#notifytgl` |

## 3. Providers & model picker ledger

| Feature | Kilo behavior | Forge behavior | Status | Where (symbols) |
|---|---|---|---|---|
| Keyless public provider | Free models with no key | `opencode` slug falls back to the literal bearer `public` when no key is found; targets `https://opencode.ai/zen/v1` (chat-completions families only). | Full | `ProviderFactory.Resolve`, `ProviderFactory.SlugFor`, `OpenAiCompatibleClient`, `MissingKeyMessage` |
| Subscription provider with custom transport | Paid gateway session handling | `opencode-go` targets `https://opencode.ai/zen/go/v1`, requires a key (no `public` fallback → `OpenCode Go subscription required`), and sends `x-opencode-session` per conversation. | Close | `ProviderFactory.Resolve`, `MissingKeyMessage`, `OpenAiCompatibleClient` (`x-opencode-session`) |
| Per-slug keys | Keys stored per provider | `KeyStore` DPAPI files per slug (`%APPDATA%\Forge\keys\<slug>.key`); `StoredProviderKeyForSlug` lookup order; env `FORGE_PROVIDER_KEY_<SLUG>`; Settings can replace/remove a key; never in `config.json`. | Full | `KeyStore`, `StoredProviderKeyForSlug`, `ProviderFactory.EnvSlug`/`KeyEnvFor` |
| Custom providers | Add/edit/delete custom endpoints | Settings `customProviderAdd`/`Update`/`Remove` → `CustomProviderAdd`, `customProviders[]` in `config.json`; slug/kind/URL validation, base-URL collision guard against other customs and built-in presets, reserved slugs, cap 20. Headers are stored in `customProviders[].headers` with `{env:VAR}` / `${VAR}` / `%VAR%` expansion via `ParseProviderHeaders` → `ExpandEnvTokens`. A label field is stored for display, with a label-impersonation guard rejecting labels that match a built-in provider display name. | Close | `customProviderAdd`, `CustomProviderAdd`, `ParseProviderHeaders`, `ExpandEnvTokens`, `ReservedSlugs`, `CustomKinds`, `ProviderFactory.DisplayNameFor` |
| Model picker | Favorites, recents, search, grouped list | `#modelpop` with `#msearch`, `Custom` pinned first, `🕘 Recent`, Favorites, vendor grouping when >1 vendor, `Free only` filter (`#modelfree`), `MODEL_MAX` cap, keyboard Up/Down/Home/End/Enter/Esc; picks drive the next run. | Full | `renderModelList`, `getRecents`, `forge_fav_models`, `forge_recent_models`, `MODEL_MAX`, `#modelpop` |
| Model details | Context, output, capabilities, pricing | The `ⓘ` detail row shows context (with `~` when approximate), max output, capabilities, `Key: BYOK/provider`, and `Free`/`BYOK` badges from `ModelInfo`. `pricing` is always null with `pricingUnknown = true`, so the panel renders "pricing unknown". | Partial | `ProviderFactory.ModelInfo`, `ContextWindowFor`, `ByokSlugs`, `FreeSlugs`, panel `mdetail`/`mbadge` rendering |
| Reasoning effort selector | Effort levels per model | `effseg` buttons `none/low/medium/high/xhigh/max`, Shift+Tab cycling via `EFFORTS`+`setEffort`, per-model persistence (`reasoningEffort_<key>`) and global `reasoningEffort`; availability gated by `SupportsReasoningEffort`. | Close | `#effseg`, `EFFORTS`, `setEffort`, `setReasoningEffort`, `ProviderFactory.SupportsReasoningEffort` |
| Work style | Approval presets | Settings `styleseg` maps `review-first` (auto-approve master OFF, read/build ON, destructive OFF) and `high-autonomy` (master ON, read/build ON, destructive OFF) via `ApplyWorkStyle`; persisted as `workStyle` in `config.json`. | Close | `setWorkStyle`, `ApplyWorkStyle`, `styleseg`, `workStyle` |
| Auto-approve master | Unattended-run switch | Composer `🛡` shield, `/auto-approve` and Settings master all drive `McpBuild.ApproveMaster`; read/build/destructive plus master persist under `autoApprove` in `config.json`. Destructive stays gated. | Full | `#shield`, `/auto-approve` dispatch, `setAutoApprove`, `McpBuild.ApproveMaster`, `autoApprove` |
| Approval rules | Last-match-wins allow/deny | Ordered rules in `approval-rules.json`, glob matching on qualified or bare tool names; per-tool allow/ask/deny in `tool-policy.json`; per-server `AlwaysAllow`/`DisabledTools` glob lists; approval cards pause the loop. | Close | `McpBuild.Rules`, `ApprovalFor`, `AddAlwaysAllow`, `SetDenied`, `GateToolAsync`, `SetToolPolicy`, `tool-policy.json` |

## 4. Context, compaction & rendering ledger

| Feature | Kilo behavior | Forge behavior | Status | Where (symbols) |
|---|---|---|---|---|
| Auto-compaction | Manual + context-% threshold | `/compact` (aliases `/summarize`, `/smol`, `/condense`) folds older turns; host `TryContextUsagePct`/`CompactHistoryIfNeeded` triggers at a context-% threshold, keeping newest tail turns, with legacy message-count fallback. | Full | `/compact` dispatch, `compactTranscript`, `McpBuild.ConfigureCompaction`, `TryContextUsagePct`, `CompactHistoryIfNeeded` |
| Context-% pill | Live context usage | `#ctxpill`/`renderCtx` show used ÷ window from host `CtxSnapshot` fields (`ctxUsed`, `ctxWindow`, `ctxApprox`), with an "approx" label for unknown windows; usage dropdown shows tokens, turns, provider/model, completion tokens. | Full | `#ctxpill`, `renderCtx`, `CtxSnapshot`, `ctxUsed`/`ctxWindow` |
| Compaction settings | Threshold + tuning | `setCompact` clamps the threshold to **1–100** (default 60) and accepts `reservedTokens` (≥0) and `tailTurns` (0–10); emits `compactConfig` and persists `compactOn`, `compactThresholdPct`, `compactReservedTokens`, `compactTailTurns`. The Settings number input itself restricts the threshold to 10–95. | Close | `setCompact`, `compactConfig`, `LoadHostPreferences`, `pushCompactCfg`, `#compactat` |
| Markdown answers | Rendered markdown, copyable code | `renderMarkdown` handles headings, lists, quotes, tables, inline code and fenced code blocks; each fence gets a `.mdcodewrap` header with a `.mdcopy` button; plain text falls back to `.mdplain`. | Full | `renderMarkdown`, `.mdcodewrap`, `.mdcopy`, `.mdplain` |

## 5. MCP integration

MCP is closer to Kilo than earlier documentation claimed, but it is still not complete. `McpBridge` speaks two transports: `stdio` launches a child process with redirected stdin/stdout/stderr and newline-delimited JSON-RPC 2.0; `http` posts to a streamable-HTTP endpoint, accepts **both JSON and SSE** response bodies, and echoes a server-supplied `Mcp-Session-Id` on later requests (`RpcOnServerAsync`). The handshake is `initialize` → `notifications/initialized` → `tools/list` → `tools/call`; `notifications/tools/list_changed` and `notifications/resources/list_changed` invalidate the cached tool/resource lists (`notification` handling in `McpBridge`). The default stdio launch is `uvx solidworks-mcp`.

| Feature | Kilo behavior | Forge behavior | Status | Where (symbols) |
|---|---|---|---|---|
| Transports | stdio + streamable HTTP | `type:"stdio"|"http"`, `env`/`cwd` for stdio, `url`/`headers` for HTTP; JSON + SSE accepted, `Mcp-Session-Id` echoed; failed HTTP connections are re-handshaken on the next route. Legacy flat config migrates to `servers[0]` named `default`. SSE is an accepted response format, **not** a standalone SSE-only transport. | Partial | `McpServerConfig`, `LoadServers`, `SaveServers`, `RpcOnServerAsync`, `RouteAsync` |
| Timeouts | ~10 s local / 15 s remote | Per-server `timeoutMs` defaults to **30 s (stdio) / 15 s (http)** with a **1 s minimum** (`DefaultStdioTimeoutMs`, `DefaultHttpTimeoutMs`, `MinConfiguredTimeoutMs`); `startTimeoutMs` follows `timeoutMs` when unset (`EffectiveStartTimeoutMs`); a separate bounded catalog fetch (`CatalogPerServerTimeoutMs`) stops one dead server blocking the run. | Close | `McpServerConfig`, `DefaultStdioTimeoutMs`, `DefaultHttpTimeoutMs`, `MinConfiguredTimeoutMs`, `EffectiveStartTimeoutMs`, `CatalogPerServerTimeoutMs` |
| Server lifecycle | Enable/disable/start/stop/restart | Settings list writes `mcpAdd`/`mcpUpdate`/`mcpSet`/`mcpRemove`; enable/timeout take effect on the next build, `mcpStart`/`mcpStop` act immediately; `/mcps` is writable (`enable|disable|start|stop|timeout <name>`, name omittable when one server via `TryResolveServerName`). Disabled servers throw an actionable error via `EnsureStartedAsync`. | Close | `mcpAdd`, `mcpUpdate`, `mcpSet`, `mcpRemove`, `mcpStart`, `mcpStop`, `mcpStatus`, `TryResolveServerName`, `EnsureStartedAsync` |
| Tool namespacing | `{server}_{tool}` permission keys | `ListAllToolsAsync` emits `{server}_{tool}` qualified names with dedupe and a 256-tool cap (`MaxCatalogTools`); calls route qualified → bare → glob for back-compat (`TrySplitQualified`, `CallToolOnServerAsync`, `RouteAsync`). | Full | `ListAllToolsAsync`, `TrySplitQualified`, `CallToolOnServerAsync`, `MaxCatalogTools`, `McpBuild.BareToolName` |
| Project scope | Project-scoped MCP config | `SyncMcpProjectScope` sets `McpBridge.ProjectKey` from `ProjectKeyForAssembly(ActiveAssemblyPath())`; `LoadServersForProject`/`SaveServersForProject` merge project entries over global into `%APPDATA%\Forge\mcp-projects\<hash>.json`. | Close | `SyncMcpProjectScope`, `ProjectKey`, `ActiveAssemblyPath`, `ProjectKeyForAssembly`, `LoadServersForProject`, `SaveServersForProject`, `ProjectConfigPath` |
| watchPaths | Auto-restart on file changes | Each server has `watchPaths`; `WatchFiles`/watch handlers re-arm on change with a busy guard and auto-restart the server. | Close | `McpServerConfig.WatchPaths`, watch setup/change handlers in `McpBridge` |
| Per-server tool policy | Server-scoped tool allow/deny | `alwaysAllow` and `disabledTools` string arrays per server, glob-matched against qualified and bare names (`McpBuild` server policy check); disabled tools are filtered from the catalog. | Close | `McpServerConfig.AlwaysAllow`/`DisabledTools`, `McpBuild` server policy check, `GlobMatch` |
| Instructions in context | Server instructions injected | `StoreInstructions`/`GetInstructions` capture the `initialize` instructions (4 KB cap, `ServerInstructionsSection`); `AllToolsDenied` suppresses them when every tool of the winning server is denied. | Full | `StoreInstructions`, `GetInstructions`, `InstructionsParts`, `AllToolsDenied`, `ServerInstructionsSection` |
| Resources | List/read MCP resources | Three agent tools — `list_mcp_resources`, `list_mcp_resource_templates`, `read_mcp_resource` (`ResourceToolList`/`Templates`/`Read`, `HandleResourceToolAsync`) backed by `ListResourcesAsync`, `ListResourceTemplatesAsync`, `ReadResourceAsync` (8 KB result cap). MCP **prompts** are not exposed. | Partial | `ResourceToolList`, `ResourceToolTemplates`, `ResourceToolRead`, `HandleResourceToolAsync`, `McpBridge.ListResourcesAsync`/`ListResourceTemplatesAsync`/`ReadResourceAsync` |
| Health / logs | Ping and diagnostics | `PingAsync` issues a JSON-RPC `ping` (available as an API helper; no dedicated panel button). Stdio stderr is appended to a bounded tail (`ServerLogTailChars` 8000) and surfaced through `mcpLog` → a panel log viewer; chat still sees curated `McpException` text. | Partial | `PingAsync`, `AppendServerLog`, `ServerLogTailChars`, `ServerLog`, `mcpLog` handler, `McpException` |

## 6. Fork-original features (not Kilo ports)

These exist in Forge but have no Kilo counterpart; they are listed so gaps vs Kilo are not confused with Forge-only work.

- Deterministic local parser / keyless mode — `IntentLayer`, `IntentParseDirect`, offline intent fallback in `ForgePanel`.
- From-scratch recipe creation — `CreateFromRecipe`, `RecipeExecutor`, `RecipeModels`, `CreatePlate`/`Sphere`/`Cylinder`/`Cone`/`Flange`/`Torus`/`Tube`.
- Compound create + honest refusal and fit/mate-idempotency/mirror-skip guards — `CompoundCreate`, `CreateOnTop`, the `Create*` refusal guards, `AddBoss`/`AddHole`/`AddPocket`, the `Add*Mate` `AlreadyDone` checks, `MirrorSkip`.
- Per-slug DPAPI keys, small-model routing and temperature gating — `KeyStore`, `ProviderFactory.GetSmallModel`/`SupportsTemperature`.
- Doom-loop and runaway breaker — `McpBuild` repeated-call approval card, synthetic ids never persisted, 200-step cap.
- Privacy scrubbing and telemetry gating — `Telemetry.Scrub`, `ForgeData.Mask`/`HARD EXCLUSIONS`, opt-in-off default.
- Bifrost polling and quota/ceiling trimming — `ForgeApi`, `ForgePanel` Bifrost timer, `McpBuild` argument/result caps.
- Extension-guidance error strings and safety scaffolding — `Create*` "ask for a bare shape" messages, `OriginalGuard`, fail-closed guardrail violations, `OpCounter`, `PanelCapture`, `CorrectionWatcher`.
- CAD context sidecar — `/init` → `DoInitContext` → `McpBuild.SetCadContext` writes `<sid>.cadctx.json` (`CadCtxPath`) and folds the summary into the system prompt via `CadContextSummary`.
- Paste-to-attach — `pasteAttach` → `ApplyPasteAttach`: base64 blobs or file paths to a temp file (`%TEMP%\Forge-paste`), 10 MB per file, first 3 files only.

## Implemented this cycle

The most recent wave added or completed the following (all verified in current source):

- **Composer rebuild:** single box with inline bottom bar, `50vh` textarea with drag-to-`70vh` grip, send→stop morph, up/down history, Ctrl/Cmd+F find with match count and match-case toggle, and slash autocomplete.
- **Streaming & turns:** 500 ms live thinking timer, per-delta inactivity watchdog, partial-thinking preservation with interrupted/timeout labels, and a per-turn action row (`↩ revert`, `↻ resend`, `⧉ copy`, `⑂ fork`, `⤷ continue` when interrupted).
- **Follow-up queue:** edit/reorder/send-now/delete/pause strip, per-sid persistence, and auto-dispatch after each run; Continue resumes an interrupted turn without duplicating the user turn.
- **Sessions:** relative-time/turn-count/model history list with title+prompt search, inline rename/delete, quota-safe degradation with thinking-stub stripping, and host `turns_data` rescue when the cache is empty.
- **Providers:** `opencode` (keyless `public`) and `opencode-go` (`x-opencode-session`, key required); custom providers with `{env:VAR}` header templating and collision guards; picker with favorites/recents/vendor grouping, Free + BYOK badges, free-only filter, per-model detail, "pricing unknown"; reasoning-effort selector with Shift+Tab cycling; work-style → approval defaults; auto-approve shield persisted in `config.json`.
- **Context/rendering:** real context-% pill, configurable threshold with reserved tokens and tail turns plus the `compactConfig` event, and markdown rendering with copyable fenced code blocks.
- **Themes:** persisted dark/light palette and `/themes`.
- **CAD context:** `/init` sidecar folded into the system message and paste-to-attach with the 10 MB / 3-file limits.
- **MCP:** standard timeout defaults (30 s stdio / 15 s http, 1 s min), `watchPaths`, per-server `alwaysAllow`/`disabledTools`, project-scope config keyed by active assembly, `tools/list_changed` refresh, bounded stderr tail with a log viewer, the MCP resources trio, instructions injection with all-tools-denied suppression, and `{server}_{tool}` namespacing with qualified→bare→glob back-compat.

## Known deltas / non-goals

Deliberately not shipped (do not assume Kilo parity here):

- **Multi-agent orchestration** — no `/agents` or selectable personas.
- **Scheduled goals / cron** — no `/goal`; work is run-per-turn only.
- **Codebase indexing** — no repository index; CAD context is a per-session sidecar created by `/init`.
- **Git worktrees / snapshot restore** — revert is conversation-only; the CAD model is untouched. There is no document snapshot to restore.
- **MCP OAuth** — only static `headers` (with `{env:VAR}` expansion) are supported.
- **MCP marketplace / registry install** — servers are added manually in Settings or `mcp.json`.
- **Legacy SSE-only transport** — SSE is accepted as an HTTP response format, but no standalone SSE-only server transport exists.
- **MCP prompts** — resources are implemented; the prompts half of the MCP primitives is not.
- **Full model pricing data** — `ModelInfo` always sets `pricingUnknown = true`; the UI says "pricing unknown".
- **Fork / share beyond local** — `fork` is local only (a new sid via `ForkSession`); `share` is a local file export (`DoShareTranscript`). There is no shared-session backend or remote link service.
- **Structured streaming parts** — answers are rendered as markdown text, not typed content parts.
- **Compaction threshold range** — host accepts 1–100 but the Settings input restricts to 10–95; protocol-level context usage is approximate for unknown model windows.
- **Ping health** — `McpBridge.PingAsync` exists as an API helper but is not wired to a dedicated panel button.
