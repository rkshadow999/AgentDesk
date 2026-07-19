# AgentDesk 可选自托管云端

[English](README.md) | [简体中文](README.zh-CN.md)

已实现的单实例、Token 轮换、停机备份与停机恢复流程见[部署与运维指南](OPERATIONS.zh-CN.md)。

> [!WARNING]
> 本项目是开发预览，不是受支持的生产服务。AgentDesk 桌面端已经提供明确启用的可选 Client，但默认 Profile 是 local-only。停机数据库维护和 Bootstrap Token 重叠轮换已经实现，但生产 TLS 验证、监控、外部审计、恢复密钥生命周期、多实例协调和打包后的多设备测试仍未完成。不得把本预览当成托管生产服务。

## 已实现的服务器表面

当前 ASP.NET Core 服务提供：

- 匿名 liveness/readiness health；全部 `/api/v1` 路由要求 Bearer 认证。
- 支持有界重启轮换的当前与可选 Previous Bootstrap 管理员 Token，以及以哈希保存、带角色策略的 `device` 和 `service` Token；管理员可通过 `DELETE /api/v1/tokens/{subjectId}` 幂等撤销已签发 subject。
- 团队策略持久化。
- 带 revision 的不透明加密会话 envelope，以及加密设备间 handoff。
- Runner 注册、能力匹配、加密任务入队、带 lease 的任务领取和仅 lease 所有者可完成。
- ECDSA 发布者登记和带签名的插件元数据发布。
- 后台 Worker 调度加密自动化任务。
- 只接受 Header 认证的 SignalR negotiation，以及按团队/设备限定的服务端通知。系统会拒绝查询字符串 `access_token`，当前也未交付或宣称浏览器 SignalR 客户端可用。
- 带单进程服务锁的 SQLite 持久化、经验证的停机备份/恢复脚本、请求验证、envelope 大小限制和固定窗口 API 限流。

服务器会验证支持的 envelope 名称、Base64、密文大小、标识符、角色、revision、lease 与插件签名。`AES-256-GCM` 要求 12 字节 nonce，`XCHACHA20-POLY1305` 要求 24 字节 nonce。它**不会**替客户端加密明文，也不会声称带签名插件可以安全执行。

## 桌面客户端边界

只有用户保存远程 Profile 后，桌面端才会离开 local-only。远程 Profile 要求 HTTPS，HTTP 仅允许回环开发。端点/团队/设备元数据保存在 `%LOCALAPPDATA%\AgentDesk`；访问 Token 与恢复密钥通过 Windows Credential Manager 保存，绝不会发送到 WebView2。

已接入的桌面流程可以：

- 导出引擎拥有的会话文档，把团队/scope/session/revision 绑定为 AES-GCM authenticated data 后上传；拒绝更低的 Server revision；下载/解密后导入为引擎会话。
- 通过原生文件对话框导出/导入口令保护的恢复密钥配对包，然后创建和接收加密设备 handoff。配对文件实施大小限制，拒绝备用数据流、设备路径、保留名称和 reparse point，通过已打开句柄校验 final path，并使用原子替换。只有引擎完成导入后才确认 handoff。
- 读取/更新团队策略；注册 Runner 身份/能力；对客户端加密的 Runner 任务/结果正文执行入队、领取和完成；创建/列出/停用客户端加密自动化。当前不提供生产 Runner、远程执行证明、后台 Push Client，也未覆盖每个桌面操作的完整策略强制。

远程 Profile 下，桌面端会对所有可能加载代码的 Plugin/Marketplace 操作 fail-closed。Web UI 提供的发布者 ID 不能作为签名证据。目录列表/刷新和 Plugin disable/remove、Marketplace uninstall 这类降低权限的操作仍可使用；未来安装路径必须绑定由宿主验证的仓库记录、摘要和签名。

这些流程具有单元测试和真实 Kestrel 集成测试，后者会检查 Server 数据库不包含已知明文会话正文。这是当前实现路径的有效证据，但不能证明生产部署安全。

## 仅用于本地开发

生成新的开发 Bootstrap Token，不要提交固定值：

```powershell
$bytes = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$env:AgentDeskCloud__BootstrapToken = [Convert]::ToBase64String($bytes)
$env:AgentDeskCloud__DatabasePath = "$env:TEMP\agentdesk-cloud-dev.db"
$env:AgentDeskCloud__RequireHttps = "false"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5187"

dotnet run --project .\cloud\src\AgentDesk.Cloud\AgentDesk.Cloud.csproj
```

`RequireHttps=false` 只适用于隔离的回环地址开发测试。不得让该配置监听局域网或公网，否则 Bearer Token 与密文元数据可能被截获或修改。

在另一个终端运行：

```powershell
Invoke-RestMethod http://127.0.0.1:5187/health/live
Invoke-RestMethod http://127.0.0.1:5187/health/ready
```

API 请求使用 `Authorization: Bearer <token>`。Token 只能放在进程级环境变量或密钥管理器中，不得进入源码、提交到 Git 的 `.env`、Shell 转录、URL、截图或 Issue。

## 配置

环境变量遵循 .NET 双下划线绑定规则：

| Key | 默认值 | 边界 |
| --- | --- | --- |
| `AgentDeskCloud__DatabasePath` | 应用旁的 `data/agentdesk-cloud.db` | 使用服务账号文件 ACL 与备份保护 |
| `AgentDeskCloud__BootstrapToken` | 空；启动验证会失败 | 至少 32 个字符；拥有管理员权限 |
| `AgentDeskCloud__PreviousBootstrapToken` | 空 | 仅在有界轮换重叠期保存上一个管理员 Token；必须与当前 Token 不同 |
| `AgentDeskCloud__RequireHttps` | `true` | 除回环测试外保持开启 |
| `AgentDeskCloud__MaximumCiphertextBytes` | 16 MiB | 有效范围为 1 KiB 至 64 MiB |
| `AgentDeskCloud__AutomationPollingIntervalSeconds` | 5 | 有效范围为 1 至 300 秒 |

TLS 开发运行需要配置受信 Kestrel 开发证书，并保持 `RequireHttps=true`。服务当前不信任 forwarded headers，也不提供多实例协调。受限的 TLS/反向代理要求、ACL、人工 Token 轮换、停机数据库维护、最低监控项与事故流程见[部署与运维指南](OPERATIONS.zh-CN.md)。

## 测试

```powershell
dotnet test .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj `
  --configuration Release

dotnet test .\desktop\tests\AgentDesk.Cloud.Client.IntegrationTests\AgentDesk.Cloud.Client.IntegrationTests.csproj `
  --configuration Release

pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskCloudDatabaseMaintenance.ps1
```

Server 测试集使用临时 SQLite 数据库，只在进程内测试服务器中关闭 HTTPS。覆盖 Bootstrap Token 重叠轮换与无效启动配置、HTTPS fail-closed、安全创建全新数据库目录、本地/reparse/多硬链接数据库路径拒绝、不破坏预置文件的服务锁创建、认证与最小权限角色、幂等 Token 撤销、租户/lease 所有权、单调 revision、按算法拒绝错误 nonce、策略、handoff 所有权、Runner 入队/领取/完成、签名拒绝/接受、自动化创建/调度、Header 认证 SignalR negotiation 与 query Token 拒绝。桌面集成项目会启动真实回环 Kestrel 进程并测试 Client API 与加密 envelope。最近记录的本地结果为 Server `37/37`、真实 Kestrel 桌面集成 `3/3` 通过，运维脚本 E2E 也已针对真实 Cloud 进程通过。CI 在 `cloud-tests` 中保留跨平台 Server 测试，并通过独立 Windows `cloud-maintenance` 作业执行备份/恢复/回滚恢复测试；这些都不是生产部署证据。

## 安全与数据边界

- Bootstrap Token 是管理员恢复/引导密钥。当前与可选 Previous Token 只以 SHA-256 摘要保存在内存中，并对全部已配置槽执行固定时序比较。轮换仍是人工重启流程；所有管理端迁移后必须立即删除 Previous Token。
- 签发 Token 以哈希保存，但持有者在管理员撤销该 subject 前仍拥有对应角色。撤销是幂等操作；运营者仍需建立可审计的轮换与恢复流程。
- 只有客户端正确加密时，会话、handoff、任务和自动化正文才是不透明密文。服务器仍会看到团队、subject、设备、Runner、能力、时间、revision 和大小元数据。
- 当前桌面端已把标识符/revision 绑定为 authenticated associated data，使用 OS 保护恢复密钥，并拒绝更低 revision。生产工作仍需补齐恢复密钥轮换/撤销、多设备回滚恢复、可审计删除/导出，以及成文的 nonce/密钥生命周期。
- 配对包路径加固会降低 reparse/path substitution 和部分写入风险，但配对包与口令同时泄露时仍可能暴露恢复密钥。
- ECDSA 插件签名只认证已登记发布者和 manifest digest，不会沙箱化、审查或让插件代码变得可信。
- 因此，在发布者、摘要和签名验证由宿主掌握前，桌面端会阻止每个远程 Profile 中加载代码的 Plugin/Marketplace 操作；客户端发布者声明始终不可信。
- 远程 Runner 是执行信任边界。本项目尚未交付生产 Runner 包、证明、隔离配置或密钥发送协议。
- SignalR 只接受 `Authorization` Header，并拒绝查询字符串 Bearer Token，以降低代理/访问日志泄露风险。部分浏览器 SignalR 传输无法设置该 Header，因此当前服务器不提供浏览器客户端兼容性保证。
- 每个数据库路径只允许一个进程。备份与恢复仅支持停机模式，会验证 SQLite 完整性和 SHA-256 证据，并在替换前保留回滚副本；不支持在线或多实例恢复。

请参阅仓库[威胁模型](../docs/AGENTDESK-THREAT-MODEL.zh-CN.md)与[安全策略](../SECURITY.zh-CN.md)。本地独立桌面工作流仍是默认值，并且不依赖本项目。
