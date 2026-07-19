# AgentDesk Plan Mode 实施计划

[English](2026-07-17-agentdesk-plan-mode.md) | [简体中文](2026-07-17-agentdesk-plan-mode.zh-CN.md)

> **面向 AI 代理的工作者：** 必需子技能：使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 逐任务实施本计划。使用下方复选框跟踪进度。

**目标：** 将上游 ACP Plan Mode 安全接入 AgentDesk Windows 输入区，作为第一个 Beta 功能。

**架构：** 增加共享 `SessionMode` 类型，通过 `agentdesk/v1/initialize` 协商支持模式，把模式切换映射到 ACP `session/set_mode`，并让宿主成为调用顺序和确认状态的唯一权威。Web 使用已批准的输入区相邻“执行/计划”分段控件；模式协商失败后绝不发送 prompt。

**技术栈：** Rust 1.92、ACP JSON-RPC/NDJSON stdio、.NET 10/C#、WinUI 3/WebView2、React 19、TypeScript、Vitest、xUnit。

---

### 任务 1：增加共享 Core 会话模式契约

**文件：**
- 创建：`desktop/src/AgentDesk.Core/Engine/SessionMode.cs`
- 修改：`desktop/src/AgentDesk.Core/Engine/EngineCapabilities.cs`
- 修改：`desktop/src/AgentDesk.Core/Engine/IEngineClient.cs`
- 创建：`desktop/tests/AgentDesk.Core.Tests/SessionModeTests.cs`

- [ ] **步骤 1：先写失败的 Core 测试**

```csharp
[Fact]
public void UninitializedCapabilitiesSupportOnlyDefaultMode()
{
    Assert.True(EngineCapabilities.Uninitialized.Supports(SessionMode.Default));
    Assert.False(EngineCapabilities.Uninitialized.Supports(SessionMode.Plan));
}
```

- [ ] **步骤 2：运行定向测试并验证红灯**

运行：`dotnet test desktop/tests/AgentDesk.Core.Tests --filter SessionModeTests`

预期：编译 FAIL，因为 `SessionMode` 和 `Supports` 不存在。

- [ ] **步骤 3：增加最小 Core API**

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

`EngineCapabilities` 保存只读模式集合，并始终包含 `Default`。

- [ ] **步骤 4：运行定向与完整 Core 测试**

```powershell
dotnet test desktop/tests/AgentDesk.Core.Tests --filter SessionModeTests
dotnet test desktop/tests/AgentDesk.Core.Tests
```

预期：PASS。

- [ ] **步骤 5：提交 Core 类型**

```powershell
git add desktop/src/AgentDesk.Core desktop/tests/AgentDesk.Core.Tests
git commit -m "feat(core): define AgentDesk session modes"
```

### 任务 2：由 Rust sidecar 声明 Plan Mode

**文件：**
- 修改：`crates/codegen/xai-grok-shell/src/extensions/agentdesk.rs`
- 修改：`crates/codegen/xai-grok-shell/tests/agentdesk_contract.rs`

- [ ] **步骤 1：增加失败的能力断言**

```rust
assert_eq!(
    initialize["sessionModes"],
    serde_json::json!(["default", "plan"])
);
```

- [ ] **步骤 2：运行契约测试并确认红灯**

运行：`cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1`

预期：FAIL，因为缺少 `sessionModes`。

- [ ] **步骤 3：增加向后兼容能力**

```rust
pub fn initialize_payload() -> serde_json::Value {
    serde_json::json!({
        "protocolVersion": 1,
        "engine": { "name": "grok-build", "version": env!("CARGO_PKG_VERSION") },
        "sessionModes": ["default", "plan"]
    })
}
```

- [ ] **步骤 4：运行契约测试与 rustfmt**

```powershell
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
cargo fmt --package xai-grok-shell -- --check
```

预期：PASS。

- [ ] **步骤 5：提交 sidecar 能力**

```powershell
git add crates/codegen/xai-grok-shell/src/extensions/agentdesk.rs crates/codegen/xai-grok-shell/tests/agentdesk_contract.rs
git commit -m "feat(engine): advertise Plan Mode capability"
```

### 任务 3：在 .NET Engine 中实现 ACP 模式协商

**文件：**
- 修改：`desktop/src/AgentDesk.Engine/Acp/AcpEngineClient.cs`
- 修改：`desktop/tests/AgentDesk.Engine.Tests/AcpEngineClientTests.cs`

- [ ] **步骤 1：编写失败的初始化与请求形状测试**

测试服务返回：

```json
{"protocolVersion":1,"engine":{"name":"grok-build","version":"0.1.0"},"sessionModes":["default","plan"]}
```

然后精确断言：

```json
{"method":"session/set_mode","params":{"sessionId":"session-1","modeId":"plan"}}
```

- [ ] **步骤 2：运行定向 Engine 测试并验证红灯**

运行：`dotnet test desktop/tests/AgentDesk.Engine.Tests --filter "PlanMode|SetSessionMode"`

预期：编译或断言 FAIL，因为当前丢弃能力响应且没有模式方法。

- [ ] **步骤 3：保留并解析扩展初始化响应**

用返回 `JsonElement?` 的方法替换布尔探测。只解析精确 `default`/`plan`；忽略未知项，非数组畸形数据抛出 `InvalidDataException`，缺失数据只支持 Default。

- [ ] **步骤 4：实现精确 ACP 请求**

```csharp
_ = await _connection.SendRequestAsync(
    "session/set_mode",
    new { sessionId = sessionId.Value, modeId = ModeId(mode) },
    cancellationToken).ConfigureAwait(false);
```

- [ ] **步骤 5：运行定向与完整 Engine 测试**

```powershell
dotnet test desktop/tests/AgentDesk.Engine.Tests --filter "PlanMode|SetSessionMode"
dotnet test desktop/tests/AgentDesk.Engine.Tests
```

预期：PASS，覆盖缺失能力和畸形响应。

- [ ] **步骤 6：提交 Engine 支持**

```powershell
git add desktop/src/AgentDesk.Engine/Acp/AcpEngineClient.cs desktop/tests/AgentDesk.Engine.Tests/AcpEngineClientTests.cs
git commit -m "feat(engine): negotiate ACP session modes"
```

### 任务 4：扩展严格 WebView2 协议

**文件：**
- 修改：`desktop/src/AgentDesk.App/Bridge/WebMessageProtocol.cs`
- 修改：`desktop/tests/AgentDesk.App.Tests/WebMessageProtocolTests.cs`

- [ ] **步骤 1：增加失败的严格解析测试**

合法 prompt：

```json
{"schemaVersion":1,"type":"engine/prompt","text":"inspect","executionProfile":"NativeProtected","sessionMode":"plan","nativeRiskAcknowledged":true,"workspaceGeneration":1}
```

增加 `"Plan"`、`" plan"`、`"plan "`、`1`、`null`、缺失字段、`"browser_use"` 理论用例，全部必须抛出 `InvalidDataException`。

- [ ] **步骤 2：运行定向测试并确认红灯**

运行：`dotnet test desktop/tests/AgentDesk.App.Tests --filter WebMessageProtocolTests`

预期：合法用例无法取得模式，畸形用例没有被拒绝。

- [ ] **步骤 3：增加精确模式解析与权威事件序列化**

```csharp
var sessionMode = RequiredString(root, "sessionMode") switch
{
    "default" => SessionMode.Default,
    "plan" => SessionMode.Plan,
    _ => throw Invalid("The session mode is not supported."),
};
```

增加 `SessionModeChangedWebEvent(SessionId, Mode, PlanAvailable)`，序列化为 `session/mode/changed` 和精确小写 mode。

- [ ] **步骤 4：运行定向测试绿灯**

预期：全部协议测试 PASS。

- [ ] **步骤 5：提交协议改动**

```powershell
git add desktop/src/AgentDesk.App/Bridge/WebMessageProtocol.cs desktop/tests/AgentDesk.App.Tests/WebMessageProtocolTests.cs
git commit -m "feat(app): add strict session mode protocol"
```

### 任务 5：强制宿主调用顺序并拒绝旧事件

**文件：**
- 修改：`desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`
- 修改：`desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`

- [ ] **步骤 1：编写失败的首次 prompt 顺序测试**

使用现有记录型假引擎并断言：

```csharp
Assert.Equal(
    ["initialize", "authenticate", "new-session", "set-mode:plan", "prompt"],
    fakeEngine.Operations);
```

再证明不支持 Plan 或 `set-mode` 失败时绝不记录 `prompt`。

- [ ] **步骤 2：运行定向测试并确认红灯**

运行：`dotnet test desktop/tests/AgentDesk.App.Tests --filter "PlanMode|SessionMode"`

预期：FAIL，因为宿主没有设置或跟踪模式。

- [ ] **步骤 3：在宿主中增加确认模式状态**

在 `_sessionId` 附近跟踪期望/确认模式，在 `DetachEngineUnsafe` 清除确认状态，验证 `client.Capabilities.Supports(mode)`，并在创建会话后、prompt 前调用 `SetSessionModeAsync`。

- [ ] **步骤 4：解析权威模式更新**

当 `EngineEvent.UpdateKind == "current_mode_update"` 时，要求精确字符串 `currentModeId` 为 `default` 或 `plan`。除非 sender、engine generation 和 session 都匹配活动状态，否则忽略。发布一个幂等 `session/mode/changed`。

- [ ] **步骤 5：增加重启、取消、重复和旧 session 测试**

每个测试都证明失败或 sidecar 替换后不会保留乐观确认状态。

- [ ] **步骤 6：运行定向与完整 App 测试**

```powershell
dotnet test desktop/tests/AgentDesk.App.Tests --filter "PlanMode|SessionMode"
dotnet test desktop/tests/AgentDesk.App.Tests
```

预期：PASS。

- [ ] **步骤 7：提交宿主状态处理**

```powershell
git add desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs
git commit -m "feat(app): enforce Plan Mode prompt ordering"
```

### 任务 6：增加输入区相邻“执行/计划”分段控件

**文件：**
- 修改：`desktop/web/src/hostBridge.ts`
- 修改：`desktop/web/src/Workbench.tsx`
- 修改：`desktop/web/src/styles.css`
- 修改：`desktop/web/src/locales/en-US.json`
- 修改：`desktop/web/src/locales/zh-CN.json`
- 修改：`desktop/web/tests/hostBridge.test.ts`
- 修改：`desktop/web/tests/workbench.test.tsx`

- [ ] **步骤 1：编写失败的 bridge 与 UI 测试**

断言 prompt 命令包含 `sessionMode: "plan"`。渲染 Workbench 后断言存在名为 `Session mode`/`会话模式` 的 radiogroup，包含 `Execute`/`执行` 与 `Plan`/`计划`；空闲时 ArrowLeft/ArrowRight 可改变期望模式。

- [ ] **步骤 2：运行定向 Web 测试并确认红灯**

```powershell
Set-Location desktop/web
npm test -- tests/hostBridge.test.ts tests/workbench.test.tsx
```

预期：FAIL，因为命令字段与分段控件不存在。

- [ ] **步骤 3：增加严格 TypeScript 类型与事件解析**

```ts
export type SessionMode = "default" | "plan";
```

Prompt 命令必须携带 `sessionMode`；只有 session ID 非空、mode 精确且 `planAvailable` 为布尔值时才接受 `session/mode/changed`。

- [ ] **步骤 4：实现已批准的分段控件**

使用固定宽度 `role="radiogroup"`，内部两个图标/文字按钮使用 `role="radio"` 和 `aria-checked`。放在禁用的 NativeProtected 安全控件之前。`starting`、`running` 或等待确认时禁用切换。

- [ ] **步骤 5：保持模式与安全确认独立**

切换模式不得设置 `nativeRiskAcknowledged`。两种模式下首次本机 prompt 都继续显示现有风险对话框。

- [ ] **步骤 6：运行定向与完整 Web 测试**

```powershell
npm test -- tests/hostBridge.test.ts tests/workbench.test.tsx
npm test
npm run build
```

预期：PASS 且无控制台警告。

- [ ] **步骤 7：提交 Web UI**

```powershell
git add desktop/web
git commit -m "feat(web): add Execute and Plan mode control"
```

### 任务 7：验证、文档化、复审并推送 Beta 切片

**文件：**
- 修改：`README.md`
- 修改：`README.zh-CN.md`
- 修改：`docs/ROADMAP.md`
- 修改：`docs/ROADMAP.zh-CN.md`
- 修改：`desktop/README.md`
- 修改：`desktop/README.zh-CN.md`

- [ ] **步骤 1：把 Plan Mode 记录为 Beta，而非 Alpha**

中英文说明能力协商、执行/计划控件、不支持引擎的行为，以及不变的本机执行警告。

- [ ] **步骤 2：运行完整串行验证**

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

预期：全部命令退出 0。

- [ ] **步骤 3：运行真实浏览器验收**

验证默认执行状态、键盘切换、等待/运行禁用状态、Plan prompt 顺序、本机风险对话框独立性、125%-200% 缩放和零控制台错误/警告。截图保存到 `output/playwright/`，不提交生成输出。

- [ ] **步骤 4：请求独立代码复审**

修复全部 Critical/Important，然后重跑受影响测试与完整门禁。

- [ ] **步骤 5：推送 Beta 分支**

```powershell
git push -u origin feature/agentdesk-beta-plan-mode
```

预期：分支出现在 `rkshadow999/AgentDesk` 并触发 CI；CI 与复审通过前不合并。
