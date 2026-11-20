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
