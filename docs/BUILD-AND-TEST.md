# Build And Test AgentDesk

[English](BUILD-AND-TEST.md) | [简体中文](BUILD-AND-TEST.zh-CN.md)

## Supported Development Environment

The desktop build targets Windows 11 x64 and ARM64 with:

- PowerShell 7 and Git.
- Node.js 24 and npm.
- Visual Studio 2022 Build Tools with the **Desktop development with C++** workload and the MSVC v143 toolset. Install the matching C++ ARM64 build-tools component when targeting ARM64. The Rust Windows target uses the MSVC linker; the .NET SDK alone is insufficient.
- Windows 11 SDK 10.0.26100, including the native libraries and packaging/signing tools used by the Windows build. The `Microsoft.Windows.SDK.BuildTools` package does not replace the local MSVC toolchain required by Rust.
- .NET SDK 10.0.302, selected by `global.json`.
- Rust 1.92.0, selected by `rust-toolchain.toml`.
- The repository-pinned protobuf compiler 29.3 for Rust packages that generate protobuf code. Use `scripts/agentdesk/Install-Protoc.ps1`; it verifies the supported archive checksum before exposing `protoc`.

Run Windows builds from a Visual Studio 2022 Developer PowerShell, or another PowerShell session in which the matching MSVC v143 and Windows SDK environment is available. Verify the selected versions with `dotnet --version`, `rustc --version`, `& $env:PROTOC --version`, and `where.exe link` before diagnosing build failures.

Use the architecture-matched native Rust sidecar. A Windows x64 executable cannot be shipped in an ARM64 package, and a Windows executable cannot be used as the WSL payload. ARM64 project/CI configuration is not evidence of a successful ARM64 build or real-device launch until those jobs and manual checks have run for the exact commit.

## Web Workbench

```powershell
Set-Location desktop/web
npm ci
npm test
npm run build
Set-Location ../..
```

`npm run build` produces release Web assets consumed by the WinUI project. Do not commit `node_modules` or `desktop/web/dist`.

## Desktop .NET Suites

After the Web build:

```powershell
dotnet restore .\desktop\AgentDesk.sln
dotnet test .\desktop\AgentDesk.sln --configuration Release -m:1
dotnet format .\desktop\AgentDesk.sln --verify-no-changes --no-restore
```

The solution covers Core contracts, ACP/sidecar behavior, Windows storage, host state, provider validation, permissions, sessions/history, worktrees, extensions, notifications, backup/restore, Portable updates, encrypted Cloud workflows, Windows Automation policy/coordinator/protocol behavior, hardened pairing-package file I/O, remote Plugin/Marketplace fail-closed policy, shutdown, and Web asset discovery. It also includes a real-Kestrel desktop/Cloud integration project and unit tests for the sanitized live-provider smoke tool. Run the solution sequentially because CI intentionally avoids cross-testhost contention in time-sensitive sidecar lifecycle tests. These are unit and component/integration tests; they are not evidence that the packaged FlaUI executor has controlled a real target application, or that Narrator, IME, high-contrast, scaling, and a real packaged desktop flow have been exercised.

To build the WinUI application for the current host:

```powershell
$platform = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }
dotnet build .\desktop\src\AgentDesk.App\AgentDesk.App.csproj `
  --configuration Release `
  -p:Platform=$platform `
  -p:PackageMode=Portable `
  -p:WindowsPackageType=None
```

## Rust Sidecar

Install the pinned compiler when `protoc` is not already available:

```powershell
$env:PROTOC = .\scripts\agentdesk\Install-Protoc.ps1 `
  -Version 29.3 `
  -Destination "$env:TEMP\agentdesk-protoc"
```

Run the AgentDesk-focused formatting and contract checks:

```powershell
cargo fmt `
  --package xai-proto-build `
  --package xai-grok-pager-bin `
  --package xai-grok-sandbox `
  --package xai-grok-shell `
  -- --check

cargo test --locked -p xai-proto-build
cargo test --locked -p xai-grok-memory
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
cargo test --locked -p xai-grok-shell --test agentdesk_session_transfer -- --test-threads=1
cargo check --locked -p xai-grok-pager --lib
cargo build --locked -p xai-grok-pager-bin --profile release-dist --features release-dist
```

The built Windows engine is `target/release-dist/xai-grok-pager.exe`. Windows MSVC builds reserve an 8 MiB main-thread stack. `Test-AgentDeskEngineArchitecture.ps1` fails closed when the PE architecture is wrong, the optional header is invalid, or `SizeOfStackReserve` is below 8 MiB; the package builder runs this verifier automatically. The ignored native lifecycle test requires that binary:

```powershell
$env:GROK_BINARY = (Resolve-Path .\target\release-dist\xai-grok-pager.exe).Path
cargo test --locked `
  -p xai-grok-shell `
  --test test_built_binary_e2e `
  test_windows_agentdesk_stdio_lifecycle_and_clean_shutdown `
  -- --ignored --exact --test-threads=1
```

Linux CI additionally sets `SANDBOX_E2E_REQUIRE_ENFORCEMENT=1` for `xai-grok-sandbox`. Do not claim strict sandbox enforcement from a Windows-only or skipped test.

## Cloud Server And Desktop Client

```powershell
dotnet restore .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj
dotnet format .\cloud\src\AgentDesk.Cloud\AgentDesk.Cloud.csproj `
  --verify-no-changes --no-restore
dotnet format .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj `
  --verify-no-changes --no-restore
dotnet test .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj `
  --configuration Release

pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskCloudDatabaseMaintenance.ps1
```

The server suite runs the ASP.NET Core application in process with an isolated SQLite database and tests authentication/roles, opaque revisioned sync, Runner leases, queue/claim/complete, policy, handoffs, plugin signatures, automation creation/dispatch, and authenticated SignalR negotiation. Desktop solution tests additionally exercise Credential Manager-backed token/recovery-key adapters, AES-GCM metadata binding, rollback detection, hardened pairing file handling, encrypted Runner task/result bodies, automation create/list/disable, engine session export/import, and a real Kestrel round trip that verifies plaintext is not stored by the server. The maintenance E2E starts a real Cloud process and covers service-lease refusal, validated offline backup, exact-byte restore, rollback evidence, and automatic rollback after failed post-install validation. These tests do not prove production TLS, online or multi-instance recovery, production token/key operations, a production Runner, or packaged multi-device behavior.

## Repository And Release Contracts

```powershell
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskRollbackBundle.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskUpdateManifest.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskReleaseScripts.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskPublicRepository.ps1
git diff --check
```

The public-repository test requires bilingual documents, secret ignore rules, GitHub community files, upstream provenance, and a leading AgentDesk modification notice on every inherited Rust file changed from `c68e39f`. The release tests cover version mapping, architecture rejection, dependency notice closure, explicit non-Docker WSL distribution selection, MSIX/update signing gates, Cloud server and maintenance CI, update-manifest trust, and rollback hash/path/signature failures.

## Package Inputs

Build Web assets and the native sidecar first, then:

```powershell
.\scripts\agentdesk\Build-AgentDeskPackage.ps1 `
  -Architecture x64 `
  -Mode All `
  -Version 0.1.0-alpha.2 `
  -NativeEnginePath .\target\release-dist\xai-grok-pager.exe `
  -OutputRoot .\artifacts\agentdesk `
  -SourceRepository https://github.com/rkshadow999/AgentDesk `
  -SourceRevision (git rev-parse HEAD)
```

Use `arm64` only with an ARM64 engine and matching build host, then record successful CI and real ARM64 launch evidence before making a support claim. Add `-WslEnginePath` only for an architecture-matched 64-bit little-endian Linux ELF payload. `Test-AgentDeskLinuxEngineArchitecture.ps1` runs automatically during packaging and fails closed on a malformed or mismatched x64/ARM64 payload. The accepted payload is packaged for future strict-mode work, but the current desktop health gate still blocks `WslStrict`.

Without `-CertificatePath` and `-CertificatePassword`, MSIX output is unsigned and suitable only for development. It is not a release package and must not be distributed as one. The package input also contains a separate single-file Portable updater; only signed tag metadata may establish its trust channel. Official tag packaging is performed by CI, requires MSIX and update-signing secrets, verifies both trust paths, generates SPDX/CycloneDX SBOMs, final archives and SHA-256 files, and publishes a verified previous-release rollback bundle.

The package script supports a no-write validation path:

```powershell
.\scripts\agentdesk\Build-AgentDeskPackage.ps1 `
  -Architecture x64 `
  -Mode All `
  -NativeEnginePath .\missing-engine.exe `
  -OutputRoot .\artifacts\dry-run `
  -DryRun
```

## CI Evidence

| Job | Platform | Evidence produced |
| --- | --- | --- |
| `linux-sidecar` | Ubuntu 22.04 x64/ARM64 | Rust contract and enforced sandbox tests, real strict sidecar health fail-closed probe, ELF/GLIBC check, Linux SBOM and checksum |
| `cloud-tests` | Ubuntu 24.04 | Release-configuration Cloud server integration suite |
| `cloud-maintenance` | Windows 2025 | Real-process offline database backup/restore/rollback E2E, including lease refusal and failed-restore automatic rollback |
| `windows-build` | Windows 2025 x64 / Windows 11 ARM | Web tests/build, WebView2 CDP helper tests on both architectures, every desktop test project (including Cloud client/updater/provider-smoke tests), Rust contract/lifecycle, native and WSL package inputs, signing gates, dependency closure, and package-input upload |
| `interactive-gui-smoke` | Opt-in interactive self-hosted Windows x64 | Downloads the packaged x64 Portable input and launches both WebView2 surfaces through the bounded CDP/Job Object harness |
| `assemble-release` | Ubuntu 24.04 | Portable zip, standalone updater, MSIX, SPDX/CycloneDX companions, signing status, update manifests/signatures, SHA-256 list |
| `github-release` | Windows 2025, tag only | Current/previous MSIX Authenticode/publisher re-verification, signed artifact publication, and rollback bundle |

GitHub-hosted Windows sessions do not provide a reliable interactive GUI boundary, so they run the CDP helper tests and all non-GUI package gates but do not launch the packaged WinUI application. The public repository must never use a persistent self-hosted runner or an ordinary developer workstation for this gap. To enable the real packaged GUI gate, provision a clean disposable x64 VM from a trusted snapshot for one reviewed non-PR run, start a JIT/ephemeral runner in the logged-in foreground interactive session, and label it `self-hosted`, `Windows`, `X64`, and `agentdesk-interactive`. Run it under a dedicated non-administrator local account with no personal or signing credentials, SSH agents, network shares, reusable disks, or access to internal networks; destroy the VM and runner registration after the job. Do not install it as a Windows service. Keep `AGENTDESK_RUN_INTERACTIVE_GUI_SMOKE` false unless all of these controls are in place.

The checked-in job excludes `pull_request` events as defense in depth, but a modifiable workflow condition and custom label are not a security boundary for a public repository. Review queued jobs before registering the disposable runner and use a runner group restricted to this workflow where the GitHub plan supports it. When the repository variable is enabled, a failure blocks Tag publication; when disabled, the skipped optional job does not block the hosted package pipeline.

CI configuration is evidence only after it runs successfully for the exact commit. A hosted helper result or interactive x64 run is not ARM64 real-device evidence, and an unsigned branch artifact is not a signed release.

## Recorded Local Evidence

The latest recorded Windows x64 component verification on 2026-07-19 includes:

| Check | Recorded result | Boundary |
| --- | --- | --- |
| Web surfaces | `245/245` passed; `npm run build` passed | React/WebView2 components and release asset build; not a packaged WinUI run |
| Core, engine, App, and Windows platform suites | `37/37`, `215/215`, `627/627`, and `53/53` passed | Source-level contracts, host projection, and Windows services; not a packaged UI run |
| Cloud client and real-Kestrel integration | `116/116` and `3/3` passed | Encrypted client behavior and loopback server integration; not production Cloud evidence |
| Updater core, Provider smoke, and Process Job launcher suites | `114/114`, `8/8`, and `1/1` passed | Updater/provider/process-containment tooling; not a signed update channel, live-provider result, or packaged process-escape proof |
| Cloud server | `37/37` passed | In-process developer-preview server; not production deployment evidence |
| Cloud database maintenance E2E | Passed | Real-process offline backup, exact-byte restore, rollback evidence, and failed-restore automatic rollback |
| Rust proto, ACP contract, focused Marketplace regression, Memory, and session-transfer suites | `4/4`, `12/12`, `2/2`, `292/292`, and `3/3` passed | The ACP contract result uses the required `--test-threads=1`; the focused Marketplace filter is part of that contract and must not be added to a synthetic total |
| Release contract and WebView2 CDP helpers | Passed; CDP helpers `15/15` | Verifies scripts and test helpers, not GitHub publication or packaged manual UX |
| Final sidecar and authorized live provider | x64 SHA-256 `1DB8E791EEB84FF75581374916403BC084F8D44CD58BD1795636AFFEE9C906A8`; 8 MiB PE stack; `/models` HTTP 200 in 4,250 ms; Chat Completions start 3,609 ms, handshake 749 ms, prompt 45,836 ms with 15 streamed events, cancellation 15,654 ms with `Cancelled`; invalid-model failure handled as `JsonRpcException`; no credential in diagnostics | The supplied compatibility endpoint used explicitly authorized plaintext HTTP. Timings are provider/network-specific, no response body was retained, and this is not a general performance or transport-security claim |

The Release desktop solution completed with exit code 0, and its application build completed with zero warnings and zero errors. Do not derive or publish a synthetic grand total by adding suites with different scopes. The ACP contract must be run with `--test-threads=1`; its tests intentionally share a process-level `OnceLock`, so a default-parallel result is not release evidence. The post-notice release binary rebuild and sanitized live-provider gates are complete. A separate authorized Responses-backend run also streamed events, acknowledged cancellation, shut down cleanly, and kept the credential out of diagnostics.

These results do not prove a signed public MSIX, successful GitHub push, real ARM64 hardware, a usable `WslStrict` profile in a real WSL2 environment, packaged accessibility, real-target Windows Automation, production Runner isolation, production Cloud operation, or equivalence with any official Codex private service. Re-run every applicable command for the final commit and package before release.

## Real-Service Testing Rules

- Use a local mock OpenAI-compatible server for deterministic 401, 429, cancellation, streaming, and tool-call cases.
- Never place a real API key in source, command history, test fixtures, screenshots, workflow logs, or issue reports.
- A manual live-provider smoke test must use HTTPS unless the tester knowingly accepts the plaintext transport risk. Report only sanitized status and timing, never request/response bodies.
- Before release, separately perform packaged x64/ARM64 smoke tests, a real-target Windows Automation allow/deny/cancel exercise, Chinese IME, keyboard-only, Narrator, high-contrast, 125%-200% scaling, crash recovery, and update/rollback checks. Those manual gates are not yet automated by the repository.

The smoke tool reads the key only from `GROK_THIRD_PARTY_API_KEY`, removes it from the child process environment before the sidecar starts, exercises handshake, a streamed prompt, cancellation, clean shutdown, and diagnostic secret scanning, and emits bounded JSON without response bodies. Set the key through a secret manager or parent process, not as a literal command. Example for an HTTPS Responses endpoint:

```powershell
if (-not $env:GROK_THIRD_PARTY_API_KEY) {
  throw "Set GROK_THIRD_PARTY_API_KEY through a process-scoped secret source first."
}

$env:AGENTDESK_REAL_PROVIDER_BASE_URL = "https://provider.example/v1"
$env:AGENTDESK_REAL_PROVIDER_MODEL = "provider-model-id"
$env:AGENTDESK_REAL_PROVIDER_BACKEND = "responses"
$env:AGENTDESK_REAL_PROVIDER_ALLOW_INSECURE_HTTP = "0"
$env:AGENTDESK_REAL_PROVIDER_ENGINE = (Resolve-Path `
  .\target\release-dist\xai-grok-pager.exe).Path
$env:AGENTDESK_REAL_PROVIDER_WORKSPACE = (Resolve-Path .).Path

try {
  dotnet run --project .\desktop\tools\AgentDesk.ProviderSmoke\AgentDesk.ProviderSmoke.csproj `
    --configuration Release
}
finally {
  @(
    "GROK_THIRD_PARTY_API_KEY",
    "AGENTDESK_REAL_PROVIDER_BASE_URL",
    "AGENTDESK_REAL_PROVIDER_MODEL",
    "AGENTDESK_REAL_PROVIDER_BACKEND",
    "AGENTDESK_REAL_PROVIDER_ALLOW_INSECURE_HTTP",
    "AGENTDESK_REAL_PROVIDER_ENGINE",
    "AGENTDESK_REAL_PROVIDER_WORKSPACE"
  ) | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
}
```

For an explicitly authorized plaintext HTTP test, set the Base URL to that test endpoint and set `AGENTDESK_REAL_PROVIDER_ALLOW_INSECURE_HTTP=1`. This opt-in only bypasses the transport refusal; it does not make HTTP confidential or tamper-resistant and must not be used for a normal release smoke.
