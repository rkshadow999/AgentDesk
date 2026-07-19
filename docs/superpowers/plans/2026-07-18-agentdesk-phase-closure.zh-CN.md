# AgentDesk 阶段收尾实施计划

[English](2026-07-18-agentdesk-phase-closure.md) | [简体中文](2026-07-18-agentdesk-phase-closure.zh-CN.md)

> **面向 AI 代理的工作者：** 每个任务使用测试驱动开发，并继续使用 `feature/agentdesk-alpha` linked worktree。Rust 正式发布构建统一由发布审计代理串行执行。

**目标：** 补齐 AgentDesk 四阶段剩余的用户入口，使用用户提供的 OpenAI 兼容服务真实验收 Windows 客户端，并发布可供二次开发的双语公开仓库。

**架构：** Rust 继续作为 ACP sidecar 和会话权威；所有新操作通过强类型 C# 契约进入现有串行宿主状态。原生文件选择、通知、备份、更新器启动和凭据持久化留在 WinUI/Windows 层；WebView2 只接收有界且不含秘密的状态。可选云端复用 `AgentDesk.Cloud.Client` 的加密工作流，本地使用永远不依赖云端账号。

**技术栈：** .NET 10、WinUI 3、WebView2、React/Vite/Vitest、xUnit、Rust ACP、Windows Credential Manager、SQLite、GitHub Actions。

---

### 任务 1：桌面维护与会话传输

**文件：**
- 创建：`desktop/src/AgentDesk.App/Maintenance/AgentDeskMaintenanceCoordinator.cs`
- 创建：`desktop/src/AgentDesk.App/Maintenance/DesktopFileDialogRequest.cs`
- 修改：`desktop/src/AgentDesk.App/AgentDesk.App.csproj`
- 修改：`desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`
- 修改：`desktop/src/AgentDesk.App/Bridge/WebCommandDispatcher.cs`
- 修改：`desktop/src/AgentDesk.App/Bridge/WebMessageProtocol.cs`
- 修改：`desktop/src/AgentDesk.App/MainWindow.xaml.cs`
- 修改：`desktop/web/src/Workbench.tsx`
- 修改：`desktop/web/src/locales/en-US.json`
- 修改：`desktop/web/src/locales/zh-CN.json`
- 测试：`desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`
- 测试：`desktop/tests/AgentDesk.App.Tests/WebCommandDispatcherTests.cs`
- 测试：`desktop/tests/AgentDesk.App.Tests/WebMessageProtocolTests.cs`
- 测试：`desktop/web/tests/workbench.test.tsx`

- [x] 为 `session/export`、`session/import`、`backup/create`、`backup/restore`、`update/check` 和 `update/apply` 增加失败协议测试；拒绝未知字段、超长路径和任务运行期间的维护操作。
- [x] 增加失败宿主测试，证明引擎导入导出能往返有界 UTF-8 JSON、导入创建新会话、恢复备份前停止 sidecar、应用更新交给独立更新器进程。
- [x] 在 `AgentDeskBackupService`、`PortableUpdateService` 和独立 updater 之上实现协调器。不得在运行中的应用进程内替换自身安装目录。
- [x] 增加用 WinUI 窗口句柄初始化的原生打开/保存选择器；用户取消时产生中性事件，不能虚构路径。
- [x] 增加双语设置入口和进度/错误事件。会话文档、API Key、恢复密钥和文件正文不得进入 WebView2 状态。
- [x] 运行聚焦 App/Web 测试，再运行完整 App/Web 测试。

### 任务 2：Worktree 与代码审查生命周期

**文件：**
- 修改：`desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`
- 修改：`desktop/src/AgentDesk.App/Bridge/WebMessageProtocol.cs`
- 修改：`desktop/web/src/Workbench.tsx`
- 修改：`desktop/web/src/locales/en-US.json`
- 修改：`desktop/web/src/locales/zh-CN.json`
- 测试：`desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`
- 测试：`desktop/tests/AgentDesk.App.Tests/WebMessageProtocolTests.cs`
- 测试：`desktop/web/tests/workbench.test.tsx`

- [x] 为 create/list/show/apply/remove/gc 命令和 generation 安全结果事件增加失败测试。
- [x] 将现有强类型 `IEngineClient` worktree 方法接入宿主，不削弱路径、ID、ref、数量或文本限制。
- [x] 增加有界 worktree 面板，明确基础 ref、应用确认、保留/删除状态，并提供仍受正常权限约束的代码审查命令。
- [x] 运行 Engine、App、Web 的 worktree 测试与格式检查。

### 任务 3：可选端到端加密云桌面工作流

**文件：**
- 创建：`desktop/src/AgentDesk.App/Cloud/AgentDeskCloudDesktopService.cs`
- 创建：`desktop/src/AgentDesk.App/Cloud/CloudDesktopState.cs`
- 修改：`desktop/src/AgentDesk.App/AgentDesk.App.csproj`
- 修改：`desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`
- 修改：`desktop/src/AgentDesk.App/Bridge/WebMessageProtocol.cs`
- 修改：`desktop/web/src/Workbench.tsx`
- 修改：`desktop/web/src/locales/en-US.json`
- 修改：`desktop/web/src/locales/zh-CN.json`
- 测试：`desktop/tests/AgentDesk.App.Tests/AgentDeskCloudDesktopServiceTests.cs`
- 测试：`desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`
- 测试：`desktop/web/tests/workbench.test.tsx`

- [x] 为默认纯本地、HTTPS 端点校验、Credential Manager Token、恢复密钥、上传/下载/导入、接力接收后确认、Automation 列表/创建/禁用、Runner 注册/任务流和团队策略投影增加失败测试。
- [x] 将 `JsonCloudConnectionProfileStore`、`CredentialCloudAccessTokenProvider`、`CredentialRecoveryKeyStore`、`SqliteCloudSyncMetadataStore`、`AgentDeskCloudClient` 与 `EngineCloudSessionWorkflow` 组合为单一可释放桌面服务。
- [x] Token 与恢复密钥不得进入 JSON、Web 事件、异常文本和日志。云端保持 opt-in，纯本地配置下所有本地命令照常工作。
- [x] 增加双语云设置区，覆盖同步、接力、Runner、Automation 和团队策略；远程执行与 UI Automation 标记为受策略控制的实验能力。
- [x] 运行 Cloud.Client 单元/集成测试、App 测试与 Web 测试。

### 任务 4：通知与明确限定范围的 Windows Automation 实验

**文件：**
- 创建：`desktop/src/AgentDesk.App/Notifications/IUserNotificationService.cs`
- 创建：`desktop/src/AgentDesk.App/Notifications/WindowsUserNotificationService.cs`
- 创建：`desktop/src/AgentDesk.App/Automation/WindowsAutomationPolicy.cs`
- 修改：`desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`
- 修改：`desktop/src/AgentDesk.App/Settings/UiPreferences.cs`
- 修改：`desktop/web/src/Workbench.tsx`
- 修改：`desktop/web/src/locales/en-US.json`
- 修改：`desktop/web/src/locales/zh-CN.json`
- 测试：`desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`
- 测试：`desktop/tests/AgentDesk.App.Tests/JsonUiPreferencesStoreTests.cs`
- 测试：`desktop/web/tests/workbench.test.tsx`

- [x] 增加失败测试，确保通知只含任务状态和会话 ID、默认关闭，并且绝不包含提示词、文件或终端正文。
- [x] 增加默认关闭的 Automation 策略：必须同时有本地显式开关；启用云策略时还要获得团队许可；任何操作都不能绕过正常权限队列。
- [x] 在界面明确展示中断、范围和审计警告，绝不静默启用 Windows UI 控制。
- [x] 运行 App 与 Web 无障碍聚焦测试。

### 任务 5：发布事实、全量验证与公开仓库

**文件：**
- 修改：`README.md`、`README.zh-CN.md`
- 修改：`docs/ROADMAP.md`、`docs/ROADMAP.zh-CN.md`
- 修改：`docs/ARCHITECTURE.md`、`docs/ARCHITECTURE.zh-CN.md`
- 修改：`docs/BUILD-AND-TEST.md`、`docs/BUILD-AND-TEST.zh-CN.md`
- 修改：`.github/workflows/agentdesk-windows.yml`
- 仅在最终验证暴露发布契约缺口时修改：`scripts/agentdesk/*`

- [x] 只有对应桌面工作流有通过测试和真实证据后，才更新滞后的阶段状态。
- [x] 运行 Web 测试/构建、全部 .NET 单元和集成项目、聚焦 Rust ACP/会话测试、仓库/发布/更新脚本、格式、空白、秘密和大文件扫描。
- [ ] 构建唯一经过审计的 `release-dist` sidecar，执行 8 MiB PE 栈门禁，并生成 x64/ARM64 Portable/MSIX 输入、SBOM、声明、校验和、回滚材料和签名更新元数据。没有真实代码签名证书时不得声称 MSIX 已签名。
- [ ] 使用应用内 Browser 测试本地 Web 界面，使用 Windows UI 自动化测试真实 WinUI 应用的缩放和纯键盘路径。
- [x] 只把用户提供的 Provider Secret 注入真实进程，验证 `/models`、流式提示、取消、错误处理和无密钥泄漏，然后清除进程范围 Secret。
- [ ] 创建 `rkshadow999/AgentDesk` 公开仓库，不打印地把更新私钥写入 GitHub Actions Secret，删除临时密钥目录，推送 `main`，开启 Issues/Discussions/私密漏洞报告/Topics/分支保护，并检查首次 Actions。
