# AgentDesk Plan Mode Implementation Plan

[English](2026-07-17-agentdesk-plan-mode.md) | [简体中文](2026-07-17-agentdesk-plan-mode.zh-CN.md)

> **For AI agent workers:** Required subskill: use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task by task. Track progress with the checkboxes below.

**Goal:** Expose the upstream ACP Plan Mode safely in the AgentDesk Windows composer as the first Beta feature.

**Architecture:** Add a shared `SessionMode` type, negotiate supported modes through `agentdesk/v1/initialize`, map mode changes to ACP `session/set_mode`, and make the host the single authority for ordering and confirmed state. The Web UI uses the approved composer-adjacent Execute/Plan segmented control and never sends a prompt after failed mode negotiation.

**Tech Stack:** Rust 1.92, ACP JSON-RPC over NDJSON stdio, .NET 10/C#, WinUI 3/WebView2, React 19, TypeScript, Vitest, xUnit.

---

### Task 1: Add the shared Core session-mode contract

**Files:**
- Create: `desktop/src/AgentDesk.Core/Engine/SessionMode.cs`
- Modify: `desktop/src/AgentDesk.Core/Engine/EngineCapabilities.cs`
- Modify: `desktop/src/AgentDesk.Core/Engine/IEngineClient.cs`
- Create: `desktop/tests/AgentDesk.Core.Tests/SessionModeTests.cs`

- [ ] **Step 1: Write failing Core tests**

```csharp
[Fact]
public void UninitializedCapabilitiesSupportOnlyDefaultMode()
{
    Assert.True(EngineCapabilities.Uninitialized.Supports(SessionMode.Default));
    Assert.False(EngineCapabilities.Uninitialized.Supports(SessionMode.Plan));
}
```

- [ ] **Step 2: Run the focused test and verify red**

Run: `dotnet test desktop/tests/AgentDesk.Core.Tests --filter SessionModeTests`

Expected: compile FAIL because `SessionMode` and `Supports` do not exist.

- [ ] **Step 3: Add the minimum Core API**

```csharp
public enum SessionMode
{
    Default,
    Plan,
}

public Task SetSessionModeAsync(
    SessionId sessionId,
    SessionMode mode,
    CancellationToken cancellationToken = default);
```

`EngineCapabilities` stores a read-only supported-mode set that always contains `Default`.

- [ ] **Step 4: Run the focused and full Core tests**

```powershell
dotnet test desktop/tests/AgentDesk.Core.Tests --filter SessionModeTests
dotnet test desktop/tests/AgentDesk.Core.Tests
```

Expected: PASS.

- [ ] **Step 5: Commit Core types**

```powershell
git add desktop/src/AgentDesk.Core desktop/tests/AgentDesk.Core.Tests
git commit -m "feat(core): define AgentDesk session modes"
```

### Task 2: Advertise Plan Mode from the Rust sidecar

**Files:**
- Modify: `crates/codegen/xai-grok-shell/src/extensions/agentdesk.rs`
- Modify: `crates/codegen/xai-grok-shell/tests/agentdesk_contract.rs`

- [ ] **Step 1: Add a failing capability assertion**

```rust
assert_eq!(
    initialize["sessionModes"],
    serde_json::json!(["default", "plan"])
);
```

- [ ] **Step 2: Run the contract test and verify red**

Run: `cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1`

Expected: FAIL because `sessionModes` is absent.

- [ ] **Step 3: Add the backward-compatible capability**

```rust
pub fn initialize_payload() -> serde_json::Value {
    serde_json::json!({
        "protocolVersion": 1,
        "engine": { "name": "grok-build", "version": env!("CARGO_PKG_VERSION") },
        "sessionModes": ["default", "plan"]
    })
}
```

- [ ] **Step 4: Run the contract test and rustfmt**

```powershell
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
cargo fmt --package xai-grok-shell -- --check
```

Expected: PASS.

- [ ] **Step 5: Commit the sidecar capability**

```powershell
git add crates/codegen/xai-grok-shell/src/extensions/agentdesk.rs crates/codegen/xai-grok-shell/tests/agentdesk_contract.rs
git commit -m "feat(engine): advertise Plan Mode capability"
```

### Task 3: Implement ACP mode negotiation in the .NET Engine

**Files:**
- Modify: `desktop/src/AgentDesk.Engine/Acp/AcpEngineClient.cs`
- Modify: `desktop/tests/AgentDesk.Engine.Tests/AcpEngineClientTests.cs`

- [ ] **Step 1: Write failing initialization and request-shape tests**

The test server returns:

```json
{"protocolVersion":1,"engine":{"name":"grok-build","version":"0.1.0"},"sessionModes":["default","plan"]}
```

Then assert the mode request is exactly:

```json
{"method":"session/set_mode","params":{"sessionId":"session-1","modeId":"plan"}}
```

- [ ] **Step 2: Run focused Engine tests and verify red**

Run: `dotnet test desktop/tests/AgentDesk.Engine.Tests --filter "PlanMode|SetSessionMode"`

Expected: compile or assertion FAIL because the capability response is discarded and no mode method exists.

- [ ] **Step 3: Preserve and parse the extension initialize response**

Replace the boolean probe with a method returning `JsonElement?`. Parse only exact `default` and `plan` strings; unknown entries are ignored, malformed non-array data throws `InvalidDataException`, and absent data yields Default only.

- [ ] **Step 4: Implement the exact ACP request**

```csharp
_ = await _connection.SendRequestAsync(
    "session/set_mode",
    new { sessionId = sessionId.Value, modeId = ModeId(mode) },
    cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 5: Run focused and full Engine tests**

```powershell
dotnet test desktop/tests/AgentDesk.Engine.Tests --filter "PlanMode|SetSessionMode"
dotnet test desktop/tests/AgentDesk.Engine.Tests
```

Expected: PASS, including missing capability and malformed response cases.

- [ ] **Step 6: Commit Engine support**

```powershell
git add desktop/src/AgentDesk.Engine/Acp/AcpEngineClient.cs desktop/tests/AgentDesk.Engine.Tests/AcpEngineClientTests.cs
git commit -m "feat(engine): negotiate ACP session modes"
```

### Task 4: Extend the strict WebView2 protocol

**Files:**
- Modify: `desktop/src/AgentDesk.App/Bridge/WebMessageProtocol.cs`
- Modify: `desktop/tests/AgentDesk.App.Tests/WebMessageProtocolTests.cs`

- [ ] **Step 1: Add failing strict parsing tests**

Valid prompt:

```json
{"schemaVersion":1,"type":"engine/prompt","text":"inspect","executionProfile":"NativeProtected","sessionMode":"plan","nativeRiskAcknowledged":true,"workspaceGeneration":1}
```

Add theory cases for `"Plan"`, `" plan"`, `"plan "`, `1`, `null`, missing field, and `"browser_use"`; every case must throw `InvalidDataException`.

- [ ] **Step 2: Run focused tests and verify red**

Run: `dotnet test desktop/tests/AgentDesk.App.Tests --filter WebMessageProtocolTests`

Expected: valid case cannot expose mode and malformed cases are not rejected.

- [ ] **Step 3: Add exact mode parsing and authoritative event serialization**

```csharp
var sessionMode = RequiredString(root, "sessionMode") switch
{
    "default" => SessionMode.Default,
    "plan" => SessionMode.Plan,
    _ => throw Invalid("The session mode is not supported."),
};
```

Add `SessionModeChangedWebEvent(SessionId, Mode, PlanAvailable)` serialized as `session/mode/changed` with exact lower-case mode.

- [ ] **Step 4: Run focused tests green**

Expected: all protocol tests PASS.

- [ ] **Step 5: Commit protocol changes**

```powershell
git add desktop/src/AgentDesk.App/Bridge/WebMessageProtocol.cs desktop/tests/AgentDesk.App.Tests/WebMessageProtocolTests.cs
git commit -m "feat(app): add strict session mode protocol"
```

### Task 5: Enforce host ordering and stale-event rejection

**Files:**
- Modify: `desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`
- Modify: `desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`

- [ ] **Step 1: Write a failing first-prompt ordering test**

Use the existing recording fake engine and assert:

```csharp
Assert.Equal(
    ["initialize", "authenticate", "new-session", "set-mode:plan", "prompt"],
    fakeEngine.Operations);
```

Add tests proving unsupported Plan or a failed `set-mode` operation never records `prompt`.

- [ ] **Step 2: Run focused tests and verify red**

Run: `dotnet test desktop/tests/AgentDesk.App.Tests --filter "PlanMode|SessionMode"`

Expected: FAIL because the host does not set or track mode.

- [ ] **Step 3: Add confirmed mode state to the host**

Track desired/confirmed mode beside `_sessionId`, clear confirmed mode in `DetachEngineUnsafe`, validate `client.Capabilities.Supports(mode)`, and call `SetSessionModeAsync` after session creation but before the prompt.

- [ ] **Step 4: Parse authoritative mode updates**

When `EngineEvent.UpdateKind == "current_mode_update"`, require an exact string `currentModeId` of `default` or `plan`. Ignore the event unless sender, engine generation, and session match the active state. Publish one idempotent `session/mode/changed` event.

- [ ] **Step 5: Add restart, cancellation, duplicate, and stale-session tests**

Each test must prove no optimistic confirmed mode survives failure or sidecar replacement.

- [ ] **Step 6: Run focused and full App tests**

```powershell
dotnet test desktop/tests/AgentDesk.App.Tests --filter "PlanMode|SessionMode"
dotnet test desktop/tests/AgentDesk.App.Tests
```

Expected: PASS.

- [ ] **Step 7: Commit host state handling**

```powershell
git add desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs
git commit -m "feat(app): enforce Plan Mode prompt ordering"
```

### Task 6: Add the composer-adjacent Execute/Plan segmented control

**Files:**
- Modify: `desktop/web/src/hostBridge.ts`
- Modify: `desktop/web/src/Workbench.tsx`
- Modify: `desktop/web/src/styles.css`
- Modify: `desktop/web/src/locales/en-US.json`
- Modify: `desktop/web/src/locales/zh-CN.json`
- Modify: `desktop/web/tests/hostBridge.test.ts`
- Modify: `desktop/web/tests/workbench.test.tsx`

- [ ] **Step 1: Write failing bridge and UI tests**

Assert prompt commands contain `sessionMode: "plan"`. Render the Workbench and assert a radiogroup named `Session mode`/`会话模式` with `Execute`/`执行` and `Plan`/`计划`; keyboard ArrowLeft/ArrowRight changes the desired mode when idle.

- [ ] **Step 2: Run focused Web tests and verify red**

Run:

```powershell
Set-Location desktop/web
npm test -- tests/hostBridge.test.ts tests/workbench.test.tsx
```

Expected: FAIL because the command and segmented control do not exist.

- [ ] **Step 3: Add strict TypeScript types and event parsing**

```ts
export type SessionMode = "default" | "plan";
```

Prompt commands require `sessionMode`; `session/mode/changed` is accepted only with non-empty session ID, exact mode, and boolean `planAvailable`.

- [ ] **Step 4: Implement the approved segmented control**

Use a fixed-width `role="radiogroup"` containing two icon/text buttons with `role="radio"` and `aria-checked`. Place it before the disabled NativeProtected safety control. Disable mode changes during `starting`, `running`, or pending confirmation.

- [ ] **Step 5: Keep mode and safety acknowledgement independent**

Changing mode must not set `nativeRiskAcknowledged`. The existing risk dialog still appears before the first native prompt in either mode.

- [ ] **Step 6: Run focused and complete Web tests**

```powershell
npm test -- tests/hostBridge.test.ts tests/workbench.test.tsx
npm test
npm run build
```

Expected: PASS with no console warnings.

- [ ] **Step 7: Commit the Web UI**

```powershell
git add desktop/web
git commit -m "feat(web): add Execute and Plan mode control"
```

### Task 7: Verify, document, review, and push the Beta slice

**Files:**
- Modify: `README.md`
- Modify: `README.zh-CN.md`
- Modify: `docs/ROADMAP.md`
- Modify: `docs/ROADMAP.zh-CN.md`
- Modify: `desktop/README.md`
- Modify: `desktop/README.zh-CN.md`

- [ ] **Step 1: Document Plan Mode as Beta, not Alpha**

Describe capability negotiation, the Execute/Plan control, unsupported-engine behavior, and the unchanged native execution warning in both languages.

- [ ] **Step 2: Run complete serial verification**

```powershell
Set-Location desktop/web
npm test
npm run build
Set-Location ../..
dotnet test desktop/AgentDesk.sln --no-restore
dotnet format desktop/AgentDesk.sln --verify-no-changes --no-restore
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
cargo fmt --package xai-grok-shell -- --check
pwsh ./scripts/agentdesk/Test-AgentDeskReleaseScripts.ps1
pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1
git diff --check
```

Expected: all commands exit 0.

- [ ] **Step 3: Run real-browser acceptance**

Verify default Execute state, keyboard mode switching, pending/running disabled state, Plan prompt ordering, native risk dialog independence, 125%-200% zoom, and zero console errors/warnings. Save the screenshot under `output/playwright/` without committing generated output.

- [ ] **Step 4: Request independent code review**

Resolve every Critical and Important finding, then rerun affected tests and the full verification gate.

- [ ] **Step 5: Push the Beta branch**

```powershell
git push -u origin feature/agentdesk-beta-plan-mode
```

Expected: the branch is visible in `rkshadow/AgentDesk` and CI starts. Do not merge until CI and review pass.
