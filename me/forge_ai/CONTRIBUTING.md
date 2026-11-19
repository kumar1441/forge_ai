# Contributing to Forge

Thanks for helping make CAD talk. This project is young and moves fast — here's how to get a PR merged.

## The shape of a good PR

One PR = one focused change. A new command is exactly three things:

1. **A spec row** — the intent → action mapping entry.
2. **A handler class** — one class, one job, no static state.
3. **A test** — read-only handlers: expected output. Mutating handlers: the independent re-measurement that proves it worked.

## Non-negotiables

- **Fail closed.** If the handler can't verify its own result, it reports the limitation honestly. Never claim success you didn't measure. Never ship a stub or a TODO-scaffold.
- **GroundTruth.** Every mutation is independently re-measured after execution. If verification fails, the change is rolled back or reported — the user is never told it worked when it didn't.
- **SolidWorks API landmines are documented** in [docs/SOLIDWORKS-GOTCHAS.md](docs/SOLIDWORKS-GOTCHAS.md). Read it before touching any SW API call — several documented APIs silently no-op or lie on current builds, and the gotchas file is the map of which.
- **No new dependencies** without a note in the PR description justifying it.
- **net48, C# 7.3.** COM objects (RCWs) are released in `finally` blocks. No async-over-sync deadlocks (`ConfigureAwait(false)` in library code).
- **Build must stay green:** `dotnet build solidworks/Forge.SolidWorks.csproj -c Release -p:Platform=x64` → 0 errors, 0 warnings.

## Process

1. Open an issue first for anything bigger than a typo — the roadmap lives on [GitHub Issues](https://github.com/kumar1441/forge_ai/issues).
2. Keep diffs reviewable: touch only what the change needs.
3. Every PR is reviewed by the maintainer before merge. No self-merges, no exceptions.

## Code of conduct

Be kind, be direct, be honest about what works and what doesn't. Students are the audience — write like they're reading.
