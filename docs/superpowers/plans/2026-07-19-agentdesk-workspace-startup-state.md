# AgentDesk 无工作区启动状态修复实施计划

> **面向实现者：** 按 TDD 顺序执行，每一步都先验证失败，再写最小实现。

**目标：** 让未选择工作区成为正常空状态，消除伪“引擎操作失败”，并在首次选择工作区后正确加载会话。

**架构：** React 工作台负责阻止无工作区的会话请求并呈现操作入口；C# 宿主作为协议防线，对无工作区列表请求返回空结果。现有 workspace generation 和 session list correlation 协议保持不变。

**技术栈：** React、TypeScript、Vitest、Testing Library、C#、xUnit、WinUI 3。

---

### 任务 1：Web 无工作区回归测试

**文件：**
- 修改：`desktop/web/tests/workbench.test.tsx`

- [ ] 添加无初始工作区时不发送 `session/list`、不显示列表错误、显示“选择工作区”按钮的测试。
- [ ] 添加点击按钮发送 `workspace/select` 的断言。
- [ ] 添加首次 `workspace/selected` 后发送一个 `session/list` 的断言。
- [ ] 运行：`npm test -- --run tests/workbench.test.tsx`
- [ ] 预期：新增测试因当前启动请求和缺失空状态而失败。

### 任务 2：Web 最小实现

**文件：**
- 修改：`desktop/web/src/Workbench.tsx`
- 修改：`desktop/web/src/locales/zh-CN.json`
- 修改：`desktop/web/src/locales/en-US.json`

- [ ] 无工作区时把列表状态保持为 `loaded`，清空列表错误并拒绝发送 `session/list`。
- [ ] 在会话空状态中显示唯一的工作区选择按钮。
- [ ] 首次和后续 `workspace/selected` 都请求会话列表；仅在真正切换已有工作区时重置当前会话上下文。
- [ ] 运行聚焦 Web 测试并确认通过。

### 任务 3：宿主防御性回归测试

**文件：**
- 修改：`desktop/tests/AgentDesk.App.Tests/AgentDeskHostControllerTests.cs`

- [ ] 添加无工作区 `SessionListWebCommand` 返回空关联列表、无错误事件、无 sidecar 创建的测试。
- [ ] 运行聚焦 xUnit 测试。
- [ ] 预期：当前实现返回 `SessionListErrorWebEvent`，测试失败。

### 任务 4：宿主最小实现

**文件：**
- 修改：`desktop/src/AgentDesk.App/Bridge/AgentDeskHostController.cs`

- [ ] 在会话列表处理器入口对空工作区返回空 `SessionListChangedWebEvent`。
- [ ] 保持真实 sidecar、凭据和索引失败继续返回关联错误。
- [ ] 运行聚焦 xUnit 测试并确认通过。

### 任务 5：回归、桌面验证与重新打包

**文件：**
- 不新增生产文件；生成 `artifacts` 发布产物。

- [ ] 运行完整 Web 测试和生产构建。
- [ ] 运行 `AgentDesk.App.Tests`、格式检查和发布脚本测试。
- [ ] 重新生成 x64 Portable 包和 SBOM。
- [ ] 从最终 ZIP 解压冷启动，验证无错误卡、有工作区选择入口；选择工作区后验证引擎连接和真实模型短响应。
- [ ] 校验 ZIP、SHA-256、revision、sidecar 架构和双 WebView2。
