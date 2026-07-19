[English](2026-07-16-agentdesk-permission-bridge.md) | [简体中文](2026-07-16-agentdesk-permission-bridge.zh-CN.md)

# AgentDesk ACP 权限审批闭环实现计划

> **面向 AI 代理的工作者：** 在现有 `feature/agentdesk-alpha` worktree 中按 TDD 顺序执行本计划，不修改 `MainWindow.xaml.cs` 或 XAML。

**目标：** 让 ACP `session/request_permission` 从 sidecar 经 Core、宿主协议和 React 中文界面完成真实、失败关闭的审批闭环。

**架构：** NDJSON 连接把带 `id` 和 `method` 的反向请求投递到受跟踪的独立任务，保持 reader 可继续处理消息。`AcpEngineClient` 将请求解析为 Core 类型并持有 pending completion；HostController 只转发请求和用户决定；React 使用 FIFO 队列一次显示一个审批对话框。

**技术栈：** .NET 10、System.Text.Json、xUnit、React 19、TypeScript、Vitest、Testing Library。

---

### 任务 1：反向 JSON-RPC 请求

**文件：**
- 修改：`desktop/src/AgentDesk.Engine/Transport/NdjsonRpcConnection.cs`
- 创建：`desktop/src/AgentDesk.Engine/Transport/JsonRpcRequest.cs`
- 修改：`desktop/tests/AgentDesk.Engine.Tests/NdjsonRpcConnectionTests.cs`

- [ ] 先写测试：反向请求返回 result，等待响应时 reader 仍处理通知/响应，无处理器返回 `-32601`，释放连接会取消并等待处理任务。
- [ ] 运行 Engine 定向测试，确认因缺少反向请求 API 失败。
- [ ] 实现受跟踪、可取消的请求处理任务和串行 writer 回包。
- [ ] 重跑定向测试并保持已有测试全绿。

### 任务 2：Core 权限契约与 ACP 映射

**文件：**
- 创建：`desktop/src/AgentDesk.Core/Engine/PermissionDecision.cs`
- 创建：`desktop/src/AgentDesk.Core/Engine/PermissionRequest.cs`
- 创建：`desktop/src/AgentDesk.Core/Engine/PermissionOption.cs`
- 修改：`desktop/src/AgentDesk.Core/Engine/IEngineClient.cs`
- 修改：`desktop/src/AgentDesk.Engine/Acp/AcpEngineClient.cs`
- 修改：`desktop/tests/AgentDesk.Engine.Tests/AcpEngineClientTests.cs`

- [ ] 先写测试：解析真实 ACP 0.10.4 shape、selected/cancelled 回包、并行请求、取消 session 全部 pending、未知 option 与无订阅失败关闭。
- [ ] 运行 Engine 定向测试，确认契约和行为缺失导致失败。
- [ ] 实现 Core 类型、pending 字典、事件投影和取消/释放清理。
- [ ] 重跑 Engine 全量测试。

### 任务 3：Web 协议与宿主控制器

**文件：**
- 修改：`desktop/src/AgentDesk.App/Bridge/WebMessageProtocol.cs`
- 修改：`desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`
- 修改：`desktop/tests/AgentDesk.App.Tests/WebMessageProtocolTests.cs`
- 修改：`desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`

- [ ] 先写测试：`permission/requested` camelCase 事件、`permission/respond` selected/cancelled 命令、HostController 转发且未知请求不产生虚假批准。
- [ ] 运行 App 定向测试，确认新类型和分支缺失导致失败。
- [ ] 接线 IEngineClient 权限事件与响应方法，并在切换/释放引擎时解除订阅。
- [ ] 重跑 App 全量测试。

### 任务 4：React 中文审批队列

**文件：**
- 修改：`desktop/web/src/hostBridge.ts`
- 修改：`desktop/web/src/Workbench.tsx`
- 修改：`desktop/web/src/styles.css`
- 修改：`desktop/web/src/locales/zh-CN.json`
- 修改：`desktop/web/src/locales/en-US.json`
- 修改：`desktop/web/tests/hostBridge.test.ts`
- 修改：`desktop/web/tests/workbench.test.tsx`

- [ ] 先写测试：解析请求事件、发送 selected/cancelled、FIFO 并行请求、Escape 取消、背景 inert、最终焦点恢复。
- [ ] 运行 Web 定向测试，确认 UI 尚不存在而失败。
- [ ] 实现只展示真实 ACP options 的中文模态框和队列状态。
- [ ] 运行 `npm test` 与 `npm run build`。

### 任务 5：验证

- [ ] 运行 Core、Engine、App、Platform.Windows 全量测试。
- [ ] 运行 Web 全量测试与生产构建。
- [ ] 运行 `dotnet build`、`git diff --check`，记录本机 Rust/MSVC 限制。
