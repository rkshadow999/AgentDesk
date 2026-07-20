# AgentDesk 完全访问、布局与字体控制实施计划

> **面向 AI 代理的工作者：** 使用子智能体驱动开发或在主线逐项执行，并用复选框跟踪红灯、绿灯和验证状态。

**目标：** 在 Windows 成品客户端中交付原生确认的完全访问、持久化可调检查器和持久化字体缩放。

**架构：** 完全访问和字体进入权威 UI 偏好快照；检查器几何信息由独立原生存储负责；Host 只自动处理 ACP 工具审批。两个 WebView 投影同一字号偏好，WinUI 独占原生列宽。

**技术栈：** .NET 10、WinUI 3、WebView2、React 19、TypeScript、Monaco、xterm.js、xUnit、Vitest。

---

### 任务 1：偏好协议与完全访问

- [ ] 在 `JsonUiPreferencesStoreTests.cs`、`WebMessageProtocolTests.cs` 和 `AgentDeskHostControllerTests.cs` 先加入失败测试：schema 4 往返、旧版本迁移、五档字号、原生确认、`AllowOnce` 自动响应以及失败回退。
- [ ] 运行聚焦 xUnit，确认失败来自能力尚未实现。
- [ ] 修改 `UiPreferences.cs`、`JsonUiPreferencesStore.cs`、`WebMessageProtocol.cs`、`AgentDeskHostController.cs` 和 `MainWindow.xaml.cs`，加入严格字段、原生确认与不污染引擎状态的自动审批。
- [ ] 重跑聚焦测试至零失败。

### 任务 2：原生检查器调整

- [ ] 新建 `InspectorPaneLayoutTests.cs`、`JsonWindowLayoutStoreTests.cs` 并扩展 `WebSurfacePolicyTests.cs`，先验证缺失实现会失败。
- [ ] 新建 `InspectorPaneLayout.cs`、`WindowLayoutState.cs`、`JsonWindowLayoutStore.cs`，修改 `MainWindow.xaml/.cs` 和中英文 `.resw`。
- [ ] 实现动态钳制、首选宽度恢复、拖动、方向键、Shift 加速、双击/Enter/Space 复位、模态禁用、原子保存和关闭刷新。
- [ ] 重跑聚焦测试并完成 x64 WinUI XAML 构建。

### 任务 3：Web 设置与全局字号

- [ ] 在 `hostBridge.test.ts`、`workbench.test.tsx`、Monaco/xterm 测试中先加入严格协议、五档字号、权威回退和完全访问状态标识的失败测试。
- [ ] 修改 `hostBridge.ts`、`Workbench.tsx`、`InspectorSurface.tsx`、运行时查看器、`styles.css` 和中英文 JSON。
- [ ] 将 CSS 字号 rem 化，立即应用根字号，并同步更新 Monaco/xterm 后重新布局。
- [ ] 运行聚焦及全部 Vitest、TypeScript 和生产构建。

### 任务 4：集成验证与发布

- [ ] 运行全部桌面 .NET、Web、格式、Rust 和双架构 Windows 发布构建。
- [ ] 用打包后的 x64 EXE 在真实 Git 工作区验证审批关/开/关、侧栏拖动持久化/复位、五档字号、Enter/IME、工作树、Monaco 和终端。
- [ ] 使用已保存的 Provider 凭据验证流式、取消和干净退出，日志不得包含密钥。
- [ ] 检查 diff、提交推送、等待 CI 全绿，并上传含 SBOM、许可证包和 SHA256 的 GitHub 预发布资产。
