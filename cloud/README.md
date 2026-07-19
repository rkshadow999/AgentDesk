# AgentDesk Optional Self-Hosted Cloud

[English](README.md) | [简体中文](README.zh-CN.md)

See [Deployment and Operations](OPERATIONS.md) for the implemented single-instance, token-rotation, offline-backup, and offline-restore procedures.

> [!WARNING]
> This project is a developer preview, not a supported production service. The AgentDesk desktop has an explicit opt-in client, but its default profile is local-only. Offline database maintenance and bootstrap-token overlap are implemented, but production TLS validation, monitoring, external audit, recovery-key lifecycle, multi-instance coordination, and packaged multi-device tests remain incomplete. Do not treat this preview as a managed production service.

## Implemented Server Surface

The ASP.NET Core service currently provides:

- Anonymous liveness/readiness health endpoints; all `/api/v1` routes require bearer authentication.
- Current and optional previous bootstrap administrator tokens for bounded restart-based rotation, plus hashed issued `device` and `service` tokens with role policies; administrators can idempotently revoke an issued subject through `DELETE /api/v1/tokens/{subjectId}`.
- Team policy persistence.
- Revisioned opaque encrypted session envelopes and encrypted device-to-device handoffs.
- Runner registration, capability matching, encrypted job queueing, leased claim, and lease-owner completion.
- ECDSA publisher registration and signed plugin metadata publication.
- Scheduled encrypted automation jobs with a background worker.
- Header-authenticated SignalR negotiation and team/device-scoped server notifications. Query-string `access_token` authentication is rejected, and no browser SignalR client is currently claimed or shipped.
- SQLite persistence with a single-process service lease, validated offline backup/restore scripts, request validation, envelope size limits, and a fixed-window API rate limit.

The server validates supported envelope names, Base64 encoding, ciphertext size, identifiers, roles, revisions, leases, and plugin signatures. `AES-256-GCM` requires a 12-byte nonce; `XCHACHA20-POLY1305` requires a 24-byte nonce. It does **not** encrypt plaintext for clients and never claims that signed plugin content is safe to execute.

## Desktop Client Boundary

The desktop stays local-only unless the user saves a remote profile. Remote profiles require HTTPS, except that HTTP is accepted only for loopback development. Endpoint/team/device metadata is stored under `%LOCALAPPDATA%\AgentDesk`; access tokens and recovery keys are stored through Windows Credential Manager and are never sent to WebView2.

The connected desktop workflow can:

- Export an engine-owned session document, bind team/scope/session/revision as AES-GCM authenticated data, upload it, detect a lower server revision, download/decrypt it, and import it as an engine session.
- Export/import a passphrase-protected recovery-key pairing package through native file dialogs, then create and receive encrypted device handoffs. Pairing files are size-bounded, reject alternate data streams/device paths/reserved names/reparse points, validate final paths through opened handles, and use atomic replacement. A handoff is acknowledged only after the engine imports it.
- Read/update team policy; register a Runner identity/capability set; queue, claim, and complete client-encrypted Runner task/result bodies; and create/list/disable client-encrypted automations. It does not ship a production Runner, remote execution attestation, background push client, or complete policy enforcement across every desktop action.

In a remote profile, the desktop fails closed for all Plugin and Marketplace actions that can load code. A publisher ID supplied by the Web UI is not signature evidence. Catalog list/refresh and reduction actions such as Plugin disable/remove and Marketplace uninstall remain available; a future installation path must bind a host-verified registry record, digest, and signature.

These flows are covered by unit tests and a real Kestrel integration test that checks the server database does not contain the known plaintext session body. This is useful evidence for the implemented path, not proof of production deployment safety.

## Local Development Only

Generate a fresh development bootstrap token rather than committing one:

```powershell
$bytes = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$env:AgentDeskCloud__BootstrapToken = [Convert]::ToBase64String($bytes)
$env:AgentDeskCloud__DatabasePath = "$env:TEMP\agentdesk-cloud-dev.db"
$env:AgentDeskCloud__RequireHttps = "false"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5187"

dotnet run --project .\cloud\src\AgentDesk.Cloud\AgentDesk.Cloud.csproj
```

`RequireHttps=false` is acceptable only for an isolated loopback development test. Never bind that configuration to a LAN or public interface because bearer tokens and ciphertext metadata can be intercepted or modified.

In another terminal:

```powershell
Invoke-RestMethod http://127.0.0.1:5187/health/live
Invoke-RestMethod http://127.0.0.1:5187/health/ready
```

API requests use `Authorization: Bearer <token>`. Keep tokens in process-scoped environment variables or a secret manager, not source, `.env` files committed to Git, shell transcripts, URLs, screenshots, or issue reports.

## Configuration

Environment variables follow standard .NET double-underscore binding:

| Key | Default | Boundary |
| --- | --- | --- |
| `AgentDeskCloud__DatabasePath` | `data/agentdesk-cloud.db` beside the app | Protect with service-account filesystem ACLs and backups |
| `AgentDeskCloud__BootstrapToken` | empty; startup validation fails | Minimum 32 characters; grants administrator authority |
| `AgentDeskCloud__PreviousBootstrapToken` | empty | Previous administrator token only during a bounded rotation overlap; must differ from the current token |
| `AgentDeskCloud__RequireHttps` | `true` | Keep enabled outside loopback tests |
| `AgentDeskCloud__MaximumCiphertextBytes` | 16 MiB | Valid range is 1 KiB to 64 MiB |
| `AgentDeskCloud__AutomationPollingIntervalSeconds` | 5 | Valid range is 1 to 300 seconds |

For a TLS development run, configure a trusted Kestrel development certificate and keep `RequireHttps=true`. The service does not currently trust forwarded headers or provide multi-instance coordination. The bounded TLS/reverse-proxy requirements, ACLs, manual token rotation, offline database maintenance, monitoring minimums, and incident procedures are documented in [Deployment and Operations](OPERATIONS.md).

## Tests

```powershell
dotnet test .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj `
  --configuration Release

dotnet test .\desktop\tests\AgentDesk.Cloud.Client.IntegrationTests\AgentDesk.Cloud.Client.IntegrationTests.csproj `
  --configuration Release

pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskCloudDatabaseMaintenance.ps1
```

The server suite uses a temporary SQLite database and disables HTTPS only inside the in-process test server. It covers overlapping bootstrap-token rotation and invalid startup configuration, fail-closed HTTPS enforcement, safe fresh-directory creation, local/reparse/hard-link database path rejection, non-destructive service-lock creation, authentication and least-privilege roles, idempotent token revocation, tenant/lease ownership, monotonic revisions, algorithm-specific nonce rejection, policy, handoff ownership, Runner queue/claim/complete, signature rejection/acceptance, automation creation/scheduling, header-authenticated SignalR negotiation, and query-token rejection. The desktop integration project starts a real loopback Kestrel process and exercises the client API and encrypted envelopes. The latest recorded local runs passed `37/37` server tests and `3/3` real-Kestrel desktop integration tests; the maintenance script E2E also passed against a real Cloud process. CI keeps the cross-platform server suite in `cloud-tests` and runs backup/restore/rollback recovery on Windows in the separate `cloud-maintenance` job. None of this is production deployment evidence.

## Security And Data Boundary

- Bootstrap tokens are administrative recovery/bootstrap secrets. The current and optional previous token are stored in memory as SHA-256 digests and compared in fixed time across configured slots. Rotation remains a manual restart procedure; remove the previous token immediately after consumers move to the current token.
- Issued tokens are stored as hashes, but possession grants their role until an administrator revokes the subject. Revocation is idempotent; operators still need an audited rotation and recovery procedure.
- Session, handoff, job, and automation bodies are opaque ciphertext only when the client encrypts them correctly. The server still sees team, subject, device, runner, capability, timing, revision, and size metadata.
- The current desktop binds identifiers/revisions as authenticated associated data, uses OS-protected recovery-key storage, and rejects lower revisions. Production work still needs recovery-key rotation/revocation, multi-device rollback recovery, audited deletion/export, and a documented nonce/key lifecycle.
- Pairing package path hardening reduces reparse/path-substitution and partial-write risk, but an exposed package plus passphrase can still expose the recovery key.
- ECDSA plugin signatures authenticate a registered publisher and manifest digest. They do not sandbox, review, or make plugin code trustworthy.
- The desktop therefore blocks code-loading Plugin/Marketplace actions in every remote profile until publisher, digest, and signature verification is host-authoritative; client publisher claims remain untrusted.
- A remote Runner is an execution trust boundary. No production Runner package, attestation, isolation profile, or secret-delivery protocol is shipped here.
- SignalR accepts only the `Authorization` header and rejects query-string bearer tokens to reduce proxy/access-log leakage. Some browser SignalR transports cannot set that header, so browser client compatibility is not provided by the current server.
- One process may own a database path. Backup and restore are offline-only, validate SQLite integrity and SHA-256 evidence, and preserve a rollback copy before replacement; they are not online or multi-instance recovery.

See the repository [threat model](../docs/AGENTDESK-THREAT-MODEL.md) and [Security Policy](../SECURITY.md). The local-only desktop workflow remains the default and stays independent of this project.
