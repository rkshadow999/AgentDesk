# AgentDesk Versioned Memory Browser Implementation Plan

> **For AI agent workers:** Implement each checkbox in order with test-driven development and verify every command before reporting completion.

**Goal:** Expose bounded, workspace-scoped Memory browsing and approved mutation over versioned ACP, with a strongly typed C# client.

**Architecture:** `xai-grok-memory` owns opaque target resolution and safe file I/O. `xai-grok-shell` resolves the active session and maps ACP JSON to those operations. `AgentDesk.Core` and `AgentDesk.Engine` expose strict typed models and transport parsing.

**Tech Stack:** Rust, Tokio, ACP JSON extensions, .NET 10, xUnit.

---

### Task 1: Safe Memory Storage Surface

**Files:**
- Modify: `crates/codegen/xai-grok-memory/src/storage.rs`

- [ ] Add failing tests for safe opaque IDs, current-workspace isolation, traversal, symlink/reparse rejection, 64 KiB limits, atomic overwrite, and exact-file deletion.
- [ ] Run `cargo test --locked -p xai-grok-memory storage::tests::test_browsable_memory -- --test-threads=1` and confirm the new tests fail for missing APIs.
- [ ] Add `BrowsableMemoryTarget`, bounded metadata/document types, and secure list/read/write/delete methods.
- [ ] Re-run the targeted tests and the full `xai-grok-memory` package.

### Task 2: Versioned ACP Extension

**Files:**
- Create: `crates/codegen/xai-grok-shell/src/extensions/agentdesk_memory.rs`
- Modify: `crates/codegen/xai-grok-shell/src/extensions/mod.rs`
- Modify: `crates/codegen/xai-grok-shell/src/extensions/agentdesk.rs`
- Modify: `crates/codegen/xai-grok-shell/src/agent/mvp_agent/acp_agent.rs`

- [ ] Add failing unit tests for list/read response schemas, unknown sessions, and mutation responses that require confirmation before I/O.
- [ ] Route `agentdesk/v1/memory/list|read|write|delete` through a focused handler that resolves `SessionHandle.info.cwd`.
- [ ] Return `schemaVersion: 1`; reject malformed IDs/content; run blocking file I/O with `spawn_blocking`.
- [ ] Advertise Memory capabilities from `agentdesk/v1/initialize` and run targeted shell tests.

### Task 3: Strongly Typed Desktop Client

**Files:**
- Create: `desktop/src/AgentDesk.Core/Engine/MemoryManagement.cs`
- Modify: `desktop/src/AgentDesk.Core/Engine/IEngineClient.cs`
- Modify: `desktop/src/AgentDesk.Engine/Acp/AcpEngineClient.Extensions.cs`
- Modify: `desktop/tests/AgentDesk.Engine.Tests/AcpEngineClientExtensionsTests.cs`

- [ ] Add failing xUnit tests for exact request shapes, typed list/read parsing, confirmation-required mutation results, and input/response bounds.
- [ ] Add `MemoryFileId`, `MemoryFileScope`, descriptors, documents, and mutation status/result models.
- [ ] Implement `ListMemoryFilesAsync`, `ReadMemoryFileAsync`, `WriteMemoryFileAsync`, and `DeleteMemoryFileAsync` with strict parsing.
- [ ] Run `dotnet test desktop/tests/AgentDesk.Engine.Tests/AgentDesk.Engine.Tests.csproj --no-restore --disable-build-servers`.

### Task 4: Verification

**Files:**
- Verify all files above; do not modify Workbench.

- [ ] Run full `xai-grok-memory`, focused shell extension tests, C# engine tests, Rust formatting checks, and `git diff --check`.
- [ ] Inspect the final diff for path exposure, unchecked mutation, unbounded payloads, and unrelated changes.
- [ ] Report exact JSON shapes and verification counts without committing.
