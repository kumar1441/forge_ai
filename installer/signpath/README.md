# Signing the Forge MSI with SignPath

Status: **pipeline-ready, not yet live.** No signed MSI exists in this repo and
none is checked in here. Everything below is ready to use; signing starts
working the moment the project owner configures SignPath (checklist at the
bottom).

The local unsigned build path is unchanged: `installer/build-msi.ps1` still
produces a working unsigned MSI, and signing is a strictly opt-in post-build
step via `installer/sign-with-signpath.ps1`. The signing script uses only
PowerShell + `Invoke-RestMethod` against the SignPath REST API — no new
packages, no embedded secrets.

## Required secrets / environment variables

| Variable | Required | What it is | Where to get it |
|---|---|---|---|
| `SIGNPATH_API_TOKEN` | Yes | API token of the SignPath CI user (sent as `Authorization: Bearer`) | SignPath dashboard → CI user → API token; store as a GitHub repo secret, never in code |
| `SIGNPATH_ORGANIZATION_ID` | Yes | SignPath organization id (GUID) | SignPath dashboard → project → signing policy details page / URL |
| `SIGNPATH_PROJECT_SLUG` | Yes | Slug of the SignPath project for this repo | SignPath dashboard → project settings |
| `SIGNPATH_SIGNING_POLICY_SLUG` | Yes | Slug of the signing policy to sign under | SignPath dashboard → project → signing policy |
| `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | No | Artifact configuration to use (default is used when unset) | SignPath dashboard → project → artifact configuration |
| `SIGNPATH_API_URL` | No | API base URL, defaults to `https://app.signpath.io/Api` | Only override for self-hosted SignPath |
| `SIGNPATH_DESCRIPTION` | No | Description attached to the signing request | Defaults to `Forge MSI signing request for <file>` |

`SIGNPATH_ORG_ID` is accepted as a fallback alias for
`SIGNPATH_ORGANIZATION_ID` (it matches the placeholder name already used in
`.github/workflows/msi.yml`). Prefer `SIGNPATH_ORGANIZATION_ID` going forward.

## Exact invocation (local, PowerShell 7)

Build the MSI first (unchanged, still unsigned):

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-msi.ps1
```

Then submit it for signing (reads secrets from the environment only):

```powershell
$env:SIGNPATH_API_TOKEN = "<ci-user-token>"
$env:SIGNPATH_ORGANIZATION_ID = "<organization-id>"
$env:SIGNPATH_PROJECT_SLUG = "<project-slug>"
$env:SIGNPATH_SIGNING_POLICY_SLUG = "<policy-slug>"
powershell -ExecutionPolicy Bypass -File installer\sign-with-signpath.ps1 -MsiPath dist\Forge-Setup-<version>.msi
```

Useful forms:

```powershell
# Auto-pick the newest dist\*.msi, default timeout 600 s, poll every 10 s
powershell -ExecutionPolicy Bypass -File installer\sign-with-signpath.ps1

# Custom output path and longer wait (manual approval can take a while)
powershell -ExecutionPolicy Bypass -File installer\sign-with-signpath.ps1 -MsiPath dist\Forge-Setup-0.1.0.msi -OutPath dist\Forge-Setup-0.1.0.signed.msi -TimeoutSeconds 1800 -PollSeconds 15
```

On success the script writes `<name>.signed.msi` next to the input and prints
the request id. If any required variable is absent, the submission fails, or
the request does not reach `Completed`, the script throws a clear error and
**no file is labelled signed** — the unsigned input is left untouched.

Requires PowerShell 7+ (`Invoke-RestMethod -Form` multipart upload). CI
runners (`windows-latest`, `pwsh`) already have it.

## CI usage

A ready-to-paste job fragment lives at
`installer/signpath/sign-msi.fragment.yml`. It downloads the `forge-msi`
artifact built by the `build-msi` job, signs it with this script (secrets from
`secrets.SIGNPATH_*`), verifies the Authenticode signature, and uploads only
the `.signed.msi`. Wire the secrets in the repo settings first; without them
the sign step fails with the setup message instead of publishing anything.

## Verify the signature

```powershell
$sig = Get-AuthenticodeSignature -FilePath dist\Forge-Setup-<version>.signed.msi
$sig.Status
$sig.SignerCertificate.Subject
```

`Status` must report `Valid`. A locally built unsigned MSI reports
`NotSigned` at the same command.

## Owner checklist (what makes this live)

1. Make the repo public and enroll in SignPath OSS (free for open source).
2. Create the SignPath project for this repo and a signing policy for the MSI.
3. Create a CI user with signing permission on the project and issue its API token.
4. Add repo secrets: `SIGNPATH_API_TOKEN`, `SIGNPATH_ORGANIZATION_ID` (or
   `SIGNPATH_ORG_ID`), `SIGNPATH_PROJECT_SLUG`,
   `SIGNPATH_SIGNING_POLICY_SLUG`.
5. Paste `installer/signpath/sign-msi.fragment.yml` into the release workflow
   so only the signed MSI is attached to GitHub Releases.

## Not possible in this environment

- Producing a real code-signing certificate or a signed MSI here: both need
  the project owner's SignPath account and secrets, which cannot be created
  from this machine.
- Fully hands-off CI verification end to end: the fragment is syntax-checked
  by inspection only; the submit/poll/download path can only succeed once the
  secrets above exist.
