# AgentDesk Full Access, Layout, and Font Controls Implementation Plan

> **For AI agent workers:** Use subagent-driven development or execute the checked steps inline. Track every step with the checkboxes below.

**Goal:** Deliver native-confirmed Full Access, a persistent resizable inspector, and persistent font scaling in the packaged Windows client.

**Architecture:** Extend the authoritative UI preference snapshot for access and font controls, keep inspector geometry in a separate native store, and let the host auto-answer only ACP tool approvals. Both WebViews project the same font preference while WinUI owns the native column layout.

**Tech stack:** .NET 10, WinUI 3, WebView2, React 19, TypeScript, Monaco, xterm.js, xUnit, Vitest.

---

### Task 1: Preference contract and Full Access

**Files:**
- Modify: `desktop/src/AgentDesk.App/Settings/UiPreferences.cs`
- Modify: `desktop/src/AgentDesk.App/Settings/JsonUiPreferencesStore.cs`
- Modify: `desktop/src/AgentDesk.App/Bridge/WebMessageProtocol.cs`
- Modify: `desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`
- Modify: `desktop/src/AgentDesk.App/MainWindow.xaml.cs`
- Test: `desktop/tests/AgentDesk.App.Tests/JsonUiPreferencesStoreTests.cs`
- Test: `desktop/tests/AgentDesk.App.Tests/WebMessageProtocolTests.cs`
- Test: `desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`

- [ ] Add failing tests proving schema 4 round-trips `fullAccessEnabled` and `fontScalePercent`, validates the five font values, and migrates schemas 1-3 to `false` and `110`.
- [ ] Add failing host tests proving Full Access requires native confirmation, auto-selects the first `AllowOnce`, falls back to `PermissionRequestedWebEvent` on no option/false/exception, and does not emit engine error.
- [ ] Run the focused xUnit filters and confirm failures are caused by missing fields and behavior.
- [ ] Add the two preference fields, strict protocol projection, native approval delegate/dialog, and asynchronous auto-approval fallback.
- [ ] Re-run focused xUnit tests and confirm zero failures.

### Task 2: Native inspector resizing

**Files:**
- Create: `desktop/src/AgentDesk.App/Windowing/InspectorPaneLayout.cs`
- Create: `desktop/src/AgentDesk.App/Windowing/WindowLayoutState.cs`
- Create: `desktop/src/AgentDesk.App/Windowing/JsonWindowLayoutStore.cs`
- Modify: `desktop/src/AgentDesk.App/MainWindow.xaml`
- Modify: `desktop/src/AgentDesk.App/MainWindow.xaml.cs`
- Modify: `desktop/src/AgentDesk.App/Strings/en-US/Resources.resw`
- Modify: `desktop/src/AgentDesk.App/Strings/zh-CN/Resources.resw`
- Test: `desktop/tests/AgentDesk.App.Tests/InspectorPaneLayoutTests.cs`
- Test: `desktop/tests/AgentDesk.App.Tests/JsonWindowLayoutStoreTests.cs`
- Test: `desktop/tests/AgentDesk.App.Tests/WebSurfacePolicyTests.cs`

- [ ] Add failing pure layout/store tests for clamping, resize direction, temporary narrowing/restoration, invalid JSON, strict fields, and round-trip.
- [ ] Add failing XAML policy assertions for named columns, Thumb handlers, keyboard accessibility, and inspector placement.
- [ ] Run focused xUnit tests and confirm the new tests fail for missing implementation.
- [ ] Implement the pure layout policy, atomic JSON store, XAML Thumb, drag/keyboard/reset handlers, modal disabling, load, debounce save, and shutdown flush.
- [ ] Re-run focused tests and build the WinUI XAML project for x64.

### Task 3: Web font and Full Access UI

**Files:**
- Modify: `desktop/web/src/hostBridge.ts`
- Modify: `desktop/web/src/Workbench.tsx`
- Modify: `desktop/web/src/InspectorSurface.tsx`
- Modify: `desktop/web/src/inspectorRuntime.ts`
- Modify: `desktop/web/src/monacoDiffViewer.ts`
- Modify: `desktop/web/src/xtermViewer.ts`
- Modify: `desktop/web/src/styles.css`
- Modify: `desktop/web/src/locales/en-US.json`
- Modify: `desktop/web/src/locales/zh-CN.json`
- Test: `desktop/web/tests/hostBridge.test.ts`
- Test: `desktop/web/tests/workbench.test.tsx`
- Test: `desktop/web/tests/monacoDiffViewer.test.ts`
- Test: `desktop/web/tests/xtermViewer.test.ts`

- [ ] Add failing bridge/UI/runtime tests for strict fields, five font choices, authoritative rollback, visible Full Access indicator, and Monaco/xterm option updates.
- [ ] Run focused Vitest cases and confirm expected failures.
- [ ] Implement settings controls and translations, rem-based CSS font sizing, root scale application, and runtime editor font updates with relayout.
- [ ] Re-run focused and full Web tests, TypeScript build, and production bundle.

### Task 4: Integrated verification and release

**Files:**
- Modify: release notes and bilingual installation docs only where behavior changed.
- Generate: x64/ARM64 portable and MSIX release assets, SBOM, license bundle, and SHA256 files.

- [ ] Run all desktop .NET tests, Web tests/build, formatting checks, Rust tests, and both Windows publish targets.
- [ ] Launch the packaged x64 EXE against a real Git workspace and verify approval off/on/off, drag persistence/reset, all font levels, Enter/IME, worktree detection, Monaco, and terminal.
- [ ] Run the real configured provider without printing credentials and verify streaming, cancellation, and clean shutdown.
- [ ] Inspect git diff, commit, push, wait for all CI jobs, download CI package inputs, build signed/checksummed release bundles, and upload a GitHub prerelease.
