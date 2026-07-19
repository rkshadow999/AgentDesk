# AgentDesk Roadmap And Delivery Status

[English](ROADMAP.md) | [简体中文](ROADMAP.zh-CN.md)

This document reports the source-tree state on 2026-07-19. It is not a release schedule. A protocol method, unit test, or server endpoint is not considered a complete user feature until the desktop workflow, security boundary, recovery behavior, documentation, and release evidence are present.

## Status Legend

- **Available in source:** connected through the local desktop workflow and covered by focused tests. A signed public release may still be pending.
- **Partial/experimental:** useful implementation exists, but a required UI, isolation, integration, or release gate is missing.
- **Blocked:** intentionally unavailable because a security requirement cannot be proven.
- **Not implemented:** no feature-complete AgentDesk desktop path exists.

## Capability Matrix

| Capability | Status | Evidence and boundary |
| --- | --- | --- |
| Windows 11 WinUI shell and two WebView2 surfaces | Available in source | x64/ARM64 project configuration, workbench/inspector bridge, Web component tests |
| ACP stdio sidecar lifecycle, streaming, cancel, terminal, diff, permission approval | Available in source | Host/engine/Rust contract suites; native execution remains non-sandboxed |
| xAI and custom OpenAI-compatible Base URL/model/backend | Available in source | Chat Completions/Responses selection, endpoint-bound Credential Manager key, HTTPS default, explicit HTTP opt-in |
| Plan Mode | Available in source | Execute/Plan segmented control, capability negotiation, stale-event isolation |
| Session center | Available in source | Engine list/load/rename, search/paging, SQLite metadata index, reversible local archive |
| Session fork, compact, rewind points and rewind | Available in source | ACP extension client, host projection, Web dialogs and focused tests |
| Runtime command/Skill catalog and explicit Memory flush | Available in source | Command palette, capability-bound list, explicit flush control, host/Web tests |
| Active-session Runtime Dashboard | Available in source | Authoritative background task/subagent list, task kill, subagent detail/cancel, host and Web tests |
| Sidecar fault detection and restart isolation | Available in source | Process generation and transport-fault tests; full app crash recovery/migration remains a Beta gate |
| Simplified Chinese UI | Available in source | Default shell/Web resources and Chinese-first workflow |
| English UI and in-app language selector | Available in source | Web labels switch immediately; WinUI resources apply after restart; manual truncation/accessibility review remains a release gate |
| WSL strict execution | Blocked | Named non-Docker distribution selection is fail-closed; health attestation still reports incomplete child-process network coverage and stops startup |
| Image attachments | Available in source | PNG/JPEG/GIF/WebP signature checks, count/size budgets, capability fail-closed behavior, preview/send tests |
| Workspace references, AGENTS.md, Memory, and extension management | Available in source | Bounded `@` file search/chips, an existing-file `AGENTS.md` editor, a 64 KiB capability-gated Memory browser with two-stage mutation confirmation, and MCP/Skills/Hooks/Plugins/Marketplace management; remote Cloud profiles fail closed for every registry-changing Plugin/Marketplace action |
| Backup, session transfer, pairing, and Portable update | Available in source | Native file pickers, bounded session documents, backup/restore, hardened pairing package file I/O, pinned-key manifest verification, external Portable updater, and persisted opt-in background availability checks that default off; apply remains explicit and MSIX update remains external |
| Worktree lifecycle | Available in source | Create/list/show/apply/remove/GC plus a two-stage editable review request that starts through the standard prompt/permission chain, dry-run and conflict projection, host/engine/Web tests |
| Automatic parallel worktree isolation | Available in source | In strict mode, the desktop sidecar forces every subagent, including explore/plan, into an isolated worktree and aborts instead of sharing when setup fails; explicit branch ownership and automatic conflict resolution remain future workflow refinements |
| Windows notifications | Available in source | Opt-in generic completion/permission notifications and session-only activation routing; no prompt or workspace path in activation arguments |
| Browser testing and Windows UI Automation Computer Use | Partial/experimental | Settings exposes the native FlaUI/UIA3 focus-window/invoke/set-value surface with host policy, allow-once approval, and value-redacted events; packaged real-target evidence, isolation, autonomous target discovery, and general Computer Use guarantees are not delivered |
| Self-hosted cloud server | Partial/experimental | Auth/RBAC, encrypted-envelope storage, runners, policy, handoffs, plugin signatures, automations, SignalR, a 37-test server suite, and a real-process offline backup/restore/rollback E2E job exist in `cloud/`; production operations are incomplete |
| Desktop encrypted sync, Runner, automation, and cross-device handoff | Partial/experimental | Default local-only profile, Credential Manager token/recovery key storage, AES-GCM metadata binding, rollback detection, hardened pairing, upload/download/import/export/delete, handoff, policy, Runner register/queue/claim/complete, automation create/list/disable, authenticated SignalR change notifications, and real Kestrel integration tests are connected; production Runner and background/device push delivery are not |

## Current Verification Snapshot

The exact current local verification counts are maintained in [Build and Test](BUILD-AND-TEST.md) and regenerated before publication. Windows Automation policy/coordinator/protocol, Cloud cryptographic binding, pairing-file hardening, release staging, and the sanitized provider-smoke tool are covered by automated tests. Authorized live Chat Completions and Responses provider runs are complete; a real packaged UI Automation target remains a final-candidate gate.

The public source repository is [rkshadow999/AgentDesk](https://github.com/rkshadow999/AgentDesk). No signed public MSIX, ARM64 real-device launch, usable `WslStrict` profile in a real WSL2 environment, production Runner, production Cloud deployment, or equivalence with official Codex private services is claimed. CI definitions and locally generated unsigned artifacts do not satisfy those gates.

## Product Principles

- Remain independent and community-maintained, with clear `xai-org/grok-build` provenance and no copied Codex name, logo, assets, or private service claims.
- Keep the Windows client local-first and useful without an AgentDesk account or cloud service.
- Make execution authority, permission decisions, provider transport, and sandbox limitations visible.
- Negotiate capabilities through ACP instead of linking unstable Rust internals.
- Publish AgentDesk-owned user and contributor documentation in English and Simplified Chinese together.
- Enable a security profile only when enforcement is verified; otherwise fail closed.

## Stage 1: Alpha Local Workbench

Implemented in the source tree:

- [x] Windows WinUI host and React workbench/inspector with x64/ARM64 project configuration, sidecar ownership, streaming task flow, cancellation, permission approval, terminal and diff inspection.
- [x] Credential Manager integration, custom provider settings, credential environment cleanup, and process-tree cleanup.
- [x] Portable/MSIX build inputs, architecture checks, package legal notices, SBOM/checksum workflow, tag signing gate, and previous-release rollback bundle generation.
- [x] Windows/Linux CI matrix definitions and focused Web/.NET/Rust/release tests.

Open Alpha release gates:

- [ ] Produce and verify the first signed public x64 and ARM64 release in the public repository. A workflow file alone is not release evidence.
- [ ] Complete packaged smoke tests on both architectures and manual Chinese IME, keyboard, Narrator, high-contrast, and 125%-200% scaling checks.
- [ ] Retain deterministic mock-provider coverage and run the opt-in real-provider smoke tool with process-scoped environment variables; neither path may log credentials or request/response bodies.
- [ ] Keep `WslStrict` blocked until all child launch paths enforce and attest network restriction. Native compatibility remains available with an explicit warning.

## Stage 2: Complete Local Workflows

Delivered ahead of the original stage boundary:

- [x] Plan Mode with host-authoritative confirmed state.
- [x] Session search, paging, open/load, rename, reversible local archive, fork, compact, rewind points, and rewind.
- [x] Durable SQLite UI metadata separated from engine-owned session content.
- [x] Provider Base URL/model/backend configuration, including Responses API selection and explicit insecure HTTP consent.
- [x] English selection, validated image attachments, session export/import, local backup/restore, and Windows notifications.
- [x] Worktree create/list/show/apply/remove/GC workflows with explicit destructive confirmation and dry-run support. Review first prepares an editable request, then requires a separate Start review action that uses the normal prompt and permission path.
- [x] Bounded workspace file references, an existing-file `AGENTS.md` editor, and a Memory browser whose writes/deletes require host-authoritative two-stage confirmation.
- [x] MCP, Skills, Hooks, Plugins, and Marketplace settings with bounded protocol projection and environment-variable references instead of secret values. Remote Cloud profiles block every Plugin mutation and Marketplace install/update/uninstall until the host can verify a registry record, digest, and signature; catalog list/refresh remains available.
- [x] User-initiated Portable update checks with pinned signing-key verification and a standalone updater. Optional background availability polling is persisted, disabled by default, runs periodically only after opt-in, and never applies an update automatically.

Still required for Stage 2 completion:

- [ ] Complete manual bilingual, IME, accessibility, and scaling verification on packaged Windows builds.
- [ ] Broader file navigation beyond the connected `@` search/chips, plus extension provenance and recovery workflows beyond the connected management actions.
- [ ] Complete crash recovery/data migrations and documented restore compatibility across released versions.
- [ ] Complete packaged failure-recovery, downgrade, privacy, and rollback verification for the Portable updater and its opt-in background availability notifications. MSIX remains outside the in-app replacement path.
- [ ] Automated accessibility and performance gates for cold start, sidecar handshake, 10,000 sessions, and 100,000 terminal lines. These targets are not currently proven.

## Stage 3: Advanced Agent Workflows

Connected in source at the host/protocol boundary:

- [x] Active-session Runtime Dashboard with authoritative task/subagent list, task kill, detail, and cancellation operations.
- [x] Manual worktree create/list/show/apply/remove/GC and the two-stage editable review request.
- [x] Opt-in generic Windows completion/permission notifications with session-only activation routing.
- [x] A limited native Windows UI Automation Settings surface. The executor attaches to an explicit process ID, can focus a window, invoke a selected control, or set a selected value, and requires both policy enablement and an allow-once permission.

This does not turn AgentDesk into a generally isolated Computer Use system. Stage 3 is still not complete. Required delivery includes:

- [ ] Extend the Runtime Dashboard with per-task permission queues, execution profile, output ownership, cross-session navigation, and complete stale-generation recovery.
- [ ] Extend the existing fail-closed strict-mode worktree isolation for every subagent with explicit base-branch ownership, automatic conflict policy, retention controls, and cross-session recovery UX.
- [ ] Extend the current two-stage worktree review request into multi-agent review dashboards and reviewable handoffs without bypassing session or permission policy.
- [ ] Complete packaged notification activation and interruption testing while preserving the existing no-prompt/no-source payload boundary.
- [ ] Complete browser testing through an isolated WebView2/CDP test path with explicit network/data disclosure; helper tests alone are not packaged browser evidence.
- [ ] Harden the experimental Windows UI Automation surface with packaged real-target tests, durable audit metadata that excludes entered values, stronger interruption guarantees, and an isolation story. It must never be silently enabled.

No Stage 3 feature may bypass the same permission, provider, credential, and execution gates used by foreground tasks.

## Stage 4: Optional Self-Hosted Cloud

Connected developer-preview scope:

- [x] The separate `cloud/` server implements hashed role tokens, policy, revisioned opaque envelopes, runner leases, encrypted handoffs, ECDSA plugin publisher metadata, scheduled jobs, and authenticated SignalR notifications.
- [x] The opt-in desktop client implements encrypted session sync, Runner queue/claim/complete, automation create/list/disable, policy, and cross-device handoff while its persisted default remains local-only.
- [x] Recovery-key pairing packages use bounded native file I/O with final-path and reparse checks plus atomic replacement.

It is not a complete cloud product. Stage 4 still requires:

- [ ] Recovery-key rotation/revocation, multi-device rollback recovery, and production privacy/retention controls around the connected encrypted client.
- [ ] A production Runner with attested isolation, least-privilege secret delivery, cancellation, upgrade, and audit behavior.
- [ ] Production deployment guidance for TLS, reverse proxy headers, token rotation, backup/restore, database migration, quotas, monitoring, multi-instance scheduling, and incident response.
- [ ] Production background/device push delivery beyond the connected authenticated SignalR desktop channel, production Runner UX, complete team-policy enforcement across every desktop action, a host-verified signed plugin repository trust UX, and packaged multi-device tests. Client-supplied publisher identifiers must remain untrusted.
- [ ] Explicit retention and privacy controls. API keys, prompts, source, and command output must never be uploaded by default.

The local-only desktop remains supported and must not require this service.

## Release Completion Gates

Every public release must have evidence for the exact commit:

1. Relevant Web, .NET, cloud, Rust, ACP lifecycle, repository, and release-contract tests pass.
2. x64 and ARM64 package inputs are built; tag MSIX files are signed and signer-verified.
3. SPDX and CycloneDX SBOMs, Apache/MPL/third-party notices, source revision, SHA-256 lists, and a previous-release rollback artifact are present.
4. No committed or logged key, token, prompt, private source, username, or local path is found.
5. Changed trust boundaries and user workflows have synchronized English/Chinese documentation.
6. Manual platform/accessibility checks and any unautomated risks are recorded without converting them into unsupported claims.

See [Build and Test](BUILD-AND-TEST.md), [Installation](INSTALLATION.md), and the [threat model](AGENTDESK-THREAT-MODEL.md).

## Non-goals

- Pixel-for-pixel copying of Codex or use of its name, logos, proprietary assets, account system, quotas, connectors, or cloud execution.
- Reusing an unauthorized grok.com OAuth client.
- Calling native compatibility a sandbox.
- Requiring telemetry, crash upload, or a hosted account for local work.
- Claiming model quality or private service equivalence that this repository cannot provide.
