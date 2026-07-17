# AgentDesk Plan Mode 设计

[English](2026-07-17-agentdesk-plan-mode-design.md) | [简体中文](2026-07-17-agentdesk-plan-mode-design.zh-CN.md)

**状态：** 2026-07-17 已批准实施

## 目标与范围

将上游 ACP Plan Mode 接入 Windows 桌面客户端，作为 AgentDesk Beta 的第一个可交付切片。本切片包含能力协商、权威会话模式状态、已选定的输入区分段控件、严格协议解析和端到端顺序测试。

本次不包含会话搜索/历史、worktree、后台任务、多代理 Dashboard 和云服务；它们保留为后续 Beta 里程碑。

## 用户体验

- 输入框底部使用稳定的双选项分段控件：`执行` / `计划`（`Execute` / `Plan`）。
- 控件位于执行配置安全控件之前。会话模式与沙箱执行配置保持视觉相邻，但语义明确分离。
- 所选模式只作用于当前会话，并用于该会话后续提示。
- 提示正在运行或引擎请求尚未确认时禁用切换。
- UI 将用户选择显示为等待确认，只有宿主确认后才提交选中状态。
- sidecar 未声明 Plan Mode 能力时禁用“计划”，并提供可访问说明；客户端绝不在本地伪造 Plan Mode。
- 本机执行风险确认保持独立。选择“计划”不会绕过或削弱权限审批。

## 协议与类型

- 在 Core 和 Web 协议中增加 `SessionMode`，精确 wire 值为 `default` 和 `plan`。
- `agentdesk/v1/initialize` 协议版本 1 以向后兼容方式增加 `sessionModes` 能力列表。
- `EngineCapabilities` 暴露支持的会话模式；缺失能力数据时只允许 `default`。
- `IEngineClient.SetSessionModeAsync(SessionId, SessionMode)` 映射到上游 ACP `session/set_mode` 和对应模式标识。
- Prompt Web 命令必须携带会话模式，同时继续携带执行配置、本机风险确认和工作区 generation。
- 增加宿主到 Web 的 `session/mode/changed` 事件，包含权威模式与 session ID。
- 未知、大小写错误、前后空白、null、数字或未支持的模式值在 JSON 解析阶段失败关闭。

## 状态与数据流

1. Web UI 以期望模式 `default` 启动。
2. 用户选择分段控件。Web 在下一次 prompt 中发送期望模式；已有空闲会话时也可以立即请求切换。
3. 首次 prompt 时，宿主启动 sidecar、初始化并认证、创建会话、校验能力、调用 `SetSessionModeAsync`，然后才调用 `PromptAsync`。
4. 后续 prompt 仅在期望模式与最后确认模式不一致时切换。
5. 引擎成功响应后更新宿主状态并发送 `session/mode/changed`。只有当前引擎 generation 和活动 session 的 `current_mode_update` 才会被接收。
6. sidecar 重启会清除已确认模式，并在下一条 prompt 前重新校验能力。

严格顺序为 `initialize -> authenticate -> new/load session -> set mode -> prompt`。模式协商失败时绝不发送 prompt。

## 失败与竞态处理

- 引擎不支持 Plan Mode 时返回本地化错误，并让会话保持 `default`。
- 切换模式期间引擎失败时不发送排队的 prompt。
- 忽略来自旧 sidecar generation 或旧 session 的模式更新。
- 工作区切换保留 UI 模式偏好，但必须重新执行现有工作区风险确认；新会话独立设置模式。
- 重复确认事件保持幂等。
- 等待模式确认时取消，不得留下运行中的 prompt 或乐观确认状态。

## 测试

- Rust 测试验证 initialize 能力 payload 和协议版本向后兼容。
- Engine transport 测试验证精确 ACP 请求、响应、未支持模式和通知解析。
- App 测试验证首次会话顺序、已有会话切换、旧事件拒绝、取消、重启以及失败时不发送 prompt。
- Web 协议测试拒绝畸形模式。
- Web 组件测试验证分段控件可访问性、中英文标签、等待/运行禁用状态、键盘操作、能力回退，以及与本机风险确认的独立性。
- 真实浏览器检查焦点、布局、控制台和 125%-200% 缩放，不能出现截断。

## 交付

Plan Mode 从已发布 Alpha `main` 创建 Beta 功能分支开发。只有各层红绿循环与完整 Rust/Web/.NET 验证通过后才推送供审查。
