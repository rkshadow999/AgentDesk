[English](2026-07-16-agentdesk-permission-bridge.md) | [简体中文](2026-07-16-agentdesk-permission-bridge.zh-CN.md)

# AgentDesk ACP Permission Approval Loop Implementation Plan

> **For the AI agent implementing this plan:** Work in the existing `feature/agentdesk-alpha` worktree and follow TDD order. Do not modify `MainWindow.xaml.cs` or XAML.

**Goal:** Complete a real, fail-closed approval loop for ACP `session/request_permission`, from the sidecar through Core, the host protocol, and the React interface.

**Architecture:** The NDJSON connection dispatches reverse requests containing both `id` and `method` to tracked independent tasks so the reader can continue processing messages. `AcpEngineClient` parses requests into Core types and owns their pending completions. `HostController` only forwards requests and user decisions. React presents one approval dialog at a time through a FIFO queue.

**Technology:** .NET 10, System.Text.Json, xUnit, React 19, TypeScript, Vitest, and Testing Library.

---

### Task 1: Reverse JSON-RPC requests

**Files:**
- Modify: `desktop/src/AgentDesk.Engine/Transport/NdjsonRpcConnection.cs`
- Create: `desktop/src/AgentDesk.Engine/Transport/JsonRpcRequest.cs`
- Modify: `desktop/tests/AgentDesk.Engine.Tests/NdjsonRpcConnectionTests.cs`

- [ ] Write tests first: reverse requests return a result; the reader keeps processing notifications and responses while a reply is pending; a missing handler returns `-32601`; disposing the connection cancels and awaits handler tasks.
- [ ] Run focused Engine tests and confirm they fail because the reverse-request API is missing.
- [ ] Implement tracked, cancellable request-handler tasks and serialize response writes.
- [ ] Re-run the focused tests and keep all existing tests green.

### Task 2: Core permission contract and ACP mapping

**Files:**
- Create: `desktop/src/AgentDesk.Core/Engine/PermissionDecision.cs`
- Create: `desktop/src/AgentDesk.Core/Engine/PermissionRequest.cs`
- Create: `desktop/src/AgentDesk.Core/Engine/PermissionOption.cs`
- Modify: `desktop/src/AgentDesk.Core/Engine/IEngineClient.cs`
- Modify: `desktop/src/AgentDesk.Engine/Acp/AcpEngineClient.cs`
- Modify: `desktop/tests/AgentDesk.Engine.Tests/AcpEngineClientTests.cs`

- [ ] Write tests first: parse the real ACP 0.10.4 shape; return selected and cancelled responses; support concurrent requests; cancel all pending requests for a session; fail closed for an unknown option or when there is no subscriber.
- [ ] Run focused Engine tests and confirm they fail because the contract and behavior are missing.
- [ ] Implement Core types, the pending-request dictionary, event projection, and cancellation/disposal cleanup.
- [ ] Re-run the complete Engine test suite.

### Task 3: Web protocol and host controller

**Files:**
- Modify: `desktop/src/AgentDesk.App/Bridge/WebMessageProtocol.cs`
- Modify: `desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`
- Modify: `desktop/tests/AgentDesk.App.Tests/WebMessageProtocolTests.cs`
- Modify: `desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`

- [ ] Write tests first: camelCase `permission/requested` events; selected and cancelled `permission/respond` commands; HostController forwarding; unknown requests never produce a false approval.
- [ ] Run focused App tests and confirm they fail because the new types and branches are missing.
- [ ] Connect `IEngineClient` permission events and response methods, and unsubscribe when replacing or disposing the engine.
- [ ] Re-run the complete App test suite.

### Task 4: React approval queue

**Files:**
- Modify: `desktop/web/src/hostBridge.ts`
- Modify: `desktop/web/src/Workbench.tsx`
- Modify: `desktop/web/src/styles.css`
- Modify: `desktop/web/src/locales/zh-CN.json`
- Modify: `desktop/web/src/locales/en-US.json`
- Modify: `desktop/web/tests/hostBridge.test.ts`
- Modify: `desktop/web/tests/workbench.test.tsx`

- [ ] Write tests first: parse request events; send selected and cancelled decisions; queue concurrent requests in FIFO order; cancel with Escape; make the background inert; restore focus after the final dialog closes.
- [ ] Run focused Web tests and confirm they fail because the UI does not exist yet.
- [ ] Implement the approval dialog and queue state, displaying only the real options supplied by ACP.
- [ ] Run `npm test` and `npm run build`.

### Task 5: Verification

- [ ] Run the complete Core, Engine, App, and Platform.Windows test suites.
- [ ] Run the complete Web test suite and production build.
- [ ] Run `dotnet build` and `git diff --check`, and record any local Rust/MSVC limitation.
