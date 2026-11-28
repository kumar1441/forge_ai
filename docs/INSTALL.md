# Installing Forge

## Signed installer (recommended once released)

The installer project (`installer/`, WiX v4) produces a single-file **Forge-Setup.msi** that registers the add-in for you — no `regasm`, no `.bat`. A **signed** MSI installs on Smart App Control ON machines and the flow is: download from GitHub Releases → run → enable Forge in SolidWorks → Tools → Add-ins.

Status: the pipeline is ready but **no signed MSI exists in this repo**. Signing requires the project owner to configure SignPath (see `installer/signpath/README.md`); every MSI built locally here is unsigned.

Local unsigned build (Smart App Control must be off — see the note under Install; on some Windows builds that switch is one-way and cannot be re-enabled without reinstalling Windows):

```
powershell -ExecutionPolicy Bypass -File installer\build-msi.ps1           # unsigned
powershell -ExecutionPolicy Bypass -File installer\build-msi.ps1 -Sign -CertThumbprint <sha1>
```

Opt-in signing as a post-build step (needs the secrets documented in `installer/signpath/README.md`):

```
powershell -ExecutionPolicy Bypass -File installer\sign-with-signpath.ps1 -MsiPath dist\Forge-Setup-<version>.msi
```

The script writes `dist\Forge-Setup-<version>.signed.msi` and fails loudly when secrets are absent — it never labels an unsigned file as signed. Verify the result:

```
$sig = Get-AuthenticodeSignature -FilePath dist\Forge-Setup-<version>.signed.msi
$sig.Status
$sig.SignerCertificate.Subject
```

`Status` must report `Valid`. An unsigned local build reports `NotSigned` at the same command.

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

> **Panel never appears?** Windows Smart App Control blocks unsigned code from loading regardless of file-unblocking or Defender exclusions. A locally built (unsigned) MSI requires Smart App Control off (Windows Security → App & browser control); on some builds that switch is one-way — it can't be re-enabled without reinstalling Windows. Signed release MSIs (see above) are trusted with Smart App Control on.

## Configure a provider

Unlike Kilo there is no sign-in, zero-config provider, or provisioning wizard: `/connect` and `/provider` only open Settings or switch slugs. Create `config.json` + a key as below, or run keyless — the deterministic local parser handles a large part of the catalog with no provider at all.

Forge reads chat settings from `%APPDATA%\Forge\config.json`. **Provider API keys** never live in the config file (they use per-slug DPAPI files or env vars) — but custom-provider header **templates** and MCP server `headers`/`env` are plain config values stored in `config.json` / `mcp.json`. Resolution order, everywhere: (1) per-slug DPAPI file `%APPDATA%\Forge\keys\<slug>.key` (e.g. `nvidia.key`), (2) per-slug env var, (3) the legacy shared client-id file (auto-adopted under the slug on first use), (4) the bare `FORGE_PROVIDER_KEY` fallback. For `opencode`, `OPENCODE_API_KEY` (Kilo parity) is accepted between (2) and (3); with no key found anywhere, Forge sends the literal bearer token `public` — **no key = free models only (public)**. For `opencode-go` the same chain applies (`FORGE_PROVIDER_KEY_OPENCODE_GO` then `OPENCODE_API_KEY`), but a paid subscription key is **required**: no `public` fallback — with no key Forge refuses with `OpenCode Go subscription required`.

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

`opencode` targets the OpenCode Zen gateway at `https://opencode.ai/zen/v1` using OpenAI-compatible chat completions. Scope: the chat-completions model families (`glm` / `kimi` / `minimax` / `deepseek` / `qwen` / `grok` / `nemotron` / `ling` / `mimo` / `big-pickle`, per `OpenCodeChatFamilies`) — zen's GPT-family models use `/v1/responses` and claude/qwen-plus use `/v1/messages`, which the `opencode` preset does not implement (those wire paths exist only on `opencode-go`). No key is required: the gateway serves zero-cost models with the `public` bearer token.

`opencode-go` targets the sibling OpenCode Go subscription gateway at `https://opencode.ai/zen/go/v1` and routes per model id (`ProviderFactory.WirePathFor` / `ResolveFor`): chat-completions families (`glm` / `kimi` / `deepseek` / `mimo` / `longcat` / `hy` / `space-bunny` / `minimax`, per `OpenCodeGoChatFamilies` — digit-suffixed ids such as `hy3-preview`, `hy4` and `minimax-m3` are grouped under their leading family) stay on OpenAI-compatible chat; Responses-only `grok-*`, `gpt-*-luna`, `muse-spark-*` go through the `OpenAiResponsesClient` (POST `<base>/responses`, `IsResponsesModel`); Messages-only `qwen3.7`/`3.8` and `qwen3.*-max/plus`, `jev`, `claude-*`, `gemini-*` go through `AnthropicClient` pointed at the zen/go base URL (`IsGoMessagesModel`). The model picker lists all three families (the family filter admits any id whose wire path is not `chat`). A paid subscription key is still required (no `public` fallback — with no key Forge refuses with `OpenCode Go subscription required`), and `x-opencode-session` is now stamped per run (`ProviderOptions.SessionId` preferred over the static provider, so concurrent runs never share a session id). Subscription usage/limits are surfaced through the `usageGo` panel→host action: with `opencode-go` active the context popover shows a Usage affordance that posts `{action:"usageGo"}`, and the host replies `{type:"usageGo", meters:{rolling,weekly,monthly}, rawAvailable}` (or an `error` when the provider is not opencode-go / no key / no subscription), rendered as a compact card with percent used, status (`ok` / `rate-limited`) and localized reset time per meter — three meters, no spend/cost breakdown — plus a "limits not reported by the gateway" note when `rawAvailable` is false. Unknown Go model ids use the approximate 128k context-window fallback — no per-model window numbers are invented.

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

`smallModel` is optional: rewrite chores (enhance, compaction summaries) use it when set, else a built-in cheap default per provider (DeepSeek→`deepseek-flash`, OpenAI→`gpt-6-luna`, Anthropic→`claude-haiku-4-5`; OpenCode Zen / OpenCode Go→`glm-5.3-flash`, the live-catalog default), else the current model.

2. Set the provider API key as an environment variable — never in the config file:

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
| `compactThresholdPct` | 1–100 % (default 60); clamped on both host and Settings input; 0/negative keeps the default | Settings → Compact at |
| `compactReservedTokens` | 0/negative = keep default `min(20 000, model max output)`; a positive value overrides | Settings → Reserved tokens |
| `compactTailTurns` | 0/negative = keep default 2; 1–10 overrides (higher values clamped to 10) | Settings → Tail turns |
| `customModels.<slug>` | `[{ "id": "...", "label": "..." }]` | Settings → Custom models |
| `customProviders[]` | `{ "slug", "label", "kind": "openai-compatible"|"anthropic", "baseUrl", "headers": {...} }` | Settings → Custom providers |
| `agents[]` | `[{ "name", "description", "systemPrompt", "tools[]", "model" }]` — max 12 roles, unique non-empty names, built-in names (`general`/`explore`/`verifier`) overridable once; `tools` is an array of tool-name strings (blank = all tools); `model` blank/`null` = current chat model | `/agents` roster, Settings → Subagents, hand-edited `config.json` |

Custom-provider `headers` values are stored **as raw templates** in `config.json` (plain config values, not DPAPI secrets) and support `{env:VAR}`, `${VAR}` and `%VAR%` templating. `ParseProviderHeaders` keeps the template verbatim; `ExpandedProviderHeaders` → `ExpandEnvTokens` resolves `{env:VAR}` against the process environment **at request time** into `ProviderOptions.Headers` / `McpBuild.ProviderHeaders`. Unresolved tokens are left in place and reported. MCP server request `headers` in `mcp.json` are likewise plain config values but **do** expand the same three token forms at use time (`McpBridge.ExpandHeaderValue`, per request on both HTTP transports); stdio `env`/`cwd` pass through verbatim. The base URL is checked against other custom providers and built-in presets, reserved built-in slugs are rejected, and at most 20 custom providers are allowed. Removing the active custom provider falls back to DeepSeek; its key and model ids are kept on disk.

## Multi-part builds via the SolidWorks-MCP server (optional)

Chat routes assembly and 3+-step from-scratch builds to the
[SolidWorks-MCP](https://github.com/hjbaard/SolidWorks-MCP) server, which Forge launches itself —
no extra setup beyond what the server needs:

1. Install uv from `astral.sh/uv` (you already need Python 3.11+).
2. With SolidWorks open, run `uvx solidworks-mcp --selftest` once to verify your install.
3. Ask Forge to build something multi-part — it starts the server on first use.

Override the launch in `%APPDATA%\Forge\mcp.json`. Each `servers[]` entry takes `{name, type, command, args, env, cwd, url, headers, enabled, timeoutMs, startTimeoutMs, watchPaths, alwaysAllow, disabledTools, oauth:{clientId,authorizationUrl,tokenUrl,scope[],redirectPort}}`; `type` (`stdio` / `http` / `sse`) defaults to `stdio`:

```json
{
  "servers": [
    { "name": "default", "type": "stdio", "command": "uvx", "args": ["solidworks-mcp"], "env": {}, "cwd": "", "enabled": true, "timeoutMs": 300000,
      "watchPaths": [], "alwaysAllow": [], "disabledTools": [] },
    { "name": "remote", "type": "http", "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer <token>" }, "enabled": true, "timeoutMs": 15000 },
    { "name": "legacy", "type": "sse", "url": "https://example.com/sse", "enabled": true,
      "oauth": { "clientId": "<id>", "authorizationUrl": "https://example.com/authorize", "tokenUrl": "https://example.com/token", "scope": ["mcp"], "redirectPort": 53821 } }
  ]
}
```

- For `stdio`, `command` (+ `args`) launches the server; `env` adds environment variables verbatim (no token expansion) and `cwd` sets the working directory.
- For `type: "http"`, `url` is required and `headers` are sent on each request, with `{env:VAR}` / `${VAR}` / `%VAR%` tokens expanded at use time (`ExpandHeaderValue`). Both JSON and SSE responses are accepted, and a `Mcp-Session-Id` returned by the server is echoed on later calls. Failed HTTP connections are re-handshaken on the next route.
- For `type: "sse"` (legacy SSE-only transport), `url` is the event-stream endpoint: the bridge opens a GET event stream, discovers the POST endpoint, then POSTs JSON-RPC to it. Header expansion and OAuth apply the same as `http`.
- `oauth` enables per-server OAuth: `StartOAuthAsync` runs a PKCE flow through the system browser with a `127.0.0.1` loopback listener (default port 53821, overridable via `redirectPort`), stores the DPAPI-encrypted token in `%APPDATA%\Forge\mcp-oauth\` (never in `mcp.json`), injects it as a Bearer token per request with proactive refresh, and surfaces `none` / `configured` / `authenticated` / `expired` / `needs_auth` status with a Sign in / Sign out UI.
- Timeouts: `timeoutMs` is the per-call timeout in **milliseconds** — default **300000 ms (300 s) for stdio / 15000 ms (15 s) for HTTP**, with a **1 s minimum** in config. Stdio keeps a long default because the first run can download the MCP package and the server may start slowly; remote HTTP stays short for Kilo parity (15 s). `startTimeoutMs` covers launch+handshake and follows `timeoutMs` when unset, but the initialize handshake is bumped to **60 s** whenever the configured value is ≤ 30 s (so a legacy 30 s config still gets a 60 s handshake). The Settings UI shows seconds. A separate catalog fetch is bounded to 60 s (`CatalogPerServerTimeoutMs`) so one dead server can't block the whole run.
- `enabled: false` disables that server; multi-part builds will ask you to re-enable it. Routing is ordered failover across enabled servers.
- `watchPaths` lists local paths that, when changed, auto-restart that server (2 s poll by design — no native file watcher). `alwaysAllow` and `disabledTools` are per-server glob lists applied to tool names (qualified `{server}_{tool}` or bare); disabled tools are filtered from the catalog.
- The legacy flat form (`{ "command": "uvx", "args": [...], ... }` with no `servers` key) auto-migrates in-memory to `servers[0]` named `"default"` (`McpBridge.LoadServers`); the `servers[]` form is written back on the next save.

Settings → MCP servers exposes the ordered list with add/edit (`mcpAdd`/`mcpUpdate`), start/stop (`mcpStart`/`mcpStop`), enable/timeout (`mcpSet`), delete (`mcpRemove`), per-server Ping (`mcpPing` with an `rtt` badge), OAuth Sign in / Sign out (`mcpOAuthStart`/`mcpOAuthClear` with an auth-status badge), and a per-server log viewer (built from a bounded stdio stderr tail). Start/stop act immediately; enable/transport/timeout take effect on the next build. `/mcps` lists servers in chat and accepts `enable|disable|start|stop <name>` and `timeout <name> <secs>` (name omittable when only one server is configured).

Tool names are namespaced `{server}_{tool}` for permission keys; calls resolve qualified name → bare name → glob for back-compat. A winning server's `initialize` instructions are injected into the agent system prompt (4 KB cap) and omitted when every tool of that server is denied. Five read-only tools — `list_mcp_resources`, `list_mcp_resource_templates`, `read_mcp_resource`, `list_mcp_prompts`, `get_mcp_prompt` — expose MCP resources and prompts to the agent (all approval-gated, 8 KB result caps). When a server's tool list changes, the host forwards `mcpToolsChanged` and the panel shows a non-blocking toast ("the next run will use the new list").

### Project-scoped MCP config

When a SolidWorks document is open, server edits are written per project to `%APPDATA%\Forge\mcp-projects\<hash>.json`, keyed by the active document's path (`ProjectKeyForAssembly(ActiveAssemblyPath())`); project entries are merged over the global `mcp.json` by server name. With no document open, edits go to the global file.

Still non-goals: the MCP marketplace, and repo-local `.mcp.json` files (project scope is assembly-keyed instead — see `docs/KILO-PARITY.md`, "Known deltas / non-goals").

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

The `▲` pill shows the short model id (or `model` when none is set). Its popover has a search box, an effort row (`none`/`low`/`medium`/`high`/`xhigh`/`max`), the list, and a footer with a `Free only` filter, a count, and ↻ Refresh. Rows are grouped as pinned `Custom`, `🕘 Recent`, Favorites, then by vendor when several vendors are present; the list is capped (refine the search for more). Each row can show `Free` / `BYOK` badges; `ⓘ` opens a detail panel with context window (marked `~` when approximate), max output, capabilities, key type, and pricing — live `$input / $output per 1M tokens` when the upstream `/models` list reports prices (OpenCode Zen/Go, OpenRouter), otherwise **"pricing unknown"** (no prices are invented). Star toggles a favorite (`forge_fav_models`); the last five picks go to `forge_recent_models`. Keyboard: Up/Down/Home/End move, Enter picks, Esc closes. Switching applies to the next run.

OpenAI-compatible providers fetch a live list on Refresh; for `opencode-go` the list includes the responses-only and messages-only families alongside chat families (admitted by `WirePathFor`). Anthropic-wire endpoints (the `anthropic` preset and custom providers with kind `anthropic`) have no model-list endpoint and report **one fixed model** — "One fixed model on this provider — nothing to load." Set the id in `config.json` or via a custom model entry.

### Enhance

`✨ Enhance` rewrites the current draft into a build-ready spec via the host `enhance` action (needs SolidWorks connected and a provider key for the current slug, except Ollama). The rewritten text replaces the draft and a `↩` button appears; clicking it — or `Ctrl/Cmd+Z` immediately after enhancing — restores the pre-enhance draft. Typing a new character clears the undo state. Empty draft inserts hint text instead of sending.

### Prompt history and chat history

`↑` recalls the previous sent prompt, `↓` moves newer (persisted in `forge_history`, last 100). Navigation only triggers on the first/last line so multiline editing keeps caret movement; the unsent draft is stashed on the first `↑` and restored when paging past the newest.

The chat-history popover (`🕘` / `/history`) lists chats most-recent-first with relative time, turn count and the model they were saved under. Search filters by title or prompt text; clicking a row restores that transcript and loads its agent conversation host-side, `✎` renames inline, `✕` deletes one chat, and `+ New chat` clears the panel. History is quota-safe: on a full localStorage it progressively drops older turns, then whole chats, then strips thinking blocks; if it still cannot save, the chats stay in memory for the session and a warning is shown. If the browser cache is empty, the host can rebuild the visible transcript from its own persisted turns (`turns_data`).

### Revert, unrevert, regen

`↩ revert` (and `/undo`) stops any in-flight run, drops that turn and everything after it, truncates the host agent history to the kept turn count, puts the prompt back in the composer, and stashes the removed turns for one step. `/unrevert` (alias `/redo`) restores that stash — same chat only; a chat switch, provider change, or new message clears it. `↻` and `/regen` re-run the last non-command prompt. Revert is **conversation-only**: no SolidWorks document snapshot is taken or restored, so rebuild the model by re-running the prompt.

### Transcript compaction

`/compact` (aliases `/summarize`, `/smol`, `/condense`) folds older turns into a collapsed block; with a provider connected the host summarizes the folded text, otherwise a numbered titles digest is kept. Settings holds `Auto-compact`, `Compact at` (context-% threshold, default 60, clamped to 1–100 on both host and UI), `Reserved tokens` and `Tail turns`. A value of 0 (or negative) for the threshold keeps the current default (host falls back to stored config, then 60); 0/negative reserved tokens or tail turns likewise keep their defaults — reserved defaults to `min(20 000, the model's max output)` and tail turns to 2 (positive tail values clamp to 10). The host fires once session context usage reaches that % of the model window and keeps the newest tail turns; unknown windows are approximate. The `Auto-compact` toggle disables it entirely. A 200-step runaway breaker caps agent loops regardless of approvals.

### Top bar

The `%` pill shows live context usage (used ÷ model window) with an "approx" label when the window is unknown; the adjacent `▾` opens a usage dropdown with tokens, turns, provider/model, completion tokens and agent-history counts. `🔍` toggles the find bar described above. `🗜` triggers manual compaction (refuses mid-run unless stopped).

### Slash commands

The dispatcher (`slashRun`) supports exactly these commands (aliases in parentheses):

`/new` (`/clear`) · `/compact` (`/summarize`, `/smol`, `/condense`) · `/undo` · `/redo` (`/unrevert`) · `/regen` (`/regenerate`) · `/copy` (`/copy-session`) · `/export` · `/model` (`/models`) · `/provider` (`/connect`) · `/mcps` · `/agents` · `/settings` · `/history` (`/sessions`, `/resume`, `/continue`) · `/rename` · `/thinking` (`/toggle-thinking`) · `/status` · `/review` · `/about` · `/help` · `/allow` · `/deny` (`/undeny`) · `/auto-approve` (`/autoapprove`, `/approve-all`, `/approveall`) · `/diff` · `/timeline` · `/share` · `/init` · `/themes`

Notes: `/export` downloads rendered turns as `forge-chat.md`; `/share` saves the transcript to a local file (local-only export — no remote link service; `revokeShare` is an unwired stub); `/diff` and `/timeline` are host-generated views; `/init` captures CAD context (below); `/themes` offers Dark/Light; `/agents` lists the subagent roster (below). `/continue` resumes the interrupted turn (falls back to opening chat history when nothing is interrupted) — the per-turn `⤷ continue` button does the same without opening history. Leading-`/` input never reaches the agent; unknown commands render the help text. There is no `/fork` or `/goal` slash command (forking is the per-turn button).

Answers support rendered markdown, including fenced code blocks with a copy button.

### Auto-approve

Settings holds `Auto-approve` (master) plus `Read tools`, `Build tools`, `Destructive` (pushed via `setAutoApprove`). The composer's `🛡` shield flips the same master switch as Settings/Auto-approve and `/auto-approve`; all four values persist in `config.json` under `autoApprove` and survive restarts, while destructive stays gated by its own toggle. Work style can change the master default (see config fields above). When a call needs consent the panel shows an approval card (`△ Permission required`, truncated JSON args, or the doom-loop text) with `Allow once` / `Allow always` / `Reject`. `/allow <tool>` and `/deny <tool>` append ordered rules to `%APPDATA%\Forge\approval-rules.json` (last match wins); `/undeny` removes them. Glob patterns are supported. Per-server `alwaysAllow` / `disabledTools` and per-tool allow/ask/deny (`tool-policy.json`) also apply.

### Subagents

`/agents` lists the active role roster (built-ins + customs) as a card with each role's tool count and model. The model-picker popover shows the same roster with a **Manage** link that opens Settings → Subagents.

**Settings → Subagents** manages custom roles: built-ins (`general`, `explore`, `verifier`) render as read-only rows (`built-in`, or `overridden` when a same-named custom exists); `+ Add` creates a custom role (name, description, systemPrompt, tools one-per-line, optional model override) with inline edit (`✎`) and delete (`✕`). Every add/edit/delete posts the full custom array (`agentsSave`); the host validates, reconfigures the agent core, persists to `config.json`, and replies with the merged roster. The roster reply carries each role's `systemPrompt`, so edited roles round-trip without destroying prompts. Roles added outside the panel (hand-edited `config.json`) are adopted into the panel list once on the first roster reply, prompts included.

**Settings → Subagents → Subagent limits** holds Max depth, Max fan-out, Child turns and Result cap (characters; 8192 ≈ 8 KB): numeric fields with an Unlimited toggle each. Unlimited sends `0`; any other positive integer sends that cap. Every change posts `{action:"agentLimitsSave", maxDepth, maxFanout, maxChildTurns, maxResultCap}`; the host replies `{type:"agentLimits", ... comSerialized:true}` (sent at startup and after every save) and the panel repopulates the fields from that echo. The host clamps values to its hard ceilings. COM serialization shows as a locked ON field: SolidWorks COM access is always serialized — not configurable.

`config.json` shape (optional; at most 12 roles, names unique case-insensitively):

```json
{
  "agents": [
    { "name": "researcher", "description": "read-only CAD analysis", "systemPrompt": "Measure twice, change nothing.", "tools": ["list_dimensions", "get_mass_properties"], "model": null }
  ]
}
```

- `tools`: array of tool-name strings; blank or missing means the role may use all tools.
- `model`: optional override; blank/`null` means the current chat model.
- Built-ins: `general` (all tools), `explore` (read-only CAD analysis), `verifier` (read-only + measurement). A custom entry with the same name overrides the built-in once (duplicates are rejected).

The main agent delegates through the **`spawn_agent`** tool (role + task), with serialized CAD tool access (one SolidWorks mutation path — no parallel CAD writes) and host-enforced depth / fan-out / child-turn / result-cap limits. Per-child status events (`started`/`queued`/`done`/`stopped`/`failed` with `parentSid`, `childSid`, `role`, `name`, `summary`) render as a distinct `.agentcard` in the transcript — header with role badge + name + status badge + expand/collapse caret + an independent Stop button (`{action:"agentStop", childSid}`, disabled/hidden once done/failed/stopped). The expanded body has tabs: Transcript (step/think/result trace), Actions (tool calls + child-tagged approval cards) and Summary (final `agentStatus` report). `agentTrace` (`parentSid`, `childSid`, `kind:"step"|"think"|"tool"|"error"|"result"`, `label`, `detail`, `at`) appends only inside that child's tabs — never as normal transcript steps — with a 200-entry cap per card so finished work stays collapsed to a compact summary line and never pollutes the parent chat. `agentLimits` and `agentTrace` are informational (`INFORMATIONAL_TYPES`) and never finish a live turn. Child sessions carry a persisted subagent marker and stay in chat history with a `subagent` tag: each `historyLoaded` reply includes a `subagents` array (`SubagentSessionsFor`, rows may include a persisted `trace`) that the panel merges into the history list, and restoring one uses the normal `loadHistory` path since child sids are persisted host-side — the card tabs hydrate from that persisted trace when available, otherwise the Transcript tab shows "Trace not saved for this subagent". Pressing Stop / `Esc` still cancels the run along with its child sessions; the per-card Stop halts one child independently.

### @-mentions

Typing `@` opens the file picker: open SolidWorks documents first, then the active document's folder and one level of subfolders, filtered by the query and capped at 50 rows; `Enter`/`Tab` accepts, arrows move, `Esc` closes. Picking adds a chip under the composer (removable with `✕`). On send, chips still named in the text are posted as `paths`.

### CAD context and paste-to-attach

`/init` asks the host to capture the active SolidWorks document (version, part/assembly/drawing, open-document count, path). The host writes `%APPDATA%\Forge\agent\<sid>.cadctx.json` (`McpBuild.SetCadContext` / `CadCtxPath`) and folds the summary into the agent system message as "CAD context for this session: …". With no document open it reports that context is unavailable.

You can also paste files directly into the composer (`pasteAttach` → `ApplyPasteAttach`): file data is base64-decoded to a temp folder (`%TEMP%\Forge-paste`), capped at **10 MB per file and the first 3 files**; already-existing file paths are attached directly. The attached file doubles as the input for compare-class prompts.

### Settings cog

`⚙` opens Settings: provider dropdown; API-key row (hidden for Ollama, shown only when no key is stored); **Custom providers** (slug/label/kind/URL, headers with `{env:VAR}`); **Custom models** per slug; **Auto-compact** with `Compact at`, `Reserved tokens`, `Tail turns`; **Work style** (Review-first / High-autonomy); **Reasoning** display mode (Expanded/Preview/Headline) plus the effort row in the model picker; **Auto-approve** (master + read/build/destructive, always-allowed and denied lists); **Per-tool policy** dropdowns (explicit entries beat glob rules); **MCP servers** (add/edit/start/stop/enable/timeout/log/ping/OAuth sign-in, with env/cwd/headers/watchPaths/alwaysAllow/disabledTools/oauth + auth-status badge); **Notifications** toggle for OS toasts; theme toggle (follows OS by default); and a usage-analytics toggle labeled "Usage analytics (functional intent/Bifrost/provider calls still apply)" (default off).

The toggle label is literal: turning analytics off stops only anonymous masked usage events (`Telemetry`, opt-in, default off; "usage data = anonymous masked events only · off = nothing leaves this machine"). The remaining traffic is functional, not analytics: hosted intent parsing and cross-tool sync (`/api/intent`, `/api/act`, Bifrost feed) send the current prompt plus coarse model context (component names, centroid positions, materials), and your own provider calls go to the endpoint you configured. The dev-only panel capture (`%TEMP%\forge-capture`, behind `FORGE_PANEL_CAPTURE=1`) never records API keys.

### CAD tool additions

Two honestly-scoped additions beyond chat transport (no invented data):

- **Body-level geometry census** — asking "what is this" with no face named returns a whole-document census (`DescribeGeometry.RunBodyCensus`): solid type, body count, volume, bounding-box extents, face-type counts (planar / cylindrical / conical / spherical / toroidal / other), and a hole count. Thread/tap classification is explicitly unavailable — the codebase has no thread/tap detection, so none is claimed.
- **Flat-pattern DXF export** — `FlatDxf` exports each sheet-metal part via `ExportToDWG2` (geometry + bend-lines flag) into `<model-dir>\Forge-Flat-DXF`, verified on disk before counting. A `BEND` layer-table entry is ensured when the exporter omits it (`EnsureBendLayer` writes only the layer definition and never remaps entities), so **bend geometry stays on layer 0 in this build and the `BEND` layer exists but is empty**.

### Notifications

Windows balloon toasts (`NotifyIcon.ShowBalloonTip`) fire for approval-card arrival, run done/failed and interrupts, and Bifrost/teammate events. The Settings → `Notifications` toggle persists to `config.json` `"notify"` and defaults to ON when absent.

## Troubleshooting

- **No models list for an Anthropic-wire endpoint.** Only `openai-compatible` providers expose a live `/models` list. Anthropic and custom providers of kind `anthropic` show a single fixed model ("One fixed model on this provider — nothing to load"). Set the model id in `config.json` or register it under Custom models.
- **"Local storage is full" history warning.** The panel is keeping chats in memory for this session only; nothing was lost. Free browser storage or delete an old chat from history and saving resumes automatically. Older turns, then whole chats, are dropped before this point, and thinking blocks are stripped first.
- **MCP project scope surprises.** With a SolidWorks document open, MCP server edits are saved per assembly under `%APPDATA%\Forge\mcp-projects\<hash>.json` and merged over the global `mcp.json`; with no document open, edits go to the global file. If a server you added "disappears", open the same document again (or check the global file). Changing `watchPaths` triggers requires the server to be enabled.
- **MCP server won't start.** A disabled server reports its name and asks you to re-enable it in Settings → MCP servers. Stdio stderr is captured in the bounded server log (per-server `mcpLog` viewer); chat shows only curated error text. Timeouts default to 300 s stdio / 15 s HTTP with a 1 s config minimum; the initialize handshake gets 60 s when the configured value is ≤ 30 s.
- **Panel never appears** — see the Smart App Control note under Install.
