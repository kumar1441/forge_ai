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

Forge reads chat settings (provider, base URL, model) from `%APPDATA%\Forge\config.json`. Keys never live in the config file. Resolution order, everywhere: (1) per-slug DPAPI file `%APPDATA%\Forge\keys\<slug>.key` (e.g. `nvidia.key`), (2) per-slug env var, (3) the legacy shared client-id file (auto-adopted under the slug on first use), (4) the bare `FORGE_PROVIDER_KEY` fallback.

| Slug | Per-slug env var |
|---|---|
| `deepseek` | `FORGE_PROVIDER_KEY_DEEPSEEK` |
| `openrouter` | `FORGE_PROVIDER_KEY_OPENROUTER` |
| `nvidia` | `FORGE_PROVIDER_KEY_NVIDIA` |
| `openai` | `FORGE_PROVIDER_KEY_OPENAI` |
| `anthropic` | `FORGE_PROVIDER_KEY_ANTHROPIC` |
| `ollama` | — (local, no key) |

Slugs normalize to uppercase with non-alphanumerics as underscores, so a custom slug like `openai-compatible` maps to `FORGE_PROVIDER_KEY_OPENAI_COMPATIBLE`.

1. Create `%APPDATA%\Forge\config.json`:

   ```json
    {
      "provider": "openai-compatible",
      "baseUrl": "https://api.deepseek.com/v1",
      "model": "deepseek-flash",
      "smallModel": "deepseek-flash"
    }

`smallModel` is optional: rewrite chores (enhance, compaction summaries) use it when set, else a built-in cheap default per provider (DeepSeek→`deepseek-flash`, OpenAI→`gpt-6-luna`, Anthropic→`claude-haiku-4-5`), else the current model.
   ```

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
| NVIDIA NIM | `openai-compatible` | `https://integrate.api.nvidia.com/v1` |
| Ollama (local) | `openai-compatible` | `http://localhost:11434/v1` |

## Multi-part builds via the SolidWorks-MCP server (optional)

Chat routes assembly and 3+-step from-scratch builds to the
[SolidWorks-MCP](https://github.com/hjbaard/SolidWorks-MCP) server, which Forge launches itself —
no extra setup beyond what the server needs:

1. Install uv from `astral.sh/uv` (you already need Python 3.11+).
2. With SolidWorks open, run `uvx solidworks-mcp --selftest` once to verify your install.
3. Ask Forge to build something multi-part — it starts the server on first use.

Override the launch in `%APPDATA%\Forge\mcp.json` if needed:

```json
{ "command": "uvx", "args": ["solidworks-mcp"], "startTimeoutMs": 300000, "enabled": true, "timeoutMs": 300000 }
```

`enabled: false` disables the server (multi-part builds will ask you to re-enable it). The Settings cog exposes the same switch plus the per-call timeout under "MCP server" (`timeoutMs`, milliseconds in config, shown/entered as seconds in the UI; both timeouts floor at 30s); `startTimeoutMs` covers launch+handshake only. This is a single local stdio-only server — there is intentionally no Add/Edit/Delete server list like Kilo's MCP manager, and no remote URLs, headers, or OAuth (Kilo defaults are 10s local / 15s remote; Forge uses 300s with a 30s floor because SolidWorks launches are slow). A failed launch reports the process error in chat (server stderr is drained, not shown). `/mcps` shows status in chat.

## Keyless mode

With no provider configured and no key set, the deterministic local parser handles a large part of the catalog — free, offline, instant.

## Using the chat

The composer sits at the bottom of the panel: `Enter` sends, `Shift+Enter` adds a line, `Esc` closes popovers (or stops a run), `/` starts a slash command. All of the below is panel plus host behavior as implemented today.

### Model picker

The pill next to Attach shows the short model id (or `model` when none is set). Clicking it opens the model popover: search box on top, rows grouped as Recent / Favorites / vendor prefix, footer with count plus Refresh. Refresh asks the host for the live list (`refreshModels`); providers backed by `openai-compatible` return a live list, others report one fixed model. Star toggles a favorite (persisted in `forge_fav_models`); the last five picks are kept in `forge_recent_models`. Keyboard in the search box: `Up/Down/Home/End` move, `Enter` picks (highlighted row, else the active model, else the first row), `Esc` closes. Switching posts `setModel` and applies to the next run.

### Enhance

`✨ Enhance` rewrites the current draft into a build-ready spec via the host `enhance` action (needs SolidWorks connected and a provider key for the current slug, except Ollama). The rewritten text replaces the draft and a `↩` button appears; clicking it — or `Ctrl/Cmd+Z` immediately after enhancing — restores the pre-enhance draft. Typing a new character clears the undo state. Empty draft inserts hint text instead of sending.

### Prompt history and chat history

`↑` recalls the previous sent prompt, `↓` moves newer (persisted in `forge_history`, last 100). Navigation only triggers on the first/last line so multiline editing keeps caret movement; the unsent draft is stashed on the first `↑` and restored when paging past the newest. `🕘` opens the chat-history popover (persisted in `forge_sessions`, newest 30 chats, 100 turns each): search filters by title or prompt text, clicking a row restores that transcript and loads its agent conversation host-side, `✕` deletes one chat, `+ New chat` clears the panel.

### Revert, unrevert, regen

Each user turn has a `↩ revert` button. It stops any in-flight run, drops that turn and everything after it from the transcript, truncates the host agent history to the kept turn count (`revert` with `sid` + `keep`), puts the reverted prompt back in the composer for editing, and stashes the removed turns for one step. `/unrevert` restores that stash (same chat only; a chat switch clears it). `↻` and `/regen` re-run the last non-command prompt; with no prior prompt they report that there is nothing to regenerate.

### Transcript compaction

`/compact` and the `🗜` top-bar button fold all but the newest 10 turns into a collapsed `🗜 N earlier turns compacted` block. With a provider connected the host summarizes the folded text (`compact` action, plain-text bullets); otherwise the block keeps a numbered titles digest. The Settings cog holds `Auto-compact` plus `Compact at` (default on, 60, floored at 10). The host applies the number to its agent-message history and retains the newest 30; the panel applies the same number to transcript turns and keeps the newest 10. The host disables entirely when the toggle is off. A 200-step runaway breaker caps agent loops regardless of approvals.

### Top bar

`%` pill: shows `hist N% · disk M%` (agent history vs compaction threshold, local storage vs ~5 MB cap; exact bytes in the tooltip). `▾`: usage dropdown — storage used bar, local storage bytes, turns (live / stored), provider · model, tokens today (in / out, host `usage` reply), agent history (message count / token count, host `HistoryCount` / `AgentTokCount`). `🔍`: in-chat find bar — highlights all matches, `↑/↓` or `Enter/Shift+Enter` jumps, counter shows `n/m` or `none`, `✕` or `Esc` clears. `🗜`: manual compact (refuses while a compact or — unless stopped — a run is in flight; short chats report there is nothing to compact).

### Slash commands

The dispatcher (`slashRun`) supports exactly: `/clear` (= `/new`: fresh chat, resets host conversation), `/settings` (open Settings), `/help` (command list), `/regen` (= `/regenerate`: re-run last prompt), `/model` (= `/models`: open model picker), `/provider [slug]` (switch with `deepseek|openrouter|nvidia|openai|anthropic|ollama`, or open Settings with no arg; `/connect` opens Settings), `/allow <tool>` (always allow), `/deny <tool>` (block), `/undeny <tool>` (unblock), `/unrevert` (= `/redo`: restore last revert; `/undo` reverts the last turn), `/auto-approve [on|off]` (master switch, or report state; aliases `/autoapprove /approve-all /approveall`), `/history` (= `/sessions`, `/resume`, `/continue`: open chat history), `/rename <title>` (names the current chat; history has no rename button yet), `/mcps` (read-only MCP server status), `/status` (open the usage dropdown), `/compact` (= `/summarize`, `/smol`, `/condense`: fold older turns), `/copy` (copy last answer), `/copy-session` (copy whole chat), `/export` (save rendered transcript as `forge-chat.md` download — rendered turns only, no think/tool metadata), `/about` (provider/model info card, no diagnostics copy), `/review` (send a canned manufacturability review of the active model; extra args ignored), `/thinking` (= `/toggle-thinking`: hide/show all thinking blocks; click a block header or press Enter on it to toggle one). Blocks auto-collapse past ~4k characters (a manual expand sticks) and cap at 50k with a truncation note. Interrupted runs keep partial output under an "Interrupted — partial thinking kept" header. Anything else renders the unknown-command card plus the same help text. Leading-`/` input never reaches the agent. No `/fork` (no shared-session backend), `/agents` (no selectable personas), or `/timestamps` (turns carry no timestamps; find/history cover navigation).

### Auto-approve

Settings holds `Auto-approve` (master) plus `Read tools`, `Build tools`, `Destructive` (persisted as `forge_appr_*`, pushed via `setAutoApprove`). Tool category is host-derived (read/build/destructive by name prefix). When a call needs consent the panel shows an approval card (`△ Permission required`, truncated JSON args, or the doom-loop text for repeated calls) with `Allow once` / `Allow always` / `Reject`; the verdict posts `approveReply`. `/allow <tool>` and `/deny <tool>` append ordered rules to `%APPDATA%\Forge\approval-rules.json` (last match wins); `/undeny <tool>` removes them; the Settings cog lists allowed and denied patterns with per-item remove buttons. Glob patterns are supported (`*`, `?`, case-insensitive). A 200-step runaway breaker caps the loop regardless of approvals.
### @-mentions

Typing `@` opens the file picker: the host searches open SolidWorks documents first (tagged `open`), then the active document's folder plus one level of subfolders (CAD extensions first), filtered by the query, capped at 50 rows; `Enter/Tab` accepts, arrows move, `Esc` closes. Picking adds a chip under the composer (removable with `✕`). On send, only chips whose names are still in the text are posted as `paths`. Host-side the first posted path doubles as the attach-button file, so Compare-class handlers pick it up with no document open. Remaining `@name` tokens resolve against open documents and the active-doc folder.

### Settings cog

`⚙` opens Settings. Provider row: dropdown (`deepseek|openrouter|nvidia|openai|anthropic|ollama`); switching posts `setProvider` and remembers the per-provider model. API-key row appears only when the current provider has no stored key (hidden for Ollama); `Save` stores it via the host DPAPI key store, never in `config.json`. MCP row: `MCP server` enable toggle plus per-call timeout in seconds, with a live status line showing enabled/disabled, running/not-running state, and the launch command. Compact rows: `Auto-compact` toggle plus `Compact at` message threshold. Approval rows: master plus read/build/destructive toggles, the `Always allowed` list, and the `Denied` list. Theme toggle (follows OS by default) and a usage-data telemetry opt-in (default off; only anonymous masked events leave the machine).

Traffic the toggle does *not* cover is functional, not analytics: hosted intent parsing and cross-tool sync (`/api/intent`, `/api/act`, Bifrost feed) send the current prompt plus coarse model context (component names, centroid positions, materials) so the feature can work, and your own provider calls go to the endpoint you configured. The dev-only panel capture (`%TEMP%\forge-capture`, behind `FORGE_PANEL_CAPTURE=1`) never records API keys.
