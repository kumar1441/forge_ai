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

Forge reads chat settings (provider, base URL, model) from `%APPDATA%\Forge\config.json`. Keys live in per-provider DPAPI-encrypted key files, falling back to the `FORGE_PROVIDER_KEY` env var — never in the config file.

1. Create `%APPDATA%\Forge\config.json`:

   ```json
   {
     "provider": "openai-compatible",
     "baseUrl": "https://api.deepseek.com/v1",
     "model": "deepseek-chat"
   }
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
{ "command": "uvx", "args": ["solidworks-mcp"], "startTimeoutMs": 300000 }
```

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

`/compact` and the `🗜` top-bar button fold all but the newest 10 turns into a collapsed `🗜 N earlier turns compacted` block. With a provider connected the host summarizes the folded text (`compact` action, plain-text bullets); otherwise the block keeps a numbered titles digest. The Settings cog holds `Auto-compact` plus `Compact at` (default on, 60 agent messages, floored at 10). The host honors the same toggle/threshold independently and disables entirely when the toggle is off or the threshold is non-positive. The panel also auto-compacts once the transcript passes 40 turns when auto-compact is on.

### Top bar

`%` pill: local-storage used versus ~5 MB (click for exact bytes). `▾`: usage dropdown — storage used bar, local storage bytes, turns (live / stored), provider · model, tokens today (in / out, host `usage` reply), agent history (message count / token count, host `HistoryCount` / `AgentTokCount`). `🔍`: in-chat find bar — highlights all matches, `↑/↓` or `Enter/Shift+Enter` jumps, counter shows `n/m` or `none`, `✕` or `Esc` clears. `🗜`: manual compact (refuses while a compact or — unless stopped — a run is in flight; short chats report there is nothing to compact).

### Slash commands

The dispatcher (`slashRun`) supports exactly: `/clear` (= `/new`: fresh chat, resets host conversation), `/help` (command list), `/regen` (= `/regenerate`: re-run last prompt), `/model` (= `/models`: open model picker), `/provider [slug]` (switch with `deepseek|openrouter|nvidia|openai|anthropic|ollama`, or open Settings with no arg), `/allow <tool>` (always allow), `/deny <tool>` (block), `/undeny <tool>` (unblock), `/unrevert` (= `/redo`: restore last revert; `/undo` reverts the last turn), `/auto-approve [on|off]` (master switch, or report state), `/history` (= `/sessions`, `/resume`, `/continue`: open chat history), `/status` (open the usage dropdown), `/compact` (= `/summarize`: fold older turns). Anything else renders the unknown-command card plus the same help text. Leading-`/` input never reaches the agent.

### Auto-approve

Settings holds `Auto-approve` (master) plus `Read tools`, `Build tools`, `Destructive` (persisted as `forge_appr_*`, pushed via `setAutoApprove`). Tool category is host-derived (read/build/destructive by name prefix). When a call needs consent the panel shows an approval card (`🔶 Approval needed`, truncated JSON args) with `Approve` / `Always allow` / `Reject`; the verdict posts `approveReply`. `/allow <tool>` and `/deny <tool>` append ordered rules to `%APPDATA%\Forge\agent\approval-rules.json` (last match wins); `/undeny <tool>` removes them; the Settings cog lists allowed and denied patterns with per-item remove buttons.

### @-mentions

Typing `@` opens the file picker: the host searches open SolidWorks documents first (tagged `open`), then the active document's folder plus one level of subfolders (CAD extensions first), filtered by the query, capped at 50 rows; `Enter/Tab` accepts, arrows move, `Esc` closes. Picking adds a chip under the composer (removable with `✕`). On send, only chips whose names are still in the text are posted as `paths`. Host-side the first posted path doubles as the attach-button file, so Compare-class handlers pick it up with no document open. Remaining `@name` tokens resolve against open documents and the active-doc folder.

### Settings cog

`⚙` opens Settings. Provider row: dropdown (`deepseek|openrouter|nvidia|openai|anthropic|ollama`); switching posts `setProvider` and remembers the per-provider model. API-key row appears only when the current provider has no stored key (hidden for Ollama); `Save` stores it via the host DPAPI key store, never in `config.json`. Compact rows: `Auto-compact` toggle plus `Compact at` message threshold. Approval rows: master plus read/build/destructive toggles, the `Always allowed` list, and the `Denied` list.
