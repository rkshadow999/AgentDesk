# AgentDesk Cloud 部署与运维指南

[English](OPERATIONS.md) | [简体中文](OPERATIONS.zh-CN.md)

> [!WARNING]
> AgentDesk Cloud 仍是开发者预览。本指南只记录当前仓库已经实现并可真实测试的部署与恢复控制，不代表生产认证。服务支持单个 SQLite 数据库对应单个进程、停机备份/恢复，以及人工执行的 Bootstrap Token 重叠轮换；不提供多实例协调、在线恢复、生产 Runner、托管 TLS 或完整可观测性平台。

## 支持的运行模型

- 每个数据库路径只能运行一个 AgentDesk Cloud 进程。进程会独占 `<数据库>.service.lock`；同一路径的第二个进程或维护操作会 fail-closed。
- Linux 上进程或主机异常退出可能留下 stale `.service.lock`。必须先确认没有 Cloud 或维护进程在运行，保存该文件作为事故证据，再在重启前删除或改名。不得仅因 health 失败就删除锁。
- SQLite 数据库、服务锁和本地备份暂存目录必须位于受服务账号 ACL 保护的本地文件系统。不得把活动数据库放到 SMB、NFS、同步盘或普通用户可写的共享目录。
- 备份或恢复前必须停止服务。维护脚本会检查服务锁，不实现在线恢复。
- 除非管理员已经明确部署本服务及其信任材料，否则桌面端应继续保持 local-only。
- Runner、自动化、插件和远程执行仍是实验能力。本仓库没有交付生产 Runner 或执行证明。

## 服务账号与 ACL

使用独立、不可交互登录的服务账号，只授予以下路径所需的读写权限：

- 托管方式确实要求写入时的已发布应用目录；
- 包含 `.db`、`-wal`、`-shm` 和 `.service.lock` 的数据库目录；
- 管理员配置的本地日志目录。

备份操作账号还需要读取已发布 Cloud 可执行文件，并读写受保护的备份暂存目录。只有管理员和备份系统可以读取该目录，普通交互用户不得访问。如果父目录授予了宽泛写权限，应关闭继承并审计 ACL 变更。

不得把 Bootstrap Token、已签发 Token、恢复密钥、签名私钥或证书私钥放入数据库目录、命令行参数、源码或服务日志。

## TLS 与反向代理

`AgentDeskCloud__RequireHttps` 默认为 `true`，除隔离的回环开发环境外必须保持开启。启用后，所有明文 HTTP 请求，包括 health 和已认证 API，都会返回 `426 Upgrade Required`；服务不依赖推断出的 HTTPS 重定向端口。

当前服务没有启用 ASP.NET Core Forwarded Headers Middleware，也没有可信代理 allowlist，因此不得信任客户端提供的 `X-Forwarded-For`、`X-Forwarded-Host` 或 `X-Forwarded-Proto`。只使用以下受限部署方式之一：

1. 由 Kestrel 直接终止 TLS，并由管理员维护证书。
2. 公网 TLS 在反向代理终止，但代理到 Kestrel 的同机或私网链路必须再次使用 HTTPS。代理要剥离外部传入的 forwarding headers 并独立实施访问控制；AgentDesk Cloud 看到的后端请求仍然是 HTTPS。

不得在共享网络上用明文 HTTP 转发后仅设置 `X-Forwarded-Proto: https`；应用有意不信任该 Header。TLS 终止端必须配置证书续期、最低 TLS 版本、密码套件、请求大小、超时和安全 Header。SignalR 认证必须继续使用 `Authorization` Header；查询字符串 Bearer Token 会被拒绝。

## 必需配置

通过服务管理器或 Secret Store 注入配置。环境变量遵循 .NET 双下划线绑定。

| 变量 | 运维要求 |
| --- | --- |
| `AgentDeskCloud__DatabasePath` | 受保护数据库目录中的完整本地路径 |
| `AgentDeskCloud__BootstrapToken` | 当前管理员 Token，至少 32 个字符 |
| `AgentDeskCloud__PreviousBootstrapToken` | 平时为空；仅在有界轮换重叠期保存上一个管理员 Token |
| `AgentDeskCloud__RequireHttps` | 隔离回环开发之外必须为 `true` |
| `AgentDeskCloud__MaximumCiphertextBytes` | 按负载设置；允许范围为 1 KiB 至 64 MiB |
| `AgentDeskCloud__AutomationPollingIntervalSeconds` | 允许范围为 1 至 300 秒 |

当前 Token 过短或为空、已配置的 Previous Token 过短或为空、两个 Token 完全相同时，服务启动会失败。认证处理器只保留 SHA-256 摘要，并对全部已配置 Bootstrap 槽执行固定时序比较。

## Bootstrap Token 轮换

轮换需要重启，并且重叠窗口必须尽量短：

1. 在批准的 Secret Manager 中生成至少 32 个字符的新随机 Token。
2. 停止唯一服务实例。
3. 把 `AgentDeskCloud__BootstrapToken` 设置为新 Token，把 `AgentDeskCloud__PreviousBootstrapToken` 设置为旧 Token。
4. 启动服务，分别使用两个 Token 的 `Authorization: Bearer` Header 调用 `GET /api/v1/policy`，确认都能认证。
5. 把所有获授权的管理端切换到新 Token。不得把任一 Token 写入 URL、Shell 历史、截图或工单。
6. 再次停止服务，删除 `AgentDeskCloud__PreviousBootstrapToken`，然后重启。
7. 确认新 Token 调用 `GET /api/v1/policy` 返回 `200`，旧 Token 返回 `401`。
8. 在外部审计系统记录轮换时间与操作人。

Previous Token 不是长期归档槽。长期保留会让 Bootstrap 凭据暴露面翻倍。已签发的 device/service Token 具有独立生命周期；受影响的 subject 需要通过 `DELETE /api/v1/tokens/{subjectId}` 撤销。

## 停机备份

保留或发布与部署完全一致的 Cloud Server 可执行文件。脚本会调用其维护子命令，使 `PRAGMA wal_checkpoint(TRUNCATE)` 和 `PRAGMA integrity_check` 使用与服务相同的 SQLite Provider。

```powershell
Stop-Service AgentDeskCloud

pwsh -NoProfile -File .\scripts\agentdesk\Backup-AgentDeskCloudDatabase.ps1 `
  -DatabasePath "C:\ProgramData\AgentDeskCloud\data\agentdesk-cloud.db" `
  -BackupPath "D:\AgentDeskCloudBackups\agentdesk-cloud-2026-07-19.db" `
  -CloudServerPath "C:\Program Files\AgentDeskCloud\AgentDesk.Cloud.exe"

Start-Service AgentDeskCloud
```

备份目标及其父目录必须是已经受保护的本地路径。脚本会拒绝相对路径、UNC、设备路径、备用数据流、reparse point、已存在目标和与源相同的目标。脚本取得服务锁后执行 WAL checkpoint，检查 SQLite header 与完整性，通过独占文件句柄复制，再次验证副本，并写出 `<备份>.sha256`。

只有脚本成功后，才能把 `.db` 与 `.sha256` 配对复制到加密的异机存储。保留多个带日期的世代，并定期在隔离主机演练恢复。没有匹配校验和的数据库副本不能视为有效备份。

## 停机恢复

恢复会替换活动数据库，必须离线执行。运行前确认所选备份世代、可用磁盘空间、事故授权和回滚目录 ACL：

```powershell
Stop-Service AgentDeskCloud

pwsh -NoProfile -File .\scripts\agentdesk\Restore-AgentDeskCloudDatabase.ps1 `
  -DatabasePath "C:\ProgramData\AgentDeskCloud\data\agentdesk-cloud.db" `
  -BackupPath "D:\AgentDeskCloudBackups\agentdesk-cloud-2026-07-19.db" `
  -RollbackDirectory "C:\ProgramData\AgentDeskCloud\rollback" `
  -CloudServerPath "C:\Program Files\AgentDeskCloud\AgentDesk.Cloud.exe"

Start-Service AgentDeskCloud
```

服务锁仍被占用时，脚本会拒绝继续。触碰活动数据库之前，脚本会验证 sidecar SHA-256、SQLite header 和 `PRAGMA integrity_check`，并制作带哈希的独立回滚主库。随后在活动数据库旁暂存已验证字节，把现有 `-wal` 与 `-shm` 文件连同各自校验和移入回滚证据集合，原子替换目标，再次验证已安装数据库。如果替换或后验证失败，脚本会恢复原主库与 sidecar，并保留失败目标作为证据。成功恢复后的数据库旁不会残留旧 SQLite sidecar。

重启后必须检查 `/health/ready`、使用当前 Bootstrap Token 认证，并核对预期的策略/会话元数据。在恢复后的服务通过管理员验证窗口前，保留回滚文件对。恢复过程不得删除或覆盖源备份。不支持在线恢复或跨实例恢复。

## 监控与告警

仓库提供 health endpoint，但没有生产 metrics exporter 或告警包。管理员至少需要监控：

- 进程重启与数据库服务锁获取失败；
- 来自可信监控网络的 `/health/live` 和 `/health/ready`；
- TLS 证书到期、反向代理与后端 TLS 失败；
- 磁盘容量、SQLite I/O 错误、完整性失败和备份年龄；
- 备份/恢复脚本退出码与校验和结果；
- 持续的 `401`、`403`、`429` 和 `5xx`，且不得记录 Bearer 值；
- 自动化积压、Runner lease 失败和异常的插件发布者变更，这些需要外部运维查询或日志。

不得把请求正文、密文、Authorization Header、配对包、Token、本地用户名或私有文件路径写入共享遥测。Health 成功不能证明备份新鲜度、远程 Runner 安全或端到端可解密性。

## 事故响应

- **Bootstrap Token 泄漏：** 限制访问，按重叠流程轮换，删除 Previous Token，并审查管理操作。
- **已签发 Token 泄漏：** 撤销对应 subject，签发替代凭据，并审查团队范围活动。
- **数据库损坏或主机丢失：** 停止服务，保留当前数据库和 WAL 相关文件，验证备份校验和，离线恢复并保留生成的回滚副本。
- **签名密钥或插件发布者失陷：** 在服务外撤销信任，停用相关插件/Marketplace，保留证据并轮换发布者材料。签名不代表代码安全。
- **Runner 失陷：** 禁用远程 Runner 策略，撤销服务凭据，停止队列执行，并假设任务元数据及 Runner 可访问的执行 Secret 已泄漏。本仓库没有生产 Runner 隔离保证。
- **恢复密钥泄漏：** 通过桌面工作流轮换客户端恢复材料并重新配对设备；服务端无法恢复明文，也不能修复客户端信任边界。

保留相关日志和不可变备份哈希，不要公开未披露漏洞或凭据，并遵循仓库[安全策略](../SECURITY.zh-CN.md)。

## 验证

每次修改运维工具后运行：

```powershell
dotnet test .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj `
  --configuration Release

pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskCloudDatabaseMaintenance.ps1
```

脚本测试会启动真实 Cloud 进程，证明在线备份/恢复被拒绝，执行经验证的备份，拒绝不安全路径和损坏输入，恢复完全相同的字节，检查完整回滚证据集合，并证明安装后校验失败时会恢复原数据库与 SQLite sidecar。`.github/workflows/agentdesk-windows.yml` 会在 Windows `cloud-maintenance` 作业中运行该测试，并要求 Tag 发布等待此作业。它不能证明多实例安全、在线备份兼容性、生产 TLS 或生产 Runner 已就绪。
