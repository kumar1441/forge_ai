# Building Forge (SolidWorks add-in)

Forge is a .NET Framework 4.8 C# COM add-in hosted inside SolidWorks as a WebView2 Task Pane. This guide covers building from source, registering the add-in, and configuring it.

## Prerequisites

- **Windows 10/11 (x64)** — SolidWorks itself is Windows-only, and so is Forge.
- **Visual Studio 2022** (any edition) **or the Build Tools** with the **C# / .NET desktop** workload.
- **.NET Framework 4.8 developer pack** (target framework is `net48`).
- **SolidWorks 2022 or later (x64)** installed. Forge talks to SolidWorks through the COM interop assemblies that ship with every install.

  The interop references use a hardcoded `HintPath` in `solidworks/Forge.SolidWorks.csproj`:

  ```
  C:\Program Files\Dassault Systemes\SOLIDWORKS 3DEXPERIENCE R2026x\SOLIDWORKS\api\redist\SolidWorks.Interop.*.dll
  ```

  **You will very likely need to edit that path** to match your local SolidWorks install (the interop DLLs live under `<install>\SOLIDWORKS\api\redist\` for every supported version).

- **WebView2 Runtime** — ships with Windows 11 and most Windows 10 installs; the NuGet reference pulls the native loader automatically.

## Build

Close SolidWorks before rebuilding. The add-in DLL is locked while SolidWorks is running, and the build fails with MSB3027 (file-in-use) otherwise.

```powershell
dotnet build solidworks/Forge.SolidWorks.csproj -c Release
```

The build produces `Forge.SolidWorks.dll` (x64, net48) and copies the WebView2 native loader next to it in the output folder — a net48 COM add-in does not probe the `runtimes\` layout a .NET Core app would, so the loader must sit beside the DLL for the panel to initialise.

## Register

Register the add-in from an **elevated** (Run as administrator) prompt so COM can write the registry:

```powershell
regasm /codebase "path\to\output\Forge.SolidWorks.dll"
```

Then start SolidWorks → **Tools → Add-Ins** → tick **Forge**. To remove it later: `regasm /unregister "path\to\output\Forge.SolidWorks.dll"`.

## Configure

Settings resolve in this order: environment variable → `%APPDATA%\Forge\config.json` → empty. No secrets live in the source tree. Reference shapes: see `.env.example` and `solidworks/config.example.json`.

Create `%APPDATA%\Forge\config.json` and choose one of two modes:

**Hosted intent endpoint** — send intents to the Forge cloud endpoint with your API key:

```json
{ "apiKey": "sk-..." }
```

(Also honoured from the `FORGE_MCP_API_KEY` environment variable.)

**BYOK — your own provider.** Bring your own key and point Forge at any OpenAI-compatible endpoint (OpenAI, OpenRouter, Ollama, local servers, …):

```json
{
  "provider": "openai-compatible",
  "baseUrl": "https://api.openai.com/v1",
  "model": "gpt-4o"
}
```

Set the provider key via the `FORGE_PROVIDER_KEY` environment variable (`setx FORGE_PROVIDER_KEY "sk-..."`, then restart SolidWorks). Keys are never read from plaintext fields in config.json — a settings pane with DPAPI-encrypted storage (`ProtectedData`, current-user scope) is already in the build and arrives with the GUI. The key is sent only to the provider you configure. Optional overrides: `modelPrimary` / `modelLight` (and `FORGE_MODEL_PRIMARY` / `FORGE_MODEL_LIGHT` env vars).

## Windows Smart App Control

Forge's **unsigned development builds** will not load if Windows **Smart App Control (SAC)** is turned on — SAC blocks unsigned code from loading regardless of SmartScreen/Defender exclusions or `Unblock-File`. Note that SAC is a one-way switch: turning it off cannot be undone without reinstalling Windows.

For daily dev builds this is a real constraint: build from source on a machine with SAC off, or run a SAC-on machine and rely on the **signed MSI installer** that is in the works (signed releases will load with SAC on, no switch-flipping required).
