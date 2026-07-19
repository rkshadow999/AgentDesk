# AgentDesk Windows Client

[English](README.md) | [简体中文](README.zh-CN.md)

> [!WARNING]
> AgentDesk Alpha currently exposes only **Native compatibility (not sandboxed, `NativeProtected`)**.
> WslStrict intentionally fails closed until complete child-network enforcement can be attested.
> Use native execution only with workspaces you trust.

## Architecture

The AgentDesk desktop client uses .NET 10, WinUI 3, and WebView2. The Rust engine runs as a separate ACP/NDJSON stdio sidecar; the desktop does not link upstream Rust internal APIs.

The WinUI host owns the application window, workspace picker, Windows Credential Manager integration, sidecar lifetime, execution-profile enforcement, permission decisions, Windows notifications, experimental Windows UI Automation, Cloud coordination, and the WebView2 boundary. React surfaces provide the workbench and inspector. Shared .NET contracts do not depend on the UI layer.

Desktop-managed API keys are never placed in the native or WSL sidecar's initial environment. Over the sidecar's private redirected stdio channel, the host calls the logical versioned `agentdesk/v1/credential` extension before the standard ACP `initialize` request; the NDJSON transport writes `_agentdesk/v1/credential` on the wire, as required for ACP extension methods. The engine stores the key only in its process-local memory slot and never echoes it. A missing, rejected, or malformed credential bridge aborts startup before authentication or session creation.

The current source UI includes Execute/Plan mode negotiation, searchable/paged engine sessions, load/rename/archive/fork/compact/rewind, bounded session import/export, local backup/restore, provider Base URL/model/backend settings, validated image attachments, bounded workspace file references, an `AGENTS.md` editor, a capability-gated Memory browser, terminal streaming, diff/plan inspection, permissions, cancellation, language selection, and opt-in notifications. Memory writes and deletes require an engine-advertised two-stage confirmation contract, and the host rejects unchecked mutation capabilities. Its active-session Runtime Dashboard lists background tasks and running subagents, kills tasks, opens subagent detail, and cancels subagents.

The worktree view connects create/list/show/apply/remove/GC, including destination, copy mode, Git ref, merge/overwrite confirmation, conflicts, and dry-run cleanup. Review is deliberately two-stage: the first action prepares a bounded editable request, and the separate Start review action submits it as the standard `engine/prompt`, preserving native-risk acknowledgement and the normal engine permission queue. Settings connect MCP, Skills, Hooks, Plugins, and Marketplace catalogs/actions through a bounded bridge. Environment-variable names may cross the bridge as secret references; secret values, Hook commands/URLs, and Skill metadata are withheld from WebView2. Remote Cloud profiles fail closed for every Plugin mutation and every Marketplace install/update/uninstall action that can rebuild or reload the registry; client-supplied publisher IDs are not trusted, while catalog list/refresh remains available.

Portable builds expose a pinned-key update flow and external updater. Manual checks remain available; background availability polling is persisted but disabled by default, starts only after the user opts in, and never applies an update without an explicit action. MSIX builds do not use that replacement path. The optional Cloud client defaults to local-only and supports explicit remote setup, encrypted session sync/import, hardened recovery-key pairing, cross-device handoff, policy, Runner register/queue/claim/complete, and automation create/list/disable against the separate developer-preview server.

The component and data-ownership design is documented in [Architecture](../docs/ARCHITECTURE.md); security assumptions are in the [threat model](../docs/AGENTDESK-THREAT-MODEL.md).

## Prerequisites

- Windows 11 on x64 or ARM64. ARM64 configuration still requires successful ARM64 CI and real-device launch evidence for the exact release.
- PowerShell 7 and Git.
- Node.js 24 with npm.
- .NET SDK 10.0.302, pinned by [`global.json`](../global.json).
- Rust 1.92, pinned by [`rust-toolchain.toml`](../rust-toolchain.toml).
- Microsoft Edge WebView2 Runtime, included with supported Windows 11 installations.
- WSL2 only when preparing the currently blocked `WslStrict` payload.

The project targets Windows SDK 10.0.26100 with a framework minimum of 10.0.19041, but the AgentDesk community currently tests and supports Windows 11 only.

## Local Build

Build the Web assets and Rust sidecar, then run the parameterized packaging entry point:

```powershell
Set-Location desktop/web
npm ci
npm test
npm run build
Set-Location ../..

cargo build --locked -p xai-grok-pager-bin --profile release-dist --features release-dist

./scripts/agentdesk/Build-AgentDeskPackage.ps1 `
  -Architecture x64 `
  -Mode All `
  -NativeEnginePath ./target/release-dist/xai-grok-pager.exe `
  -WslEnginePath C:\path\to\linux-x64\agentdesk-engine `
  -OutputRoot ./artifacts/agentdesk
```

Use `-Architecture arm64` and the matching Linux ARM64 payload only on an ARM64 build host, and do not describe it as verified until CI and a real-device launch pass. `-WslEnginePath` accepts the architecture-matched Linux binary from the `agentdesk-engine-linux-x64` or `agentdesk-engine-linux-arm64` CI artifact. Omit that parameter for a local package that exposes only Native compatibility and does not include the WSL installation script.

[`AgentDesk.App.csproj`](src/AgentDesk.App/AgentDesk.App.csproj) supports `PackageMode=Portable` and `PackageMode=MSIX`. The build script renames the native sidecar to the default application path, `agentdesk-engine.exe`, and uses [`AgentDesk.Packaging.targets`](../scripts/agentdesk/AgentDesk.Packaging.targets) to add engines and legal notices to the MSIX. The runtime resolves the native engine with `Path.Combine(AppContext.BaseDirectory, "agentdesk-engine.exe")`, so the desktop controller does not require a custom path in the default native profile.

For exact restore/test commands and the difference between local and CI evidence, use [Build and Test](../docs/BUILD-AND-TEST.md). For installing, verifying, upgrading, and removing packages, use [Installation](../docs/INSTALLATION.md).

## Execution Profiles

### NativeProtected

`NativeProtected` is retained as a protocol enum for Windows toolchain compatibility. The UI labels it **Native compatibility (not sandboxed)**. It uses a separate AgentDesk data directory, clears inherited credential environment variables, preserves permission approval, and assigns the sidecar to a Windows kill-on-close Job Object so the process tree is terminated when the host releases its handle or crashes.

It still runs with the current Windows user's full filesystem and network permissions. It is not a security boundary and must not be described as a sandbox. Do not open an unfamiliar or untrusted repository in this mode.

### WslStrict

A Windows executable cannot be used as the WSL sidecar. A Portable package may contain `wsl/agentdesk-engine`, an architecture-matched Linux x64 or ARM64 binary. The Alpha does not import it automatically. From the extracted Portable package, installation would begin with:

```powershell
./Install-AgentDeskWslEngine.ps1
# If more than one eligible distribution is installed:
./Install-AgentDeskWslEngine.ps1 -DistributionName Ubuntu
```

The installer ignores Docker Desktop's internal distributions. With exactly one installed non-Docker distribution it selects that distribution automatically; otherwise pass `-DistributionName` explicitly. Non-root distributions use `sudo`, while a root-only test or custom distribution installs directly. The payload is installed at `/usr/local/bin/agentdesk-engine`, made executable, and verified against the bundled source SHA-256. Set `AGENTDESK_WSL_DISTRIBUTION` to the same installed name before launching AgentDesk when multiple eligible distributions exist. The desktop executes only that installed path; availability also requires a current-architecture ELF and an installed SHA-256 identical to the bundled payload. Path conversion and sidecar startup use the same explicit distribution and fail closed when the selection or installed payload is missing, ambiguous, stale, non-executable, or architecture-incompatible. The MSIX contains the same payload, but the current UI cannot import it from the protected installation directory; the Alpha installation script is therefore supported only from the Portable package.

The desktop fixes `GROK_SANDBOX=strict` and `GROK_SANDBOX_REQUIRE_ENFORCEMENT=1` when launching the WSL sidecar. Landlock/Seatbelt enforcement must become active or the engine exits; it cannot silently downgrade. The current Alpha selects a named existing distribution but does not provision a dedicated distribution with Windows interop and automount disabled, so this profile will not replace a future isolated runner.

Before authentication or session creation, startup calls `agentdesk/v1/health`. The desktop allows `WslStrict` to continue only when the engine returns a complete, structured attestation with all of these properties:

- `configuredProfile` and `activeProfile` are both `strict`.
- `active`, `childNetworkRestricted`, and `enforcementRequired` are all `true`.

If `agentdesk/v1/initialize` or `agentdesk/v1/health` is missing, returns `-32601`, omits an attestation field, or reports any unsatisfied property, the desktop stops the WSL sidecar before authentication and session creation. It never automatically downgrades to native mode. WSL payloads older than the AgentDesk extension in this repository are incompatible and must be replaced by the payload shipped with the same release. Native compatibility does not require the WSL health attestation, but desktop-managed credentials still require the versioned credential bridge and the mode always remains visibly non-sandboxed.

The current seccomp network restriction covers the integrated command launch path, but complete coverage has not been demonstrated for helpers, plugins, hooks, PTYs, and every other child-process entry point. The engine therefore reports `childNetworkRestricted: false`. The desktop rejects the handshake and stops the sidecar. `WslStrict` is deliberately fail-closed in the Alpha; it will remain unavailable until global child-process network enforcement is verifiable and the health response can truthfully report `true`.

## Experimental Windows Automation

The native FlaUI/UIA3 executor, host bridge, and Settings surface support explicit focus-window, invoke, and set-value commands against a supplied process ID and Automation ID and/or accessible name. The path is disabled by default, requires the local preference and current team policy to allow it, serializes operations, and presents an allow-once permission before touching the target. Completion/status events do not echo the entered value, and the inspector WebView2 does not receive automation or permission events.

This executor uses the current Windows user's UI authority. `NativeProtected` does not isolate it because that profile is Native compatibility (not sandboxed); the executor has no operating-system isolation and is not a general autonomous Computer Use implementation. Unit/component tests cover validation, policy, permission correlation, cancellation, event redaction, and host routing; a packaged real-target FlaUI exercise remains a manual release gate.

## Tests

Build Web assets before running .NET tests that exercise packaged surface discovery:

```powershell
Set-Location desktop/web
npm ci
npm test
npm run build
Set-Location ../..

dotnet test desktop/AgentDesk.sln -m:1
dotnet format desktop/AgentDesk.sln --verify-no-changes --no-restore

cargo fmt --package xai-proto-build --package xai-grok-pager-bin `
  --package xai-grok-sandbox --package xai-grok-shell -- --check
cargo test --locked -p xai-proto-build
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
cargo check --locked -p xai-grok-pager --lib

pwsh ./scripts/agentdesk/Test-AgentDeskReleaseScripts.ps1
pwsh ./scripts/agentdesk/Test-AgentDeskUpdateManifest.ps1
pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1
git diff --check
```

The enforced Linux sandbox suite runs in the configured Linux or Docker environment with `SANDBOX_E2E_REQUIRE_ENFORCEMENT=1`.

The latest local evidence is recorded in [Build and Test](../docs/BUILD-AND-TEST.md) and regenerated before publication. The public source repository is [rkshadow999/AgentDesk](https://github.com/rkshadow999/AgentDesk). This is not evidence of a signed public MSIX, ARM64 real hardware, packaged accessibility, or production Cloud/Runner operation.

## CI And Releases

`.github/workflows/agentdesk-windows.yml` performs:

- Node.js 24, .NET 10, and Rust 1.92 tests and builds for Windows x64 and ARM64.
- A pinned protobuf 29.3 download with SHA-256 verification.
- Linux x64 and ARM64 WSL sidecar builds paired with the matching Windows packages.
- Linux sidecar builds on Ubuntu 22.04, including ELF architecture and maximum `GLIBC_2.35` dependency checks recorded in `RUNTIME-COMPATIBILITY.txt`.
- Portable packages, standalone updater inputs, unsigned MSIX packages, SPDX/CycloneDX SBOMs, SHA-256 files, and archives for branches and pull requests.
- GitHub Release publication for `v*` tags, including signed update manifests and a verified bundle of the previous signed Windows release after the first tag.
- Release-configuration integration tests for the optional self-hosted cloud developer preview.
- A Windows real-process Cloud maintenance job that exercises offline backup, restore, rollback evidence, and automatic rollback after failed post-install validation.

These are workflow definitions, not proof that a release ran. The source repository is public, but the current audit does not claim a signed tag MSIX or verified ARM64 device launch.

Release versions accept stable `major.minor.patch` or numbered `ci`, `alpha`, `beta`, `preview`, and `rc` prereleases. Each prerelease channel maps to a non-overlapping MSIX revision range. Stable releases use revision `65535`, allowing a stable package to upgrade prereleases with the same `major.minor.patch`. Other suffixes are rejected.

Branch and pull-request builds produce unsigned MSIX packages for development and CI validation only. A `v*` tag must configure the MSIX signing pair and update-signing key below. If any is absent, the workflow fails rather than publish an unsigned tag MSIX or unsigned update metadata:

- `AGENTDESK_MSIX_PFX_BASE64`
- `AGENTDESK_MSIX_PFX_PASSWORD`
- `AGENTDESK_UPDATE_ECDSA_PRIVATE_KEY_PKCS8_BASE64`

The verifier pins the repository-trusted Publisher to `CN=AgentDesk` and compares both the packaged manifest and actual signer subject against it; it never derives the expected identity from the package under verification. The optional repository variable `AGENTDESK_MSIX_SIGNER_THUMBPRINT` adds a fixed certificate-thumbprint check. Update metadata is signed with ECDSA P-256 and independently checked against the public key pinned in `desktop/update`. Updater assets are zip archives containing `AgentDesk.Updater.exe`, matching the extraction contract used by the updater core. The PFX/private key is materialized only in the runner's temporary directory and removed when the workflow finishes. Certificates and keys must never be committed.

Every artifact directory contains `AgentDesk-<version>-win-<architecture>-MSIX-SIGNING-STATUS.txt` with either `signed` or `unsigned`. An unsigned MSIX is not an official signed release package.

Release packages include the root `LICENSE` and `THIRD-PARTY-NOTICES`, desktop dependency notices, English and Chinese source-availability notices, and `SOURCE-REVISION.txt` with the exact source repository and commit.

AgentDesk does not silently apply updates. Portable background availability checks are disabled by default and require an explicit Settings opt-in; they publish only a version notification after the pinned P-256 trust root verifies update metadata. Users must still explicitly apply an update, and MSIX reports the in-app path as unsupported. Starting with the second signed tag, CI downloads the immediately preceding published Windows assets, verifies both architecture SHA-256 lists and signed status, then publishes `AgentDesk-<current>-rollback-to-<previous>.zip` with bilingual manual instructions and its own `.sha256`. MSIX downgrade is manual because Windows will not install a lower package version over a higher one.

## Project Layout

| Path | Responsibility |
| --- | --- |
| `src/AgentDesk.App` | WinUI application, WebView2 host/bridge, Windows Automation, Cloud coordination, notifications, and native dialogs |
| `src/AgentDesk.Core` | UI-independent engine, execution, and security contracts |
| `src/AgentDesk.Engine` | ACP transport and native/WSL sidecar lifecycle |
| `src/AgentDesk.Platform.Windows` | Credential Manager, SQLite, settings, backup, and Windows integration |
| `src/AgentDesk.Cloud.Client` | Encrypted self-hosted Cloud client and recovery-key workflows |
| `src/AgentDesk.Updater.Core`, `src/AgentDesk.Updater` | Signed Portable update verification and replacement |
| `tests` | .NET unit and integration tests |
| `web` | React workbench, inspector, Monaco, and xterm.js surfaces |
| `../scripts/agentdesk` | Packaging, signing, WSL installation, and validation scripts |

## License And Source

See the root [license and provenance section](../README.md#license-and-provenance), [desktop dependency notices](THIRD-PARTY-NOTICES.md), and [third-party source-availability notice](THIRD-PARTY-SOURCE-NOTICE.md). Security-sensitive findings must follow the root [Security Policy](../SECURITY.md). The separate [Cloud developer preview](../cloud/README.md) is optional, disabled by default, and used only after explicit remote-profile setup.
