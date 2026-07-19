# AgentDesk 路线图与交付状态

[English](ROADMAP.md) | [简体中文](ROADMAP.zh-CN.md)

本文记录 2026-07-19 的源码树状态，不是发布日期承诺。仅存在协议方法、单元测试或服务器端点，不代表用户功能已经完成；完整功能还需要桌面工作流、安全边界、恢复行为、文档与发布证据。

## 状态说明

- **源码中可用：** 已接入本地桌面工作流并有聚焦测试；已签名公开发布仍可能尚未产生。
- **部分完成/实验性：** 已有可用实现，但仍缺必要 UI、隔离、集成或发布门禁。
- **受阻断：** 因无法证明安全要求而有意保持不可用。
- **未实现：** 尚无功能完整的 AgentDesk 桌面路径。

## 能力矩阵

| 能力 | 状态 | 证据与边界 |
| --- | --- | --- |
| Windows 11 WinUI 外壳与两个 WebView2 界面 | 源码中可用 | x64/ARM64 项目配置、工作台/检查器桥、Web 组件测试 |
| ACP stdio sidecar 生命周期、流式响应、取消、终端、diff、权限审批 | 源码中可用 | 宿主/引擎/Rust 契约测试；本机执行仍非沙箱 |
| xAI 与自定义 OpenAI 兼容 Base URL/模型/backend | 源码中可用 | Chat Completions/Responses 选择、与端点绑定的 Credential Manager Key、默认 HTTPS、HTTP 明确授权 |
| Plan Mode | 源码中可用 | 执行/计划分段控件、能力协商、旧事件隔离 |
| 会话中心 | 源码中可用 | 引擎 list/load/rename、搜索/分页、SQLite 元数据索引、可逆本地归档 |
| 会话分叉、压缩、回退点与回退 | 源码中可用 | ACP 扩展客户端、宿主投影、Web 对话框与聚焦测试 |
| 运行时命令/Skill 目录与显式 Memory flush | 源码中可用 | 命令面板、按能力启用的目录、显式 flush 控件以及宿主/Web 测试 |
| 活动会话 Runtime Dashboard | 源码中可用 | 权威后台任务/subagent 列表、任务 kill、subagent 详情/cancel，以及宿主与 Web 测试 |
| Sidecar 故障检测与重启隔离 | 源码中可用 | 进程 generation 与传输故障测试；完整应用崩溃恢复/迁移仍是 Beta 门禁 |
| 简体中文 UI | 源码中可用 | 默认 Shell/Web 资源与中文优先工作流 |
| 英文 UI 与应用内语言选择 | 源码中可用 | Web 标签立即切换；WinUI 资源重启后应用；人工截断/无障碍检查仍是发布门禁 |
| WSL 严格执行 | 受阻断 | 具名非 Docker 发行版选择已 fail-closed；health 证明仍报告子进程网络覆盖不完整并停止启动 |
| 图片附件 | 源码中可用 | PNG/JPEG/GIF/WebP 签名检查、数量/大小预算、能力缺失时 fail-closed、预览/发送测试 |
| 工作区引用、AGENTS.md、Memory 与扩展管理 | 源码中可用 | 有界 `@` 文件搜索/引用标签、已有文件 `AGENTS.md` 编辑器、64 KiB 且按能力启用并要求两阶段修改确认的 Memory 浏览器，以及 MCP/Skills/Hooks/Plugins/Marketplace 管理；远程 Cloud Profile 对所有会改变注册表的 Plugin/Marketplace 操作 fail-closed |
| 备份、会话迁移、配对与 Portable 更新 | 源码中可用 | 原生文件选择器、有界会话文档、备份/恢复、加固的配对包文件 I/O、固定公钥的 manifest 验证、独立 Portable 更新器，以及持久化且默认关闭的可选后台可用更新检查；应用仍需显式操作，MSIX 更新仍需外部完成 |
| Worktree 生命周期 | 源码中可用 | 创建/列表/详情/应用/移除/GC，加上先编辑请求、再通过标准提示词/权限链启动的两阶段审查，另有 dry-run、冲突投影以及宿主/引擎/Web 测试 |
| 自动并行 worktree 隔离 | 源码中可用 | 在 strict 模式下，桌面 sidecar 会把包括 explore/plan 在内的所有 subagent 强制放入隔离 worktree，创建失败时中止而不是共享目录；显式分支归属与自动冲突处理仍属于后续工作流完善 |
| Windows 通知 | 源码中可用 | 可选通用完成/权限通知以及仅使用 session ID 的激活路由；激活参数不含提示词或工作区路径 |
| 浏览器测试与 Windows UI Automation Computer Use | 部分完成/实验性 | 设置页已经提供原生 FlaUI/UIA3 聚焦窗口/调用控件/设置值操作面，并接入宿主策略、“仅允许一次”审批和不回显输入值的事件；仍缺打包后真实目标证据、隔离、自主目标发现和通用 Computer Use 保证 |
| 自托管云端服务器 | 部分完成/实验性 | `cloud/` 已有认证/RBAC、加密 envelope 存储、Runner、策略、handoff、插件签名、自动化、SignalR、37 项 Server 测试，以及真实进程停机备份/恢复/回滚 E2E 作业；生产运维仍不完整 |
| 桌面加密同步、Runner、自动化与跨设备接力 | 部分完成/实验性 | 默认 local-only、Credential Manager Token/恢复密钥、AES-GCM 元数据绑定、回滚检测、加固配对、上传/下载/导入/导出/删除、handoff、策略、Runner 注册/入队/领取/完成、自动化创建/列表/停用、需认证的 SignalR 变更通知和真实 Kestrel 集成测试已接入；生产 Runner 与后台/设备 Push 尚未完成 |

## 当前验证快照

准确的当前本地测试数量统一维护在[构建与测试](BUILD-AND-TEST.zh-CN.md)中，并会在发布前重新生成。Windows Automation 策略/协调器/协议、Cloud 密文绑定、配对文件加固、发布 staging 与脱敏 Provider smoke 工具都有自动化测试。经过授权的真实 Chat Completions 与 Responses Provider 运行已经完成；针对真实目标应用的打包后 UI Automation 仍属于最终候选门禁。

公开源码仓库为 [rkshadow999/AgentDesk](https://github.com/rkshadow999/AgentDesk)。当前不声称存在签名公开 MSIX、ARM64 真实设备启动、真实 WSL2 环境中可用的 `WslStrict`、生产 Runner、生产 Cloud 部署，或与官方 Codex 私有服务等价。CI 定义和本地未签名产物不能满足这些门禁。

## 产品原则

- 保持独立社区维护身份，清楚记录 `xai-org/grok-build` 来源，不复制 Codex 名称、Logo、素材或私有服务声明。
- 坚持 Windows 客户端本地优先，不要求 AgentDesk 账号或云服务。
- 清楚展示执行权限、权限决定、提供商传输和沙箱限制。
- 通过 ACP 协商能力，不链接不稳定 Rust 内部 API。
- AgentDesk 自有用户与贡献者文档同步发布英文和简体中文。
- 只有强制措施得到验证时才开放安全配置，否则 fail-closed。

## 阶段 1：Alpha 本地工作台

源码树已经实现：

- [x] Windows WinUI 宿主与 React 工作台/检查器及其 x64/ARM64 项目配置、sidecar 所有权、流式任务、取消、权限审批、终端与 diff 检查。
- [x] 凭据管理器、自定义提供商设置、凭据环境清理与进程树清理。
- [x] Portable/MSIX 构建输入、架构检查、包内法律声明、SBOM/校验和 workflow、tag 签名门禁与上一发布回滚包生成。
- [x] Windows/Linux CI 矩阵定义和聚焦 Web/.NET/Rust/发布测试。

Alpha 发布仍缺的门禁：

- [ ] 在公开仓库实际生成并验证首个 x64/ARM64 签名发布。只有 workflow 文件不构成发布证据。
- [ ] 完成两种架构的打包冒烟，以及中文 IME、纯键盘、Narrator、高对比度和 125%-200% 缩放人工检查。
- [ ] 保持确定性 Mock Provider 覆盖，并通过进程级环境变量运行可选真实 Provider smoke；两条路径都不得记录凭据或请求/响应正文。
- [ ] 在全部子进程入口强制并证明网络限制前，保持 `WslStrict` 受阻断。本机兼容模式（非沙箱）带明确警告继续可用。

## 阶段 2：完整本地工作流

提前完成的能力：

- [x] 由宿主掌握已确认状态的 Plan Mode。
- [x] 会话搜索、分页、打开/加载、重命名、可逆本地归档、分叉、压缩、回退点与回退。
- [x] 与引擎会话正文分离的持久 SQLite UI 元数据。
- [x] Provider Base URL/模型/backend 配置，包括 Responses API 选择和显式不安全 HTTP 同意。
- [x] 英文切换、经过验证的图片附件、会话导入/导出、本地备份/恢复和 Windows 通知。
- [x] Worktree 创建/列表/详情/应用/移除/GC 流程，包含显式破坏性确认和 dry-run。审查会先准备可编辑请求，再要求独立的“开始审查”操作，并沿用正常提示词和权限链。
- [x] 有界工作区文件引用、已有文件 `AGENTS.md` 编辑器，以及写入/删除必须经过宿主两阶段确认的 Memory 浏览器。
- [x] MCP、Skills、Hooks、Plugins 与 Marketplace 设置，通过有界协议投影，并以环境变量引用代替 Secret 值。远程 Cloud Profile 会阻止所有 Plugin 变更和 Marketplace install/update/uninstall，直至宿主能够验证仓库记录、摘要和签名；目录列表/刷新仍可使用。
- [x] 使用固定签名公钥验证、由用户主动触发的 Portable 更新检查和独立更新器。可选后台可用更新轮询会持久化设置、默认关闭，只有 opt-in 后才周期运行，并且不会自动应用更新。

阶段 2 完成仍要求：

- [ ] 在打包后的 Windows 构建上完成双语、IME、无障碍和缩放人工验证。
- [ ] 超出当前 `@` 搜索/引用标签的更广泛文件导航，以及超出当前管理操作的扩展来源证明与恢复流程。
- [ ] 完整崩溃恢复/数据迁移，以及跨已发布版本的恢复兼容文档。
- [ ] 完成 Portable 更新器及其可选后台可用更新通知的打包后失败恢复、降级、隐私和回滚验证。MSIX 仍不使用应用内替换路径。
- [ ] 冷启动、sidecar 握手、1 万会话和 10 万行终端的自动无障碍/性能门禁。这些目标当前尚未得到证明。

## 阶段 3：高级智能体工作流

已经在宿主/协议边界接入源码的范围：

- [x] 活动会话 Runtime Dashboard，提供权威 task/subagent 列表、任务 kill、详情与取消操作。
- [x] 手动 worktree 创建/列表/详情/应用/移除/GC，以及两阶段可编辑审查请求。
- [x] 可选通用 Windows 完成/权限通知，以及仅使用 session ID 的激活路由。
- [x] 受限的原生 Windows UI Automation 设置页操作面。执行器会附加到明确的进程 ID，可以聚焦窗口、调用选定控件或设置选定值，并同时要求策略启用和“仅允许一次”权限审批。

该实现不会把 AgentDesk 变成具备通用隔离保证的 Computer Use 系统。阶段 3 仍未完成，完整交付需要：

- [ ] 扩展 Runtime Dashboard，加入每任务权限队列、执行配置、输出归属、跨会话导航和完整旧 generation 恢复。
- [ ] 在现有“strict 模式下所有 subagent 强制隔离且失败时中止”的 worktree 机制上，补充显式基础分支归属、自动冲突策略、保留控制和跨会话恢复 UX。
- [ ] 把当前两阶段 worktree 审查请求扩展为多智能体审查 Dashboard 与可审查 handoff，同时继续遵守会话和权限策略。
- [ ] 对打包后的通知激活与中断行为做真实测试，同时保持现有不包含提示词/源码的 payload 边界。
- [ ] 完成使用隔离 WebView2/CDP 测试路径的浏览器测试并明确网络/数据披露；只有 helper 测试不构成打包后浏览器证据。
- [ ] 加固实验性 Windows UI Automation 操作面：增加打包后真实目标测试、不包含输入值的持久审计元数据、更强中断保证和隔离方案；绝不能静默启用。

任何阶段 3 功能都不能绕过前台任务使用的权限、提供商、凭据和执行门禁。

## 阶段 4：可选自托管云端

已经接入的开发预览范围：

- [x] 独立 `cloud/` Server 实现哈希角色 Token、策略、带 revision 的不透明 envelope、Runner lease、加密 handoff、ECDSA 插件发布者元数据、调度任务和需认证的 SignalR 通知。
- [x] 可选桌面客户端实现加密会话同步、Runner 入队/领取/完成、自动化创建/列表/停用、策略和跨设备接力，持久化默认值仍是 local-only。
- [x] 恢复密钥配对包使用有界原生文件 I/O、final-path/reparse 校验和原子替换。

它还不是完整云产品。阶段 4 仍要求：

- [ ] 围绕已接入加密客户端补齐恢复密钥轮换/撤销、多设备回滚恢复和生产隐私/保留控制。
- [ ] 带隔离证明、最小权限密钥发送、取消、升级和审计行为的生产 Runner。
- [ ] TLS、反向代理 header、Token 轮换、备份/恢复、数据库迁移、quota、监控、多实例调度和事件响应生产部署指南。
- [ ] 在已接入的需认证 SignalR 桌面通道之外实现生产后台/设备 Push、生产 Runner UX、覆盖每个桌面操作的完整团队策略执行、由宿主验证的签名插件仓库信任 UX 和打包后的多设备测试。客户端提供的发布者标识必须继续视为不可信。
- [ ] 明确保留与隐私控制。API Key、提示词、源码和命令输出默认不得上传。

本地独立桌面端会继续受支持，不得依赖此服务。

## 发布完成门禁

每次公开发布都必须对准确提交提供证据：

1. 相关 Web、.NET、云端、Rust、ACP 生命周期、仓库和发布契约测试通过。
2. 构建 x64/ARM64 包输入；tag MSIX 已签名并验证签名者。
3. SPDX/CycloneDX SBOM、Apache/MPL/第三方声明、源码 revision、SHA-256 清单和上一发布回滚产物齐全。
4. 未发现提交或日志中的 Key、Token、提示词、私有源码、用户名或本地路径。
5. 变化的信任边界与用户流程已同步更新中英文文档。
6. 记录人工平台/无障碍检查与任何未自动化风险，不能把它们改写为无证据完成声明。

参阅[构建与测试](BUILD-AND-TEST.zh-CN.md)、[安装指南](INSTALLATION.zh-CN.md)和[威胁模型](AGENTDESK-THREAT-MODEL.zh-CN.md)。

## 非目标

- 像素级复制 Codex，或使用其名称、Logo、私有素材、账号系统、配额、连接器或云执行。
- 复用未经授权的 grok.com OAuth 客户端。
- 把本机兼容模式（非沙箱）描述为沙箱。
- 强制本地工作依赖遥测、崩溃上传或托管账号。
- 声称仓库无法提供的模型质量或私有服务等价能力。
