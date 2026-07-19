# AgentDesk Cloud Deployment And Operations

[English](OPERATIONS.md) | [简体中文](OPERATIONS.zh-CN.md)

> [!WARNING]
> AgentDesk Cloud remains a developer preview. This guide documents the deployment and recovery controls that are implemented and testable in this repository. It is not a production certification. The service supports one process per SQLite database, offline backup and restore, and manual overlapping bootstrap-token rotation. It does not provide multi-instance coordination, online restore, a production Runner, managed TLS, or a complete observability stack.

## Supported Operating Model

- Run exactly one AgentDesk Cloud process for each database path. The process holds `<database>.service.lock`; a second process or maintenance operation fails closed while that lease is held.
- On Linux, an abrupt process or host failure can leave a stale `.service.lock`. Confirm that no Cloud or maintenance process is running, preserve the stale file with incident evidence, then remove or rename it before restart. Never delete a lock merely because health checks are failing.
- Keep the SQLite database, service lock, and local backup staging directory on a local filesystem protected by the service account. Do not place the active database on SMB, NFS, synchronized folders, or user-writable shared storage.
- Stop the service before backup or restore. The maintenance scripts enforce the service lease and do not implement online recovery.
- Keep the desktop local-only unless an operator has explicitly provisioned this service and its trust material.
- Treat Runner, automation, plugin, and remote execution surfaces as experimental. No production Runner or execution attestation is shipped.

## Service Account And ACLs

Use a dedicated, non-interactive service account. Grant it read/write access only to:

- the published application directory when required by the hosting model;
- the database directory containing the `.db`, `-wal`, `-shm`, and `.service.lock` files;
- a local log directory configured by the operator.

The backup operator also needs read access to the published Cloud executable and read/write access to the protected backup staging directory. Administrators and backup software may read that directory; ordinary interactive users must not. Deny inheritance when the parent directory grants broad write access, and audit ACL changes.

Never place bootstrap tokens, issued tokens, recovery keys, signing keys, or certificate private keys in the database directory, command-line arguments, source files, or service logs.

## TLS And Reverse Proxies

`AgentDeskCloud__RequireHttps` defaults to `true` and must remain enabled outside isolated loopback development. When enabled, every plain HTTP request, including health and authenticated API requests, is rejected with `426 Upgrade Required`; the service does not depend on an inferred HTTPS redirect port.

The currently shipped service does not enable ASP.NET Core Forwarded Headers Middleware or maintain a trusted-proxy allowlist. Therefore, do not rely on client-supplied `X-Forwarded-For`, `X-Forwarded-Host`, or `X-Forwarded-Proto` headers. Use one of these bounded deployment models:

1. Terminate TLS directly in Kestrel with an operator-managed certificate.
2. Terminate public TLS at a reverse proxy and re-encrypt the same-host or private-network hop to Kestrel with HTTPS. Strip incoming forwarding headers and set proxy access controls independently; AgentDesk Cloud will still evaluate the backend request as HTTPS.

Do not forward plain HTTP over a shared network while setting `X-Forwarded-Proto: https`; the application intentionally does not trust that header. Configure certificate renewal, minimum TLS versions, cipher policy, request-size limits, timeouts, and security headers in the selected TLS endpoint. SignalR authentication must remain in the `Authorization` header; query-string bearer tokens are rejected.

## Required Configuration

Inject configuration through a service manager or secret store. Environment variables use .NET double-underscore binding.

| Variable | Operational requirement |
| --- | --- |
| `AgentDeskCloud__DatabasePath` | Fully qualified local path in the protected database directory |
| `AgentDeskCloud__BootstrapToken` | Current administrator token, at least 32 characters |
| `AgentDeskCloud__PreviousBootstrapToken` | Empty normally; previous administrator token only during a bounded rotation overlap |
| `AgentDeskCloud__RequireHttps` | `true` outside isolated loopback development |
| `AgentDeskCloud__MaximumCiphertextBytes` | Set from expected workload; accepted range is 1 KiB to 64 MiB |
| `AgentDeskCloud__AutomationPollingIntervalSeconds` | Accepted range is 1 to 300 seconds |

Startup fails when the current token is short or blank, when a configured previous token is short or blank, or when the two tokens are identical. Only SHA-256 digests are retained by the authentication handler, and comparisons are fixed-time across all configured bootstrap slots.

## Bootstrap Token Rotation

Rotation requires a restart and a deliberately short overlap window:

1. Generate a new random token of at least 32 characters in the approved secret manager.
2. Stop the single service instance.
3. Set `AgentDeskCloud__BootstrapToken` to the new token and `AgentDeskCloud__PreviousBootstrapToken` to the old token.
4. Start the service and verify that both tokens can call `GET /api/v1/policy` with an `Authorization: Bearer` header.
5. Update every authorized administrative consumer to the new token. Do not put either token in URLs, shell history, screenshots, or tickets.
6. Stop the service, remove `AgentDeskCloud__PreviousBootstrapToken`, and restart.
7. Verify the new token receives `200` and the old token receives `401` from `GET /api/v1/policy`.
8. Record the rotation time and operator in the external audit system.

The previous token is not an archival slot. Leaving it configured indefinitely doubles the bootstrap credential exposure. Issued device and service tokens have an independent lifecycle; revoke affected subjects through `DELETE /api/v1/tokens/{subjectId}`.

## Offline Backup

Publish or retain the exact Cloud server executable used by the deployment. The script invokes its maintenance command so `PRAGMA wal_checkpoint(TRUNCATE)` and `PRAGMA integrity_check` use the same SQLite provider as the service.

```powershell
Stop-Service AgentDeskCloud

pwsh -NoProfile -File .\scripts\agentdesk\Backup-AgentDeskCloudDatabase.ps1 `
  -DatabasePath "C:\ProgramData\AgentDeskCloud\data\agentdesk-cloud.db" `
  -BackupPath "D:\AgentDeskCloudBackups\agentdesk-cloud-2026-07-19.db" `
  -CloudServerPath "C:\Program Files\AgentDeskCloud\AgentDesk.Cloud.exe"

Start-Service AgentDeskCloud
```

The backup target and its parent must already be protected local paths. The script refuses relative, UNC, device, alternate-data-stream, reparse, existing, and same-as-source targets. It obtains the service lease, checkpoints WAL, verifies the SQLite header and integrity, copies through exclusive file handles, validates the copy, and writes `<backup>.sha256`.

Move the completed `.db` and `.sha256` pair to encrypted off-host storage only after the script succeeds. Retain multiple dated generations and periodically test restoration on an isolated host. A copied database without its matching checksum is not an accepted backup.

## Offline Restore

Restore is destructive and must remain offline. Verify the selected backup generation, available disk space, incident authorization, and rollback-directory ACL before running:

```powershell
Stop-Service AgentDeskCloud

pwsh -NoProfile -File .\scripts\agentdesk\Restore-AgentDeskCloudDatabase.ps1 `
  -DatabasePath "C:\ProgramData\AgentDeskCloud\data\agentdesk-cloud.db" `
  -BackupPath "D:\AgentDeskCloudBackups\agentdesk-cloud-2026-07-19.db" `
  -RollbackDirectory "C:\ProgramData\AgentDeskCloud\rollback" `
  -CloudServerPath "C:\Program Files\AgentDeskCloud\AgentDesk.Cloud.exe"

Start-Service AgentDeskCloud
```

The script refuses to proceed while the service lease is held. It verifies the sidecar SHA-256, SQLite header, and `PRAGMA integrity_check` before touching the active database. It creates and hashes an independent rollback copy, stages the validated bytes beside the active database, moves any existing `-wal` and `-shm` files into the rollback evidence set with their own checksums, atomically replaces the target, and validates the installed database again. If replacement or post-install validation fails, the script restores the original database and sidecars and retains the failed target as evidence. Stale SQLite sidecars are never left beside a successful restored database.

After restart, require `/health/ready`, authenticate with the current bootstrap token, inspect expected policy/session metadata, and retain the rollback pair until the restored service has passed the operator's validation window. Do not delete or overwrite the source backup during restore. Online restore and cross-instance restore are not supported.

## Monitoring And Alerting

The repository exposes health endpoints but no production metrics exporter or alert package. At minimum, the operator must monitor:

- process restarts and failure to acquire the database service lease;
- `/health/live` and `/health/ready` from the trusted monitoring network;
- TLS certificate expiry and reverse-proxy/backend TLS failures;
- disk capacity, SQLite I/O errors, integrity failures, and backup age;
- backup and restore script exit codes plus checksum verification;
- sustained `401`, `403`, `429`, and `5xx` rates without logging bearer values;
- automation backlog, Runner lease failures, and unexpected plugin publisher changes using external operational queries or logs.

Do not log request bodies, ciphertext, authorization headers, pairing packages, tokens, local usernames, or private filesystem paths to a shared telemetry service. Health success alone does not prove backup freshness, remote Runner safety, or end-to-end decryptability.

## Incident Response

- **Bootstrap token exposure:** restrict access, rotate with the overlap procedure, remove the previous token, and review administrative actions.
- **Issued token exposure:** revoke the affected subject, issue a replacement, and review team-scoped activity.
- **Database corruption or host loss:** stop the service, preserve the current database and WAL-related files, verify backup checksums, restore offline, and retain the generated rollback copy.
- **Signing key or plugin publisher compromise:** revoke trust outside the server, disable affected plugins/marketplaces, preserve evidence, and rotate publisher material. A signature does not make code safe.
- **Runner compromise:** disable remote Runner policy, revoke service credentials, stop queued execution, and assume task metadata and accessible execution secrets are exposed. No production Runner containment is provided.
- **Recovery-key exposure:** rotate client recovery material and re-pair devices through the desktop workflow; the server cannot recover plaintext or repair the client trust boundary.

Preserve relevant logs and immutable backup hashes, avoid publishing undisclosed vulnerabilities or credentials, and follow the repository [Security Policy](../SECURITY.md).

## Verification

Run the repository tests after any operational-tool change:

```powershell
dotnet test .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj `
  --configuration Release

pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskCloudDatabaseMaintenance.ps1
```

The script suite starts a real Cloud process, proves online backup and restore are rejected, performs a validated backup, rejects unsafe and corrupt inputs, restores exact bytes, verifies the rollback evidence set, and proves a failed post-install validation restores the original database and SQLite sidecars. `.github/workflows/agentdesk-windows.yml` runs this suite in the Windows `cloud-maintenance` job and requires it before tag publication. These tests do not establish multi-instance safety, online backup compatibility, production TLS, or production Runner readiness.
