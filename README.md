# AgentDesk

[English](README.md) | [简体中文](README.zh-CN.md)

> [!WARNING]
> AgentDesk is Alpha software that can read files, edit files, and run commands. The current desktop UI exposes only **Native compatibility (not sandboxed, `NativeProtected`)**, which runs with the current Windows user's file and network permissions. **WSL2 strict mode (`WslStrict`) intentionally fails closed** until complete child-process network enforcement can be attested. Use AgentDesk only with workspaces you trust.

AgentDesk is an independent, community-maintained Windows 11 desktop client built on the open-source `xai-org/grok-build` runtime at commit [`c68e39f`](https://github.com/xai-org/grok-build/commit/c68e39f). It is not affiliated with or endorsed by xAI, SpaceXAI, OpenAI, or Codex. It implements community-owned local workflows; it does not reproduce or claim equivalence with Codex's private account, quota, connector, hosted execution, or model-service capabilities.

![AgentDesk Alpha workbench](docs/images/agentdesk-alpha.png)

## Highlights

- Native Windows 11 shell built with .NET 10, WinUI 3, and WebView2.
- Rust agent runtime isolated as an ACP/NDJSON stdio sidecar rather than linked through private runtime APIs.
- Simplified Chinese-first desktop UI with an English selector, workspace selection, task cancellation, Plan Mode, image attachments, and a searchable session center. Web labels switch immediately; native strings switch after restart.
- API credentials stored through Windows Credential Manager and removed from the sidecar's inherited environment.
- xAI and custom OpenAI-compatible Base URL/model/backend settings, including Responses API selection, with HTTPS by default and an explicit plaintext HTTP risk opt-in.
- Explicit permission requests, a visible native-execution risk gate, and process-tree cleanup.
- Changes, terminal output, and plan inspection using Monaco and xterm.js surfaces; session fork, compact, rewind, rename, export/import, and reversible local archive operations.
- An active-session Runtime Dashboard plus worktree create/list/inspect/apply/remove/GC workflows. Code review is a two-step flow: prepare and edit the review request, then explicitly start it through the standard prompt and permission path.
- Bounded workspace file references, an `AGENTS.md` editor, and a capability-gated Memory browser with a 64 KiB UTF-8 limit and host-authoritative two-stage confirmation for writes and deletes.
- Settings for MCP servers, Skills, Hooks, Plugins, and policy-gated marketplace actions; secret values stay outside WebView2 messages. In a remote Cloud profile, every Plugin or Marketplace action that can load code fails closed, and client-supplied publisher claims are not trusted.
- Opt-in Windows notifications, local backup/restore, and a signature-verified Portable updater. Portable background availability checks are disabled by default and run periodically only after the user opts in; applying an update remains explicit, and MSIX updates remain external/manual.
- A host-side experimental Windows UI Automation surface for focus-window, invoke, and set-value actions. Settings exposes the bounded controls, while local/team policy and a per-action allow-once permission gate every request; entered values are not echoed into status or completion events.
- Hardened recovery-key pairing package import/export using native dialogs, bounded files, reparse/final-path validation, Windows device-name rejection, and atomic replacement.
- An optional encrypted self-hosted Cloud client for session sync, device handoff, team policy, Runner registration, encrypted task queue/claim/complete, automation create/list/disable, and authenticated SignalR change notifications. `local-only` mode remains the default; the developer-preview server has a 37-test suite and a separate real-process offline backup/restore/rollback E2E job.
- x64 and ARM64 CI definitions for Portable and MSIX packaging, signing gates, SBOMs, checksums, and previous-release rollback-bundle verification.

## Alpha Status And Safety

The current source tree provides the local desktop workflow: select a workspace, configure xAI or an OpenAI-compatible endpoint, store its key, start or resume an ACP session, use Execute or Plan Mode, review permission requests, inspect changes/output, manage session history, and stop the task. It is not yet a stable or security-hardened release, and a workflow definition is not evidence that a signed public package has been released.

The Alpha interface defaults to Simplified Chinese and provides an English selector. React/WebView2 labels switch immediately; WinUI resource selection is applied on the next application start. Automated resource and bridge tests exist, while manual Chinese IME, Narrator, high-contrast, keyboard-only, and scaling verification remains a release gate.

Recorded local source-tree evidence is maintained in [Build and Test](docs/BUILD-AND-TEST.md) and is regenerated before publication. The public source repository is [rkshadow999/AgentDesk](https://github.com/rkshadow999/AgentDesk). Local verification is not evidence of a signed public MSIX, an ARM64 real-device launch, a usable `WslStrict` profile in a real WSL2 environment, or production Cloud/Runner readiness. Any MSIX built locally without the release certificate is an unsigned development artifact only.

`NativeProtected` is retained as a protocol name for compatibility. In the UI it is labeled **Native compatibility (not sandboxed)**. It separates AgentDesk application data, removes inherited credential variables, keeps permission approval in the desktop host, and terminates the sidecar process tree. It still executes with the current Windows user's authority and does not confine filesystem or network access.

`WslStrict` requires the engine to attest an active strict sandbox and child-network restriction before authentication or session creation. The engine cannot yet prove that every helper, plugin, hook, PTY, and command launch path has the same child-network restriction, so it reports the attestation as incomplete. The desktop stops the sidecar instead of downgrading or continuing. See the [desktop safety details](desktop/README.md#execution-profiles).

## Configured Systems

| Component | Supported targets |
| --- | --- |
| Desktop client | Windows 11 x64; ARM64 project and CI configuration exists but still requires successful CI and real-device evidence |
| Native sidecar | Windows x64; ARM64 is a configured build target, not a locally verified claim |
| Bundled WSL payload | Linux x64 or ARM64 matched to the Windows package; strict execution remains blocked on both |
| Source build | PowerShell 7, Git, Node.js 24, .NET SDK 10.0.302 from `global.json`, Rust 1.92.0 from `rust-toolchain.toml`, pinned `protoc` 29.3, Visual Studio 2022 Build Tools with the Desktop development with C++ workload/MSVC v143, and the Windows 11 SDK 10.0.26100 |

Windows 10 build metadata remains in the project for framework compatibility, but the AgentDesk community currently tests and supports Windows 11 only.

## Build From Source

There is no upstream or official AgentDesk installation command. Until a signed AgentDesk GitHub Release exists, clone this repository and build the desktop client from source:

```powershell
$env:PROTOC = ./scripts/agentdesk/Install-Protoc.ps1 `
  -Version 29.3 `
  -Destination "$env:TEMP/agentdesk-protoc"

Set-Location desktop/web
npm ci
npm test
npm run build
Set-Location ../..

cargo build --locked -p xai-grok-pager-bin --profile release-dist --features release-dist

./scripts/agentdesk/Build-AgentDeskPackage.ps1 `
  -Architecture x64 `
  -Mode Portable `
  -NativeEnginePath ./target/release-dist/xai-grok-pager.exe `
  -OutputRoot ./artifacts/agentdesk
```

The exact packaging entry point is [`scripts/agentdesk/Build-AgentDeskPackage.ps1`](scripts/agentdesk/Build-AgentDeskPackage.ps1). Replace `x64` with `arm64` only on an ARM64 build host, and do not treat configuration alone as ARM64 verification. See [Installation](docs/INSTALLATION.md) and [Build and Test](docs/BUILD-AND-TEST.md) before using or distributing an artifact.

## Repository Layout

| Path | Purpose |
| --- | --- |
| `desktop/src` | WinUI host, shared contracts, ACP client, and Windows platform services |
| `desktop/web` | React workbench and inspector surfaces |
| `desktop/tests` | .NET host, engine, core, and platform tests |
| `crates/codegen/xai-grok-shell` | Upstream Rust runtime plus AgentDesk ACP extensions |
| `scripts/agentdesk` | Build, packaging, verification, and release scripts |
| `cloud` | Optional self-hosted server developer preview used by the opt-in desktop Cloud client |
| `docs` | Installation, architecture, threat model, build/test, roadmap, provenance, designs, and plans |

The inherited Rust tree and its original documentation remain available for runtime development. AgentDesk-maintained public documents are bilingual; inherited upstream changelogs, prompts, skills, fixtures, license texts, and Rust guides remain in their original language so they can stay synchronized with upstream.

## Project Direction

The local workbench, bilingual selector, image prompts, session/history maintenance, worktrees, Runtime Dashboard, extension management, notifications, backup/restore, Portable update flow, experimental Windows UI Automation surface, and opt-in encrypted Cloud workflows are connected in source. The public repository is live, but release evidence remains incomplete: no signed public package is claimed, ARM64 has not been verified on real hardware here, `WslStrict` remains blocked, Portable background checks run periodically only after explicit opt-in and never apply an update automatically, Windows UI Automation has no isolation or broad Computer Use guarantee, and the Cloud service and Runner workflow are not production-hardened.

Read the evidence-based [roadmap](docs/ROADMAP.md) for per-capability status. Upstream updates follow the [synchronization policy](docs/UPSTREAM.md).

## Documentation

- [Install, verify, upgrade, and manually roll back](docs/INSTALLATION.md)
- [Desktop/sidecar/data ownership architecture](docs/ARCHITECTURE.md)
- [Repository-grounded threat model](docs/AGENTDESK-THREAT-MODEL.md)
- [Build, test, package, and CI evidence](docs/BUILD-AND-TEST.md)
- [Maintainer release checklist](docs/RELEASING.md)
- [Delivery status and remaining gates](docs/ROADMAP.md)

## Community

Issues and focused pull requests are welcome. Read [Contributing](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md) before participating. Report vulnerabilities privately through the process in the [Security Policy](SECURITY.md); do not open a public issue for an undisclosed vulnerability.

## License And Provenance

AgentDesk retains the complete upstream history from `xai-org/grok-build`; the initial AgentDesk base is commit `c68e39f`. First-party source remains under the [Apache License 2.0](LICENSE). Third-party and vendored source remains under its respective licenses.

- [Upstream provenance and synchronization](docs/UPSTREAM.md)
- [Repository-wide third-party notices](THIRD-PARTY-NOTICES)
- [Desktop dependency notices](desktop/THIRD-PARTY-NOTICES.md)
- [Third-party source availability](desktop/THIRD-PARTY-SOURCE-NOTICE.md)

These license and source notices grant no rights to third-party trademarks. AgentDesk uses its own project identity and does not present itself as an official distribution of any upstream product.
