# Security Policy

[English](SECURITY.md) | [简体中文](SECURITY.zh-CN.md)

## Alpha Support Boundary

AgentDesk is Alpha software. Security fixes are made on the current `main` branch and, when releases exist, the latest supported release. Older snapshots, unsigned CI packages, local modifications, and third-party repackaging may not receive fixes.

The current `NativeProtected` profile is explicitly a native compatibility mode, not a kernel sandbox. Expected access using the current Windows user's filesystem and network permissions is therefore not, by itself, a vulnerability. A bypass of a permission decision, credential boundary, risk warning, process cleanup, sandbox attestation, or documented fail-closed behavior is in scope.

`WslStrict` is currently unavailable because complete child-process network enforcement cannot yet be attested. Any execution, authentication, silent downgrade, or session creation after strict attestation fails is a security issue and should be reported privately.

## Report A Vulnerability Privately

Use GitHub Private Vulnerability Reporting:

https://github.com/rkshadow999/AgentDesk/security/advisories/new

Do not open a public issue, discussion, pull request, or commit containing an undisclosed vulnerability, exploit, credential, private prompt, personal information, or private source code. If GitHub Private Vulnerability Reporting is unavailable, wait and retry rather than publishing sensitive details.

Include only the information needed to investigate:

- Affected commit, version, package type, architecture, and execution profile.
- Security impact and the boundary that was crossed.
- Minimal reproduction steps or a small sanitized reproducer.
- Relevant logs with API keys, tokens, usernames, local paths, prompts, and private source removed.
- Whether the issue is already being exploited or has been disclosed elsewhere.

## What Happens Next

Repository maintainers use the private advisory to validate the report, coordinate a fix, request a CVE when appropriate, and agree on disclosure. Timing depends on severity, reproducibility, affected upstream code, and release readiness. Please do not disclose the issue until a coordinated date is agreed or maintainers confirm that publication is safe.

If the report affects inherited `xai-org/grok-build` code, AgentDesk maintainers may coordinate privately with the relevant upstream maintainers. Reporter details and private artifacts will be shared only when necessary for remediation.

## Scope

Security reports may cover the WinUI/WebView2 host, ACP transport and sidecar lifecycle, Rust AgentDesk extensions, credential storage, permission handling, execution-profile enforcement, WSL attestation, packaging, update or release metadata, and repository automation. Vulnerabilities in an independent dependency should also be reported to that dependency's maintainers; include the dependency advisory reference when it materially affects AgentDesk.

The repository-grounded [threat model](docs/AGENTDESK-THREAT-MODEL.md) documents expected native authority, trust boundaries, abuse paths, and residual risks. Its limitations do not narrow the private-reporting scope above.
