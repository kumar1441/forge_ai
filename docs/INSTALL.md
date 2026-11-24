# Installing Forge

## Signed installer (recommended once released)

The installer project (`installer/`, WiX v4) produces a single-file **Forge-Setup.msi** that registers the add-in for you — no `regasm`, no `.bat`. Once a release ships a **signed** MSI (SignPath OSS, free for open source), it installs on Smart App Control ON machines and the flow is: download from GitHub Releases → run → enable Forge in SolidWorks → Tools → Add-ins.

Status: the MSI project + build script (`installer/build-msi.ps1`) are in the repo. Build the MSI yourself on a machine with SolidWorks (needed for the interop assemblies), with an optional local cert:

```
powershell -ExecutionPolicy Bypass -File installer\build-msi.ps1           # unsigned
powershell -ExecutionPolicy Bypass -File installer\build-msi.ps1 -Sign -CertThumbprint <sha1>
```

## From source (current path)

Build, register, and configure the SolidWorks add-in from source.

## Prereqs

- Windows, SolidWorks 2022+ (3DEXPERIENCE and desktop installs both work)
- .NET Framework 4.8
- Visual Studio 2022 (or Build Tools)

## Build

From the repo root:

```
dotnet build solidworks/Forge.SolidWorks.csproj -c Release -p:Platform=x64
```

Close SolidWorks before every rebuild — the DLL is locked while SW is open (error MSB3027).

If your SolidWorks install is not under the default path, add `-p:SwInteropRoot="<install>\SOLIDWORKS\api\redist"` to the build (see `docs/BUILD.md`).

## Install

1. Register the add-in with regasm (admin PowerShell):

   ```
   regasm /codebase "solidworks\bin\x64\Release\Forge.SolidWorks.dll"
   ```

2. In SolidWorks: **Tools → Add-ins** → check **Forge**.

The Forge panel appears as a task pane inside SolidWorks.

> **Panel never appears?** Windows Smart App Control blocks unsigned code from loading regardless of file-unblocking or Defender exclusions. If the panel is missing, Smart App Control must be off (Windows Security → App & browser control). On some builds that switch is one-way — it can't be re-enabled without reinstalling Windows.

## Configure a provider

Unlike Kilo there is no sign-in, zero-config provider, or provisioning wizard: `/connect` and `/provider` only open Settings or switch slugs. Create `config.json` + a key as below, or run keyless — the deterministic local parser handles a large part of the catalog with no provider at all.

Forge reads chat settings from `%APPDATA%\Forge\config.json`. Keys never live in the config file. Resolution order, everywhere: (1) per-slug DPAPI file `%APPDATA%\Forge\keys\<slug>.key` (e.g. `nvidia.key`), (2) per-slug env var, (3) the legacy shared client-id file (auto-adopted under the slug on first use), (4) the bare `FORGE_PROVIDER_KEY` fallback. For `opencode`, `OPENCODE_API_KEY` (Kilo parity) is accepted between (2) and (3); with no key found anywhere, Forge sends the literal bearer token `public` — **no key = free models only (public)**. For `opencode-go` the same chain applies (`FORGE_PROVIDER_KEY_OPENCODE_GO` then `OPENCODE_API_KEY`), but a paid subscription key is **required**: no `public` fallback — with no key Forge refuses with `OpenCode Go subscription required`.

| Slug | Per-slug env var |
|---|---|
| `deepseek` | `FORGE_PROVIDER_KEY_DEEPSEEK` |
| `openrouter` | `FORGE_PROVIDER_KEY_OPENROUTER` |
| `nvidia` | `FORGE_PROVIDER_KEY_NVIDIA` |
| `openai` | `FORGE_PROVIDER_KEY_OPENAI` |
| `anthropic` | `FORGE_PROVIDER_KEY_ANTHROPIC` |
| `opencode` | `FORGE_PROVIDER_KEY_OPENCODE` (also `OPENCODE_API_KEY`, Kilo parity; no key → `public`, free models only) |
| `opencode-go` | `FORGE_PROVIDER_KEY_OPENCODE_GO` (also `OPENCODE_API_KEY`, Kilo parity; key required → `OpenCode Go subscription required`, never `public`) |
| `ollama` | — (local, no key) |

`opencode` targets the OpenCode Zen gateway at `https://opencode.ai/zen/v1` using OpenAI-compatible chat completions. Scope: only the chat-completions model families (glm / kimi / minimax / deepseek / qwen / grok) are covered — zen's GPT-family models use `/v1/responses` and claude/qwen-plus use `/v1/messages`, which Forge's `opencode` preset does not implement. No key is required: the gateway serves zero-cost models with the `public` bearer token.

`opencode-go` targets the sibling OpenCode Go subscription gateway at `https://opencode.ai/zen/go/v1`, also over OpenAI-compatible chat completions. It requires a paid subscription key (no `public` fallback) and sends a stable `x-opencode-session` header per conversation. Scope: chat-completions families only — glm / kimi / deepseek / mimo / longcat / hy / space-bunny / minimax (digit-suffixed ids such as `hy3-preview`, `hy4` and `minimax-m3` are grouped under their leading family). Out of scope: Responses-only `grok-*`, `gpt-*-luna`, `muse-spark-*`; Messages-only `qwen3.*-max/plus`, `jev`, `claude-*`, `gemini-*`. The subscription usage/limits endpoint is not implemented. Unknown Go model ids use the approximate 128k context-window fallback — no per-model window numbers are invented.

Slugs normalize to uppercase with non-alphanumerics as underscores, so a custom slug like `openai-compatible` maps to `FORGE_PROVIDER_KEY_OPENAI_COMPATIBLE`.

1. Create `%APPDATA%\Forge\config.json`:

   ```json
   {
     "provider": "openai-compatible",
     "baseUrl": "https://api.deepseek.com/v1",
     "model": "deepseek-flash",
     "smallModel": "deepseek-flash"
   }
   ```

`smallModel` is optional: rewrite chores (enhance, compaction summaries) use it when set, else a built-in cheap default per provider (DeepSeek→`deepseek-flash`, OpenAI→`gpt-6-luna`, Anthropic→`claude-haiku-4-5`; OpenCode Zen / OpenCode Go→none, keeps the current model), else the current model.

2. Set the key as an environment variable — never in the config file:

   ```
   setx FORGE_PROVIDER_KEY "sk-..."   (then restart SolidWorks)
   ```

| Provider | `provider` | `baseUrl` |
|---|---|---|
| Anthropic | `anthropic` | `https://api.anthropic.com/v1` |
| OpenAI | `openai-compatible` | `https://api.openai.com/v1` |
| DeepSeek | `openai-compatible` | `https://api.deepseek.com/v1` |
| OpenRouter | `openai-compatible` | `https://openrouter.ai/api/v1` |
| OpenCode Zen | `openai-compatible` | `https://opencode.ai/zen/v1` |
| OpenCode Go | `openai-compatible` | `https://opencode.ai/zen/go/v1` |
| NVIDIA NIM | `openai-compatible` | `https://integrate.api.nvidia.com/v1` |
| Ollama (local) | `openai-compatible` | `http://localhost:11434/v1` |

### config.json fields written by the panel

`config.example.json` ships only `provider`, `baseUrl`, `model` and `autoApprove`. The Settings cog and command handlers add these keys to the same file at runtime:

| Key | Values | Written by |
|---|---|---|
| `autoApprove` | `{ "master": bool, "read": bool, "build": bool, "destructive": bool }` | `🛡` shield, `/auto-approve`, Settings → Auto-approve |
| `workStyle` | `"review-first"` or `"high-autonomy"` (default high-autonomy). `review-first` sets auto-approve master OFF; `high-autonomy` sets it ON. Both leave destructive gated. | Settings → Work style |
| `reasoningEffort` | `none`, `low`, `medium`, `high`, `xhigh`, `max` (global); per-model override as `reasoningEffort_<modelKey>` | Model-picker effort row, Shift+Tab cycling |
| `compactOn` | bool | Settings → Auto-compact |
| `compactThresholdPct` | 1–100 (default 60); the Settings input restricts entry to 10–95 | Settings → Compact at |
| `compactReservedTokens` | ≥0 (default up to 20 000 / the model's max output) | Settings → Reserved tokens |
| `compactTailTurns` | 0–10 (default 2) | Settings → Tail turns |
| `customModels.<slug>` | `[{ "id": "...", "label": "..." }]` | Settings → Custom models |
| `customProviders[]` | `{ "slug", "label", "kind": "openai-compatible"|"anthropic", "baseUrl", "headers": {...} }` | Settings → Custom providers |

Custom-provider `headers` values support `{env:VAR}`, `${VAR}` and `%VAR%` templating; unresolved tokens are left in place and reported. The base URL is checked against other custom providers and built-in presets, reserved built-in slugs are rejected, and at most 20 custom providers are allowed. Removing the active custom provider falls back to DeepSeek; its key and model ids are kept on disk.

## Multi-part builds via the SolidWorks-MCP server (optional)

Chat routes assembly and 3+-step from-scratch builds to the
[SolidWorks-MCP](https://github.com/hjbaard/SolidWorks-MCP) server, which Forge launches itself —
no extra setup beyond what the server needs:

1. Install uv from `astral.sh/uv` (you already need Python 3.11+).
2. With SolidWorks open, run `uvx solidworks-mcp --selftest` once to verify your install.
3. Ask Forge to build something multi-part — it starts the server on first use.

Override the launch in `%APPDATA%\Forge\mcp.json`. Each `servers[]` entry takes `{name, type, command, args, env, cwd, url, headers, enabled, timeoutMs, startTimeoutMs, watchPaths, alwaysAllow, disabledTools}`; `type` defaults to `stdio`:

```json
{
  "servers": [
    { "name": "default", "type": "stdio", "command": "uvx", "args": ["solidworks-mcp"], "env": {}, "cwd": "", "enabled": true, "timeoutMs": 30000,
      "watchPaths": [], "alwaysAllow": [], "disabledTools": [] },
    { "name": "remote", "type": "http", "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer <token>" }, "enabled": true, "timeoutMs": 15000 }
  ]
}
```

- For `stdio`, `command` (+ `args`) launches the server; `env` adds environment variables and `cwd` sets the working directory.
- For `type: "http"`, `url` is required and `headers` are sent on each request. Both JSON and SSE responses are accepted, and a `Mcp-Session-Id` returned by the server is echoed on later calls. Failed HTTP connections are re-handshaken on the next route.
- Timeouts: `timeoutMs` is the per-call timeout in **milliseconds** — default **30 s for stdio / 15 s for HTTP**, with a **1 s minimum** in config. `startTimeoutMs` covers launch+handshake and follows `timeoutMs` when unset. The Settings UI shows seconds. A short bounded catalog fetch stops one dead server from blocking the whole run.
- `enabled: false` disables that server; multi-part builds will ask you to re-enable it. Routing is ordered failover across enabled servers.
- `watchPaths` lists local paths that, when changed, auto-restart that server. `alwaysAllow` and `disabledTools` are per-server glob lists applied to tool names (qualified `{server}_{tool}` or bare); disabled tools are filtered from the catalog.
- The legacy flat form (`{ "command": "uvx", "args": [...], ... }` with no `servers` key) auto-migrates in-memory to `servers[0]` named `"default"` (`McpBridge.LoadServers`); the `servers[]` form is written back on the next save.

Settings → MCP servers exposes the ordered list with add/edit (`mcpAdd`/`mcpUpdate`), start/stop (`mcpStart`/`mcpStop`), enable/timeout (`mcpSet`), delete (`mcpRemove`) and a per-server log viewer (built from a bounded stdio stderr tail). Start/stop act immediately; enable/transport/timeout take effect on the next build. `/mcps` lists servers in chat and accepts `enable|disable|start|stop <name>` and `timeout <name> <secs>` (name omittable when only one server is configured).

Tool names are namespaced `{server}_{tool}` for permission keys; calls resolve qualified name → bare name → glob for back-compat. A winning server's `initialize` instructions are injected into the agent system prompt (4 KB cap) and omitted when every tool of that server is denied. Three read-only tools — `list_mcp_resources`, `list_mcp_resource_templates`, `read_mcp_resource` — expose MCP resources to the agent.

### Project-scoped MCP config

When a SolidWorks document is open, server edits are written per project to `%APPDATA%\Forge\mcp-projects\<hash>.json`, keyed by the active document's path (`ProjectKeyForAssembly(ActiveAssemblyPath())`); project entries are merged over the global `mcp.json` by server name. With no document open, edits go to the global file.

Still non-goals: OAuth, the MCP marketplace, a standalone SSE-only transport, and MCP prompts (see `docs/KILO-PARITY.md`, "Known deltas / non-goals").

## Keyless mode

With no provider configured and no key set, the deterministic local parser handles a large part of the catalog — free, offline, instant.

## Using the chat

### Composer layout and keyboard

The composer is a single box pinned to the bottom. Its textarea grows to `50vh` and can be dragged taller (to `70vh`) with the grip above it; double-click the grip to reset. The inline bottom bar holds `🛡` (auto-approve master), `📎` Attach, `✨ Enhance` (with `↩` undo after enhancing), the `▲` model pill (which also shows the current reasoning-effort chip), `↻` Regenerate, and the round `➤` Send button. While a run is active, Send morphs into a red `■` Stop button; clicking it or pressing `Esc` interrupts and keeps any partial thinking.

`Enter` sends · `Shift+Enter` inserts a line · `Esc` closes popovers, then stops a run · `↑`/`↓` recalls sent prompts (draft preserved) · `Ctrl/Cmd+F` opens the transcript find bar · `/` opens the slash-command dropdown · `Shift+Tab` in the composer cycles reasoning effort when the current model supports it.

### Streaming and turn actions

Thinking blocks stream live with a `live · Ns` timer; a 120-second inactivity watchdog marks a silent block `Interrupted — partial thinking kept` rather than dropping it. Each finished turn gets a small action row: `↩ revert` (conversation-only — the CAD model is untouched), `↻ resend` (revert then re-run that prompt, no duplicate user turn), `⧉ copy` (answer), `⑂ fork` (start a new local chat from that turn), and, on an interrupted turn, `⤷ continue` (re-run the prompt without adding a new user turn).

### Follow-up queue

Sending while a run is active adds the message to a per-chat follow-up queue instead of starting a second run. The queue strip lets you edit (`✎`), move up/down (`↑`/`↓`), send now (`➤`), delete (`✕`) or pause/resume the whole queue. Each chat's queue is saved under `%APPDATA%\Forge\agent\<sid>.queue.json` and the next item auto-dispatches when the current run finishes.

### Model picker

The `▲` pill shows the short model id (or `model` when none is set). Its popover has a search box, an effort row (`none`/`low`/`medium`/`high`/`xhigh`/`max`), the list, and a footer with a `Free only` filter, a count, and ↻ Refresh. Rows are grouped as pinned `Custom`, `🕘 Recent`, Favorites, then by vendor when several vendors are present; the list is capped (refine the search for more). Each row can show `Free` / `BYOK` badges; `ⓘ` opens a detail panel with context window (marked `~` when approximate), max output, capabilities, key type, and pricing — currently always **"pricing unknown"**. Star toggles a favorite (`forge_fav_models`); the last five picks go to `forge_recent_models`. Keyboard: Up/Down/Home/End move, Enter picks, Esc closes. Switching applies to the next run.

OpenAI-compatible providers fetch a live list on Refresh. Anthropic-wire endpoints (the `anthropic` preset and custom providers with kind `anthropic`) have no model-list endpoint and report **one fixed model** — "One fixed model on this provider — nothing to load." Set the id in `config.json` or via a custom model entry.

### Enhance

`✨ Enhance` rewrites the current draft into a build-ready spec via the host `enhance` action (needs SolidWorks connected and a provider key for the current slug, except Ollama). The rewritten text replaces the draft and a `↩` button appears; clicking it — or `Ctrl/Cmd+Z` immediately after enhancing — restores the pre-enhance draft. Typing a new character clears the undo state. Empty draft inserts hint text instead of sending.

### Prompt history and chat history

`↑` recalls the previous sent prompt, `↓` moves newer (persisted in `forge_history`, last 100). Navigation only triggers on the first/last line so multiline editing keeps caret movement; the unsent draft is stashed on the first `↑` and restored when paging past the newest.

The chat-history popover (`🕘` / `/history`) lists chats most-recent-first with relative time, turn count and the model they were saved under. Search filters by title or prompt text; clicking a row restores that transcript and loads its agent conversation host-side, `✎` renames inline, `✕` deletes one chat, and `+ New chat` clears the panel. History is quota-safe: on a full localStorage it progressively drops older turns, then whole chats, then strips thinking blocks; if it still cannot save, the chats stay in memory for the session and a warning is shown. If the browser cache is empty, the host can rebuild the visible transcript from its own persisted turns (`turns_data`).

### Revert, unrevert, regen

`↩ revert` (and `/undo`) stops any in-flight run, drops that turn and everything after it, truncates the host agent history to the kept turn count, puts the prompt back in the composer, and stashes the removed turns for one step. `/unrevert` (alias `/redo`) restores that stash — same chat only; a chat switch, provider change, or new message clears it. `↻` and `/regen` re-run the last non-command prompt. Revert is **conversation-only**: no SolidWorks document snapshot is taken or restored, so rebuild the model by re-running the prompt.

### Transcript compaction

`/compact` (aliases `/summarize`, `/smol`, `/condense`) folds older turns into a collapsed block; with a provider connected the host summarizes the folded text, otherwise a numbered titles digest is kept. Settings holds `Auto-compact`, `Compact at` (context-% threshold, default 60, host range 1–100 / UI 10–95), `Reserved tokens` and `Tail turns`. The host fires once session context usage reaches that % of the model window and keeps the newest tail turns; unknown windows are approximate. The `Auto-compact` toggle disables it entirely. A 200-step runaway breaker caps agent loops regardless of approvals.

### Top bar

The `%` pill shows live context usage (used ÷ model window) with an "approx" label when the window is unknown; the adjacent `▾` opens a usage dropdown with tokens, turns, provider/model, completion tokens and agent-history counts. `🔍` toggles the find bar described above. `🗜` triggers manual compaction (refuses mid-run unless stopped).

### Slash commands

The dispatcher (`slashRun`) supports exactly these commands (aliases in parentheses):

`/new` (`/clear`) · `/compact` (`/summarize`, `/smol`, `/condense`) · `/undo` · `/redo` (`/unrevert`) · `/regen` (`/regenerate`) · `/copy` (`/copy-session`) · `/export` · `/model` (`/models`) · `/provider` (`/connect`) · `/mcps` · `/settings` · `/history` (`/sessions`, `/resume`, `/continue`) · `/rename` · `/thinking` (`/toggle-thinking`) · `/status` · `/review` · `/about` · `/help` · `/allow` · `/deny` (`/undeny`) · `/auto-approve` (`/autoapprove`, `/approve-all`, `/approveall`) · `/diff` · `/timeline` · `/share` · `/init` · `/themes`

Notes: `/export` downloads rendered turns as `forge-chat.md`; `/share` saves the transcript to a local file; `/diff` and `/timeline` are host-generated views; `/init` captures CAD context (below); `/themes` offers Dark/Light. `/continue` opens chat history — use the per-turn `⤷ continue` button to resume an interrupted run. Leading-`/` input never reaches the agent; unknown commands render the help text. There is no `/fork`, `/agents` or `/goal` slash command (forking is the per-turn button).

Answers support rendered markdown, including fenced code blocks with a copy button.

### Auto-approve

Settings holds `Auto-approve` (master) plus `Read tools`, `Build tools`, `Destructive` (pushed via `setAutoApprove`). The composer's `🛡` shield flips the same master switch as Settings/Auto-approve and `/auto-approve`; all four values persist in `config.json` under `autoApprove` and survive restarts, while destructive stays gated by its own toggle. Work style can change the master default (see config fields above). When a call needs consent the panel shows an approval card (`△ Permission required`, truncated JSON args, or the doom-loop text) with `Allow once` / `Allow always` / `Reject`. `/allow <tool>` and `/deny <tool>` append ordered rules to `%APPDATA%\Forge\approval-rules.json` (last match wins); `/undeny` removes them. Glob patterns are supported. Per-server `alwaysAllow` / `disabledTools` and per-tool allow/ask/deny (`tool-policy.json`) also apply.

### @-mentions

Typing `@` opens the file picker: open SolidWorks documents first, then the active document's folder and one level of subfolders, filtered by the query and capped at 50 rows; `Enter`/`Tab` accepts, arrows move, `Esc` closes. Picking adds a chip under the composer (removable with `✕`). On send, chips still named in the text are posted as `paths`.

### CAD context and paste-to-attach

`/init` asks the host to capture the active SolidWorks document (version, part/assembly/drawing, open-document count, path). The host writes `%APPDATA%\Forge\agent\<sid>.cadctx.json` (`McpBuild.SetCadContext` / `CadCtxPath`) and folds the summary into the agent system message as "CAD context for this session: …". With no document open it reports that context is unavailable.

You can also paste files directly into the composer (`pasteAttach` → `ApplyPasteAttach`): file data is base64-decoded to a temp folder (`%TEMP%\Forge-paste`), capped at **10 MB per file and the first 3 files**; already-existing file paths are attached directly. The attached file doubles as the input for compare-class prompts.

### Settings cog

`⚙` opens Settings: provider dropdown; API-key row (hidden for Ollama, shown only when no key is stored); **Custom providers** (slug/label/kind/URL, headers with `{env:VAR}`); **Custom models** per slug; **Auto-compact** with `Compact at`, `Reserved tokens`, `Tail turns`; **Work style** (Review-first / High-autonomy); **Reasoning** display mode (Expanded/Preview/Headline) plus the effort row in the model picker; **Auto-approve** (master + read/build/destructive, always-allowed and denied lists); **Per-tool policy** dropdowns (explicit entries beat glob rules); **MCP servers** (add/edit/start/stop/enable/timeout/log, with env/cwd/headers/watchPaths/alwaysAllow/disabledTools); **Notifications** toggle for OS toasts; theme toggle (follows OS by default); and a usage-data telemetry opt-in (default off).

Traffic the telemetry toggle does not cover is functional, not analytics: hosted intent parsing and cross-tool sync (`/api/intent`, `/api/act`, Bifrost feed) send the current prompt plus coarse model context (component names, centroid positions, materials), and your own provider calls go to the endpoint you configured. The dev-only panel capture (`%TEMP%\forge-capture`, behind `FORGE_PANEL_CAPTURE=1`) never records API keys.

### Notifications

Windows balloon toasts (`NotifyIcon.ShowBalloonTip`) fire for approval-card arrival, run done/failed and interrupts, and Bifrost/teammate events. The Settings → `Notifications` toggle persists to `config.json` `"notify"` and defaults to ON when absent.

## Troubleshooting

- **No models list for an Anthropic-wire endpoint.** Only `openai-compatible` providers expose a live `/models` list. Anthropic and custom providers of kind `anthropic` show a single fixed model ("One fixed model on this provider — nothing to load"). Set the model id in `config.json` or register it under Custom models.
- **"Local storage is full" history warning.** The panel is keeping chats in memory for this session only; nothing was lost. Free browser storage or delete an old chat from history and saving resumes automatically. Older turns, then whole chats, are dropped before this point, and thinking blocks are stripped first.
- **MCP project scope surprises.** With a SolidWorks document open, MCP server edits are saved per assembly under `%APPDATA%\Forge\mcp-projects\<hash>.json` and merged over the global `mcp.json`; with no document open, edits go to the global file. If a server you added "disappears", open the same document again (or check the global file). Changing `watchPaths` triggers requires the server to be enabled.
- **MCP server won't start.** A disabled server reports its name and asks you to re-enable it in Settings → MCP servers. Stdio stderr is captured in the bounded server log (per-server `mcpLog` viewer); chat shows only curated error text. Timeouts default to 30 s stdio / 15 s HTTP with a 1 s config minimum.
- **Panel never appears** — see the Smart App Control note under Install.
