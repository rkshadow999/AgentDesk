# Install AgentDesk On Windows 11

[English](INSTALLATION.md) | [简体中文](INSTALLATION.zh-CN.md)

> [!WARNING]
> AgentDesk is Alpha software. It can edit files and run commands with your Windows account. `NativeProtected` is **Native compatibility (not sandboxed)**. `WslStrict` is currently blocked and fails closed.

## Choose A Package

| Package | Intended use | Trust status |
| --- | --- | --- |
| Signed MSIX from a `v*` GitHub Release | Normal installation after a signed release exists | Verify the signer and SHA-256 before installing |
| Portable zip from the same release | Isolated/manual installation | Verify SHA-256; files run with the current user authority |
| Unsigned MSIX from a branch or pull request | CI and development only | Not an official release; do not distribute as one |
| Source build | Development and audit | You are responsible for the build host and resulting binaries |

The project does not publish an installer through an upstream xAI or OpenAI channel. The public source repository is [rkshadow999/AgentDesk](https://github.com/rkshadow999/AgentDesk), but no independently verified signed GitHub Release or signed public MSIX is claimed yet. Until such a release exists, use a source build or an explicitly labeled unsigned development preview and do not treat CI or local artifacts as a stable release. AgentDesk is also not a distribution of the official Codex application and does not provide equivalence with its private account, quota, connector, hosted execution, or model-service features.

## Requirements

- Windows 11 matching the package architecture. x64 is the current local verification target; do not treat ARM64 configuration as real-device evidence without a successful ARM64 release result.
- Microsoft Edge WebView2 Runtime. Supported Windows 11 installations normally include it.
- An API key for xAI or an explicitly configured OpenAI-compatible provider.
- A workspace you trust when using native execution.

Building from source additionally requires the pinned toolchain in [Build and Test](BUILD-AND-TEST.md), including Visual Studio 2022 C++ Build Tools/MSVC v143, the Windows 11 SDK, .NET SDK 10.0.302 from `global.json`, Rust 1.92.0 from `rust-toolchain.toml`, and `protoc` 29.3.

WSL2 is not required for the current native workflow. When preparing the bundled Linux payload, AgentDesk ignores Docker Desktop's internal distributions and requires either exactly one installed non-Docker distribution or an explicit `-DistributionName` / `AGENTDESK_WSL_DISTRIBUTION` selection. The installer writes `/usr/local/bin/agentdesk-engine`; desktop availability requires that file to be executable, architecture-compatible, and SHA-256-identical to the bundled payload. Installing the payload still does not make `WslStrict` usable while its health attestation reports incomplete child-process network enforcement.

## Verify A Release

Download the package and the matching `AgentDesk-<version>-win-<architecture>-SHA256SUMS.txt` from the same GitHub Release. In PowerShell:

```powershell
$package = "AgentDesk-0.1.0-alpha.2-win-x64-portable.zip"
$expected = (Select-String `
  -Path "AgentDesk-0.1.0-alpha.2-win-x64-SHA256SUMS.txt" `
  -Pattern ([regex]::Escape($package))).Line.Split(' ')[0]
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $package).Hash.ToLowerInvariant()
if ($actual -cne $expected) { throw "AgentDesk package checksum mismatch." }
```

For MSIX, also inspect the signer:

```powershell
Get-AuthenticodeSignature -LiteralPath .\AgentDesk-0.1.0-alpha.2-win-x64.msix |
  Format-List Status,StatusMessage,@{Name='Signer';Expression={$_.SignerCertificate.Subject}}
```

The status must be `Valid`, and both the signer subject and packaged manifest Publisher must be `CN=AgentDesk`. Maintainers with Windows SDK tools can run `scripts/agentdesk/Verify-AgentDeskMsixSignature.ps1`; release automation may also supply the repository-pinned certificate thumbprint.

Each release also provides SPDX and CycloneDX SBOM companions. Installed package content includes `LICENSE`, repository and desktop third-party notices, source-availability notices, and `SOURCE-REVISION.txt`. Stop if the revision, architecture, signer, or checksum does not match.

## Install The Signed MSIX

1. Verify the MSIX as described above.
2. Open the `.msix` with Windows App Installer and review the publisher.
3. Select **Install**.
4. Start AgentDesk from the Start menu.

Do not import or trust a development certificate merely to make an unknown MSIX install. Branch and pull-request MSIX files are deliberately unsigned and are for controlled development validation only.

## Run The Portable Package

1. Verify the Portable zip.
2. Extract it to a new directory owned by your Windows user.
3. Start `AgentDesk.App.exe` from that directory.
4. Keep `agentdesk-engine.exe`, the Web assets, runtime files, and legal notices together.

Do not run directly from inside the zip or merge a new version over a running Portable directory.

## First Run

1. Select a trusted workspace.
2. Open provider settings and confirm the Base URL, model, and API backend (`chat_completions` or `responses`).
3. Enter the API key. The key is stored in Windows Credential Manager and is bound to the configured Base URL. Changing the Base URL requires entering the key again.
4. Prefer HTTPS. AgentDesk blocks sending a credential to plaintext HTTP unless the insecure-transport option is explicitly enabled. That option exposes the key and task content to anyone able to observe or alter the connection.
5. Review the native-execution warning and every permission request before approval.

Simplified Chinese is the default. English can be selected in Settings; Web labels change immediately and native WinUI text changes after restart. Image prompts are enabled only when the engine advertises the capability. Extension and Marketplace actions execute with the same user authority as the sidecar, so inspect their source and confirmation dialogs; a signature or catalog entry is not a sandbox. In a remote Cloud profile, AgentDesk blocks every Plugin mutation and Marketplace install/update/uninstall action that can rebuild or reload the registry rather than trusting a publisher ID supplied by the Web UI. Read-only catalog list/refresh remains available.

Worktree create/apply/remove/GC actions can change Git state and files. Review the selected source/destination, use dry-run where offered, and keep backups before overwrite or cleanup operations. Windows notifications are disabled by default and contain only generic task status; notification activation resolves a session by ID rather than carrying a workspace path.

The optional Cloud profile also defaults to local-only. Remote setup requires an explicit HTTPS endpoint (HTTP is accepted only for loopback development), team/device identifiers, a native access-token prompt, and recovery-key pairing for another device. Pairing import/export uses a passphrase-protected `.agentdesk-pairing` file selected through a native dialog; the host rejects oversized files, alternate data streams, device paths/names, reparse points, and changed final paths, and writes through an atomic replacement. Keep both the package and its passphrase private.

Enabling Cloud can upload a client-encrypted session document and routing metadata to the configured server. The connected developer-preview controls can register a Runner, queue/claim/complete encrypted task bodies, and create/list/disable encrypted automations. This is not a production Runner, remote sandbox, or background push service, and it is not required for local use.

The desktop sends the credential to the sidecar over its private redirected stdio channel after process launch; it does not place the key in the sidecar's initial environment. The selected model provider still receives prompts and any tool context sent by the engine.

## Experimental Windows Automation Status

The host-side Windows UI Automation executor is disabled by default. Enabling its local preference is necessary but not sufficient: a connected remote team policy can still deny it, and every execution requires an explicit **Allow once** permission. The current executor accepts a process ID and supports only focus-window, invoke, and set-value using an Automation ID and/or accessible name. Entered set-value text is not echoed in permission status or completion events.

Settings provides the complete bounded control surface for the three supported operations, but the current package must not be described as a general Computer Use feature. Calls use the current Windows user's UI access; the executor is not a sandbox, does not isolate the target process, and does not provide autonomous target discovery. Stronger in-flight interruption and packaged real-target testing remain release gates.

## Upgrade And Roll Back

Portable background update checks are disabled by default. After the user explicitly opts in, Portable builds run periodic signed update checks and may stage a verified update for notification; they never apply it or restart AgentDesk automatically. The user can also start **Check for updates** manually. Both paths stage the standalone updater only after verifying pinned-key signatures for the updater and application manifests. MSIX builds do not run this Portable monitor, report in-app update as unsupported, and continue to use Windows package installation.

- MSIX upgrades use Windows package version ordering and require the same trusted publisher identity.
- A trusted Portable release can use the in-app check/apply flow. The external updater waits for AgentDesk to exit, validates version/hash/signature/path constraints, replaces the Portable directory, and restarts the application. If no signed AgentDesk release metadata exists, this flow must fail rather than trust an unsigned artifact.
- Manual Portable upgrades should be extracted to a new directory. Keep the previous directory until the new version has been verified.
- Back up `%LOCALAPPDATA%\AgentDesk` before changing versions. It contains UI settings and the local session index; engine-owned session data may live separately under the runtime's data directory.

Starting with the second signed release, each tag publishes `AgentDesk-<current>-rollback-to-<previous>.zip` and a `.sha256` companion. The bundle contains the previous signed x64 and ARM64 MSIX/Portable assets, their original checksums and SBOMs, a machine-readable manifest, and bilingual instructions. Verify the rollback archive before use. Windows does not install a lower MSIX version over a higher one, so manual MSIX rollback requires uninstalling the current package and then installing the previous signed package.

The first release publishes a marker explaining that no earlier signed release exists to bundle.

## Remove AgentDesk

- MSIX: remove AgentDesk from **Settings > Apps > Installed apps**.
- Portable: close AgentDesk, confirm its sidecar has exited, then remove the extracted program directory.

Uninstalling the program does not promise deletion of `%LOCALAPPDATA%\AgentDesk`, engine sessions, worktrees, workspace changes, or Credential Manager entries. Review and remove retained data separately only after backing up anything you need.

The optional self-hosted Cloud server is not part of desktop installation. The desktop client remains local-only until a user explicitly configures it; see [cloud/README.md](../cloud/README.md) before connecting to the developer preview.
