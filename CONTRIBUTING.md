# Contributing To AgentDesk

[English](CONTRIBUTING.md) | [简体中文](CONTRIBUTING.zh-CN.md)

AgentDesk is an independent community project. Issues, documentation improvements, focused fixes, and pull requests are welcome. Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md).

## Choose The Right Channel

- Use [GitHub Issues](https://github.com/rkshadow999/AgentDesk/issues) for reproducible bugs, scoped feature proposals, documentation gaps, and accessibility problems.
- Use [GitHub Pull Requests](https://github.com/rkshadow999/AgentDesk/pulls) for reviewable changes tied to a clear problem.
- Follow the [Security Policy](SECURITY.md) for vulnerabilities, exposed credentials, sandbox or permission-boundary bypasses, and other sensitive reports. Never disclose these in a public issue.

Search existing issues and pull requests before opening a new one. For a behavior change that spans the desktop host, Web UI, Rust sidecar, protocol, or packaging, open an issue first so the contract and test scope can be agreed before implementation.

## Development Setup

The supported desktop build uses Windows 11, PowerShell 7, Node.js 24, .NET SDK 10.0.302, and Rust 1.92. Build the Web assets before building or testing the WinUI application:

```powershell
Set-Location desktop/web
npm ci
npm test
npm run build
Set-Location ../..

dotnet test desktop/AgentDesk.sln --no-restore
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
```

See [Build and Test](docs/BUILD-AND-TEST.md) for exact component, cloud, package, and architecture-specific commands.

## Make A Focused Change

- Keep each pull request limited to one coherent problem. Separate upstream synchronization, dependency upgrades, formatting, and feature work.
- Follow existing boundaries: the WinUI host owns Windows integration, shared .NET contracts remain UI-independent, and the Rust runtime communicates through ACP/NDJSON stdio.
- Preserve all upstream copyright, license, source-availability, and modification notices. Follow the [upstream synchronization policy](docs/UPSTREAM.md) when editing inherited files.
- Do not weaken permission prompts, the native-execution warning, sandbox attestation, or fail-closed behavior without an explicit security design and regression tests.
- Do not commit credentials, private source, user prompts, local paths, generated build directories, package outputs, screenshots from local testing, or unrelated binary churn.
- Commit lockfile or generated-file changes only when they are a direct and reproducible result of the proposed change. Explain intentional binary asset updates and retain their editable source when available.
- Avoid repository-wide formatting. Format only the packages or files in scope.

## Test The Change

Add the narrowest regression test that proves the new behavior, then run the focused test and the relevant broader suite. Typical checks are:

```powershell
# Web surfaces
Set-Location desktop/web
npm test
npm run build
Set-Location ../..

# .NET host and libraries
dotnet test desktop/AgentDesk.sln --no-restore
dotnet format desktop/AgentDesk.sln --verify-no-changes --no-restore

# AgentDesk Rust contract and formatting
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
cargo fmt --package xai-grok-shell -- --check

# Public repository and release contracts
pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1
pwsh ./scripts/agentdesk/Test-AgentDeskRollbackBundle.ps1
pwsh ./scripts/agentdesk/Test-AgentDeskReleaseScripts.ps1
git diff --check
```

Run additional package-specific tests for every component you change. If a check requires an unavailable architecture, signing certificate, Linux enforcement environment, or external service, state exactly what was not run and rely on the matching CI job before merge.

## Pull Request Requirements

A pull request should include:

- The user-visible or technical problem and the chosen scope.
- A concise implementation summary and any security or compatibility tradeoffs.
- Tests added and the exact verification commands run.
- Screenshots only when a visible UI changed, with secrets and private paths removed.
- Documentation updates for changed commands, protocols, safety guarantees, or user workflows.
- Synchronized English and Simplified Chinese updates for AgentDesk-owned public documentation.
- A clear note for dependency, generated, binary, license, or upstream-source changes.

Maintainers may ask for a smaller change, additional tests, accessibility fixes, bilingual documentation updates, or a clean separation from upstream synchronization before merging.

## Licensing

By submitting a contribution, you represent that you have the right to submit it and agree that it will be distributed under the licenses that apply to the affected files. First-party AgentDesk and inherited first-party source is generally Apache-2.0; third-party and vendored files retain their original licenses. Do not replace or remove applicable license notices.
