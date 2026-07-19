# AgentDesk Phase Closure Implementation Plan

[English](2026-07-18-agentdesk-phase-closure.md) | [简体中文](2026-07-18-agentdesk-phase-closure.zh-CN.md)

> **For AI implementers:** Execute each task with test-driven development and keep the `feature/agentdesk-alpha` linked worktree. Rust release builds are serialized through the release-audit worker.

**Goal:** Close the remaining user-facing gaps across the four AgentDesk phases, validate the real Windows desktop against the supplied OpenAI-compatible provider, and publish a reusable bilingual public repository.

**Architecture:** Keep Rust as the ACP sidecar authority and project all new operations through typed C# contracts and the existing serialized host controller. Native file selection, notifications, backup, updater launch, and credential persistence stay in the WinUI/Windows layer; WebView2 receives only bounded, non-secret state. Optional cloud operations reuse the encrypted `AgentDesk.Cloud.Client` workflow and never make local operation depend on a cloud account.

**Technology:** .NET 10, WinUI 3, WebView2, React/Vite/Vitest, xUnit, Rust ACP, Windows Credential Manager, SQLite, GitHub Actions.

---

### Task 1: Desktop maintenance and session transfer

**Files:**
- Create: `desktop/src/AgentDesk.App/Maintenance/AgentDeskMaintenanceCoordinator.cs`
- Create: `desktop/src/AgentDesk.App/Maintenance/DesktopFileDialogRequest.cs`
- Modify: `desktop/src/AgentDesk.App/AgentDesk.App.csproj`
- Modify: `desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`
- Modify: `desktop/src/AgentDesk.App/Bridge/WebCommandDispatcher.cs`
- Modify: `desktop/src/AgentDesk.App/Bridge/WebMessageProtocol.cs`
- Modify: `desktop/src/AgentDesk.App/MainWindow.xaml.cs`
- Modify: `desktop/web/src/Workbench.tsx`
- Modify: `desktop/web/src/locales/en-US.json`
- Modify: `desktop/web/src/locales/zh-CN.json`
- Test: `desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`
- Test: `desktop/tests/AgentDesk.App.Tests/WebCommandDispatcherTests.cs`
- Test: `desktop/tests/AgentDesk.App.Tests/WebMessageProtocolTests.cs`
- Test: `desktop/web/tests/workbench.test.tsx`

- [x] Add failing protocol tests for `session/export`, `session/import`, `backup/create`, `backup/restore`, `update/check`, and `update/apply`; reject unknown fields, overlong paths, and operations while a prompt is running.
- [x] Add failing host tests proving engine export/import round trips bounded UTF-8 JSON, import creates a new session, backup restore stops the sidecar before replacing data, and update apply is delegated to the external updater.
- [x] Implement a coordinator over `AgentDeskBackupService`, `PortableUpdateService`, and the standalone updater executable. Do not perform in-process installation of the running app.
- [x] Add native save/open pickers initialized with the WinUI window handle; cancellation returns a neutral event and never invents a path.
- [x] Add bilingual settings actions and progress/error events. Never place a session document, API key, recovery key, or file body in WebView2 state.
- [x] Run focused App and Web tests, then the full App/Web suites.

### Task 2: Worktree and review lifecycle

**Files:**
- Modify: `desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`
- Modify: `desktop/src/AgentDesk.App/Bridge/WebMessageProtocol.cs`
- Modify: `desktop/web/src/Workbench.tsx`
- Modify: `desktop/web/src/locales/en-US.json`
- Modify: `desktop/web/src/locales/zh-CN.json`
- Test: `desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`
- Test: `desktop/tests/AgentDesk.App.Tests/WebMessageProtocolTests.cs`
- Test: `desktop/web/tests/workbench.test.tsx`

- [x] Add failing tests for create/list/show/apply/remove/gc commands and generation-safe result events.
- [x] Wire the existing typed `IEngineClient` worktree methods through the host without weakening path, ID, ref, count, or text limits.
- [x] Add a bounded worktree panel with explicit base reference, apply confirmation, retained/removed status, and a code-review command that remains subject to normal permissions.
- [x] Run Engine, App, and Web worktree tests plus formatting.

### Task 3: Optional encrypted cloud desktop workflow

**Files:**
- Create: `desktop/src/AgentDesk.App/Cloud/AgentDeskCloudDesktopService.cs`
- Create: `desktop/src/AgentDesk.App/Cloud/CloudDesktopState.cs`
- Modify: `desktop/src/AgentDesk.App/AgentDesk.App.csproj`
- Modify: `desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`
- Modify: `desktop/src/AgentDesk.App/Bridge/WebMessageProtocol.cs`
- Modify: `desktop/web/src/Workbench.tsx`
- Modify: `desktop/web/src/locales/en-US.json`
- Modify: `desktop/web/src/locales/zh-CN.json`
- Test: `desktop/tests/AgentDesk.App.Tests/AgentDeskCloudDesktopServiceTests.cs`
- Test: `desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`
- Test: `desktop/web/tests/workbench.test.tsx`

- [x] Add failing tests for local-only default, HTTPS endpoint validation, token storage in Credential Manager, recovery-key creation, upload/download/import, handoff receive/acknowledge ordering, automation list/create/disable, runner registration/job flow, and policy projection.
- [x] Compose `JsonCloudConnectionProfileStore`, `CredentialCloudAccessTokenProvider`, `CredentialRecoveryKeyStore`, `SqliteCloudSyncMetadataStore`, `AgentDeskCloudClient`, and `EngineCloudSessionWorkflow` behind one disposable desktop service.
- [x] Keep access tokens and recovery keys out of JSON, Web events, exception text, and logs. Cloud remains opt-in and all local commands work when the profile is local-only.
- [x] Add a bilingual Cloud section with sync, handoff, runner, automation, and team-policy controls. Mark remote execution and UI automation as policy-controlled experimental capabilities.
- [x] Run Cloud.Client unit/integration tests, App tests, and Web tests.

### Task 4: Notifications and explicitly scoped Windows automation experiment

**Files:**
- Create: `desktop/src/AgentDesk.App/Notifications/IUserNotificationService.cs`
- Create: `desktop/src/AgentDesk.App/Notifications/WindowsUserNotificationService.cs`
- Create: `desktop/src/AgentDesk.App/Automation/WindowsAutomationPolicy.cs`
- Modify: `desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`
- Modify: `desktop/src/AgentDesk.App/Settings/UiPreferences.cs`
- Modify: `desktop/web/src/Workbench.tsx`
- Modify: `desktop/web/src/locales/en-US.json`
- Modify: `desktop/web/src/locales/zh-CN.json`
- Test: `desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`
- Test: `desktop/tests/AgentDesk.App.Tests/JsonUiPreferencesStoreTests.cs`
- Test: `desktop/web/tests/workbench.test.tsx`

- [x] Add failing tests that notifications contain only task status and session ID, are disabled by default, and never include prompt/file/terminal text.
- [x] Add an experimental automation policy that is disabled by default, requires an explicit local toggle plus cloud-team permission when cloud policy is active, and cannot bypass the normal permission queue.
- [x] Surface the experiment with interruption, scope, and audit warnings. Do not silently enable Windows UI control.
- [x] Run App and Web accessibility-focused tests.

### Task 5: Release truth, full verification, and public repository

**Files:**
- Modify: `README.md`, `README.zh-CN.md`
- Modify: `docs/ROADMAP.md`, `docs/ROADMAP.zh-CN.md`
- Modify: `docs/ARCHITECTURE.md`, `docs/ARCHITECTURE.zh-CN.md`
- Modify: `docs/BUILD-AND-TEST.md`, `docs/BUILD-AND-TEST.zh-CN.md`
- Modify: `.github/workflows/agentdesk-windows.yml`
- Modify: `scripts/agentdesk/*` only where final verification exposes a release-contract gap

- [x] Replace stale phase status only after the corresponding desktop workflow has passing tests and live evidence.
- [x] Run Web tests/build, every .NET unit and integration project, focused Rust ACP/session tests, repository/release/update scripts, formatting, whitespace, secret, and large-file scans.
- [ ] Build the single audited `release-dist` sidecar, enforce the 8 MiB PE stack gate, and create x64/ARM64 Portable/MSIX inputs, SBOMs, notices, checksums, rollback material, and signed update metadata. Do not claim a signed MSIX without a real code-signing certificate.
- [ ] Test the local web surface with the in-app Browser and the real WinUI app with Windows UI automation at desktop scaling and keyboard paths.
- [x] Inject the supplied provider secret only into the live process, confirm `/models`, streaming prompt, cancellation, error handling, and absence of secret leakage, then clear the process-scoped secret.
- [ ] Create `rkshadow999/AgentDesk` as a public repository, write the update private key directly to a GitHub Actions secret without printing it, remove the temporary key directory, push `main`, enable Issues/Discussions/private vulnerability reporting/topics/branch protection, and inspect the first Actions runs.
