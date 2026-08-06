# Forge

[![GitHub stars](https://img.shields.io/github/stars/kumar1441/forge_ai)](https://github.com/kumar1441/forge_ai) [![Discord](https://img.shields.io/badge/Discord-Forge-%235865F2?logo=discord&logoColor=white)](https://discord.gg/YOUR_INVITE_LINK)

**Talk to SolidWorks. Get verified changes.** Forge is an open-source add-in that turns plain English into real parametric edits inside SolidWorks — and independently re-measures every change before it claims success.

```
"make the bore 25mm"          → sets the dimension, rebuilds, measures: 25.00mm ✓
"mate all the bolts"          → classifies fasteners, seats every mate, re-checks
"check interference"          → clash report, read-only
"color the flanges grey"      → applied, verified
"fillet all sharp edges 2mm"  → rounds found, applied, counted
```

~260 commands across parts, assemblies, and drawings: dimensions, materials, colors, mates, patterns, shells, holes, equations, DXF export, health audits, and more.

**If Forge saves you an afternoon, star it — stars are how power users find it.**

## Why Forge is different

- **It verifies, it doesn't bluff.** Every mutation is independently re-measured against the model (GroundTruth). If Forge can't do it, it says so — never a fake success.
- **Bring your own key.** Forge calls *your* LLM provider directly — Anthropic, OpenAI, DeepSeek, OpenRouter, or a local model via Ollama. Your key never leaves your machine (DPAPI-encrypted, current-user scope). No account, no subscription, no middleman.
- **Works with zero LLM.** A large part of the catalog routes through deterministic local parsing — free, offline, instant.

## Quickstart

1. **Prereqs:** Windows, SolidWorks 2022+, .NET Framework 4.8, Visual Studio 2022 (or Build Tools).
2. **Build:** `dotnet build solidworks/Forge.SolidWorks.csproj -c Release -p:Platform=x64`
3. **Install:** register the output DLL with `regasm /codebase` (admin), then enable Forge in SolidWorks → Tools → Add-ins.
4. **Configure a provider** (or skip — the keyless path still works):

   `%APPDATA%\Forge\config.json`:
   ```json
   {
     "provider": "openai-compatible",
     "baseUrl": "https://api.deepseek.com/v1",
     "model": "deepseek-chat"
   }
   ```
   Set the key as an environment variable — never in the config file:
   ```
   setx FORGE_PROVIDER_KEY "sk-..."   (then restart SolidWorks)
   ```
   (A settings pane with DPAPI-encrypted storage is already in the build and lands with the GUI shortly. Keys are never written to disk in plaintext.)

   | Provider | `provider` | `baseUrl` |
   |---|---|---|
   | Anthropic | `anthropic` | `https://api.anthropic.com/v1` |
   | OpenAI | `openai-compatible` | `https://api.openai.com/v1` |
   | DeepSeek | `openai-compatible` | `https://api.deepseek.com/v1` |
   | OpenRouter | `openai-compatible` | `https://openrouter.ai/api/v1` |
   | Ollama (local) | `openai-compatible` | `http://localhost:11434/v1` |

Full build/install/config details: **[docs/BUILD.md](docs/BUILD.md)** · architecture: **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** · SolidWorks API notes: **[docs/SOLIDWORKS-GOTCHAS.md](docs/SOLIDWORKS-GOTCHAS.md)**

## Telemetry

Forge is fully functional with telemetry **off**. When off, nothing leaves your machine — no counters, no pings, nothing. If you opt in, you choose the tier (anonymous aggregates or full traces) and can change it any time. See the privacy note in docs when telemetry ships; the consent screen explains exactly what leaves in one sentence per tier.

## Contributing

PRs welcome. A new command = one spec row + one handler class + one test — see [CONTRIBUTING.md](CONTRIBUTING.md) and the roadmap on [GitHub Issues](https://github.com/kumar1441/forge_ai/issues). Every diff is reviewed by the maintainer before merge.

## Support Forge

Forge is free, open source, and built one afternoon at a time. If it saves you one, **star the repo** — stars are how power users find it. Know a fellow engineer fighting SolidWorks? Send them this page. Found a bug or want a command? Open an [issue](https://github.com/kumar1441/forge_ai/issues) — report a bug or request a command using the issue templates.

## License

MIT — see [LICENSE](LICENSE). Built by Ravi — made with love for the CAD community.
