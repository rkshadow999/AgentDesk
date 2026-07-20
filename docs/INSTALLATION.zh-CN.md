# 在 Windows 11 上安装 AgentDesk

[English](INSTALLATION.md) | [简体中文](INSTALLATION.zh-CN.md)

> [!WARNING]
> AgentDesk 仍处于 Alpha 阶段，可以使用当前 Windows 账号的权限编辑文件和运行命令。`NativeProtected` 是**本机兼容模式（非沙箱）**。`WslStrict` 当前受阻断并保持 fail-closed。

## 选择安装包

| 包类型 | 适用场景 | 信任状态 |
| --- | --- | --- |
| `v*` GitHub Release 中的已签名 MSIX | 存在正式签名发布后用于常规安装 | 安装前校验签名者和 SHA-256 |
| 同一 Release 中的 Portable zip | 隔离目录或手动安装 | 校验 SHA-256；程序仍以当前用户权限运行 |
| 分支或 Pull Request 生成的未签名 MSIX | 仅用于 CI 与开发 | 不是正式发布，不得当作正式包分发 |
| 源码构建 | 开发与审计 | 构建主机和生成二进制由构建者负责 |

本项目不会通过 xAI 或 OpenAI 的上游渠道发布安装器。公开源码仓库为 [rkshadow999/AgentDesk](https://github.com/rkshadow999/AgentDesk)，但目前仍不声称存在经过独立验证的签名 GitHub Release 或签名公开 MSIX。在此类发布实际存在前，请使用源码构建或明确标注为未签名开发预览的产物，不要把 CI 或本地产物视为稳定版本。AgentDesk 也不是官方 Codex 应用的发行版，不提供与其私有账号、配额、连接器、托管执行或模型服务功能等价的能力。

## 环境要求

- 与安装包架构一致的 Windows 11。x64 是当前本地验证目标；没有成功 ARM64 发布结果时，不得把 ARM64 配置当作真实设备证据。
- Microsoft Edge WebView2 Runtime；受支持的 Windows 11 通常已经包含。
- xAI API Key，或明确配置的 OpenAI 兼容提供商凭据。
- 使用本机执行时，只选择你信任的工作区。

从源码构建还需要[构建与测试](BUILD-AND-TEST.zh-CN.md)中固定的工具链，包括 Visual Studio 2022 C++ Build Tools/MSVC v143、Windows 11 SDK、`global.json` 固定的 .NET SDK 10.0.302、`rust-toolchain.toml` 固定的 Rust 1.92.0，以及 `protoc` 29.3。

当前本机工作流不要求 WSL2。准备随包 Linux payload 时，AgentDesk 会忽略 Docker Desktop 内部发行版，并要求系统中恰好存在一个非 Docker 发行版，或通过 `-DistributionName` / `AGENTDESK_WSL_DISTRIBUTION` 显式选择。安装器会写入 `/usr/local/bin/agentdesk-engine`；桌面可用性要求该文件可执行、架构匹配，并且 SHA-256 与随包 payload 完全一致。只安装 payload 仍不会让 `WslStrict` 变为可用；只要 health 证明继续报告子进程网络限制不完整，该配置就会保持受阻断。

## 校验发布包

从同一 GitHub Release 下载安装包，以及匹配的 `AgentDesk-<版本>-win-<架构>-SHA256SUMS.txt`。在 PowerShell 中运行：

```powershell
$package = "AgentDesk-0.1.0-alpha.2-win-x64-portable.zip"
$expected = (Select-String `
  -Path "AgentDesk-0.1.0-alpha.2-win-x64-SHA256SUMS.txt" `
  -Pattern ([regex]::Escape($package))).Line.Split(' ')[0]
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $package).Hash.ToLowerInvariant()
if ($actual -cne $expected) { throw "AgentDesk 安装包校验和不匹配。" }
```

MSIX 还需要检查签名者：

```powershell
Get-AuthenticodeSignature -LiteralPath .\AgentDesk-0.1.0-alpha.2-win-x64.msix |
  Format-List Status,StatusMessage,@{Name='Signer';Expression={$_.SignerCertificate.Subject}}
```

状态必须为 `Valid`，签名者主题与包内 manifest Publisher 都必须是 `CN=AgentDesk`。装有 Windows SDK 的维护者可以运行 `scripts/agentdesk/Verify-AgentDeskMsixSignature.ps1`；发布自动化还可传入仓库固定的证书指纹。

每个发布还提供 SPDX 与 CycloneDX SBOM。安装包内包含 `LICENSE`、全仓与桌面依赖声明、源码可获取性说明和 `SOURCE-REVISION.txt`。revision、架构、签名者或校验和不一致时必须停止安装。

## 安装已签名 MSIX

1. 按上述方法校验 MSIX。
2. 使用 Windows 应用安装程序打开 `.msix`，并核对发布者。
3. 选择**安装**。
4. 从开始菜单启动 AgentDesk。

不要为了安装来源不明的 MSIX 而导入或信任开发证书。分支和 Pull Request 的 MSIX 会有意保持未签名，只适用于受控的开发验证。

## 运行 Portable 包

1. 校验 Portable zip。
2. 解压到当前 Windows 用户拥有的新目录。
3. 从该目录启动 `AgentDesk.App.exe`。
4. 保持 `agentdesk-engine.exe`、Web 资源、运行时文件和法律声明位于同一目录结构中。

不要直接从 zip 内运行，也不要把新版本覆盖到正在运行的 Portable 目录。

## 首次运行

1. 选择受信任的工作区。
2. 打开 Provider 设置，确认 Base URL、模型和 API backend（`chat_completions` 或 `responses`）。
3. 输入 API Key。Key 存入 Windows 凭据管理器，并与当前 Base URL 绑定；修改 Base URL 后必须重新输入 Key。
4. 优先使用 HTTPS。除非明确开启不安全传输选项，否则 AgentDesk 会阻止向明文 HTTP 发送凭据。开启后，任何能够观察或修改连接的人都可能获得 Key 与任务内容。
5. 在授权前阅读本机执行警告和每一项权限请求。除非你明确接受以当前 Windows 用户权限自动批准 ACP 工具请求，否则请保持**完全访问**关闭。

默认语言为简体中文，可在设置中选择 English；Web 标签立即切换，原生 WinUI 文本在重启后切换。只有引擎声明支持时才会开放图片提示。扩展与 Marketplace 操作使用和 sidecar 相同的用户权限，因此必须检查来源和确认对话框；签名或目录条目不等于沙箱。远程 Cloud Profile 下，AgentDesk 会阻止所有 Plugin 变更和可能重建或重载注册表的 Marketplace install/update/uninstall 操作，而不会信任 Web UI 提供的发布者 ID。只读目录列表/刷新仍可使用。

设置还提供五档界面字体和可拖拽检查器分隔条，两项选择都会保存在 `%LOCALAPPDATA%\AgentDesk`。开启“完全访问”前必须完成一次原生确认；开启后，宿主会为 ACP 引擎工具请求（包括命令执行）自动选择引擎提供的单次允许选项。它不是工作区边界：工具仍拥有当前 Windows 用户的文件系统与网络权限。关闭后恢复逐次审批，而插件安装、凭据、恢复、明文传输和 Windows Automation 仍分别使用独立安全提示。

Worktree 创建/应用/移除/GC 会改变 Git 状态和文件。请核对来源/目标，在可用时先用 dry-run，并在 overwrite 或清理前保留备份。Windows 通知默认关闭，只包含通用任务状态；点击通知时通过 session ID 查找会话，不会在激活参数中携带工作区路径。

可选 Cloud Profile 同样默认 local-only。远程设置需要明确的 HTTPS 端点（HTTP 仅允许回环开发）、团队/设备标识、原生访问 Token 输入，以及另一设备的恢复密钥配对。配对导入/导出使用原生对话框选择受口令保护的 `.agentdesk-pairing` 文件；宿主会拒绝超限文件、备用数据流、设备路径/名称、reparse point 和变化的 final path，并通过原子替换完成写入。配对包和口令都必须保持私密。

启用 Cloud 后可以向配置的 Server 上传客户端加密会话文档和路由元数据。已接入的开发预览控件可以注册 Runner、对加密任务正文执行入队/领取/完成，并创建/列出/停用加密自动化。它不是生产 Runner、远程沙箱或后台 Push 服务，也不是本地使用的前提。

桌面端会在进程启动后，通过 sidecar 私有的重定向 stdio 通道发送凭据，不会把 Key 放入 sidecar 初始环境。所选模型提供商仍会接收引擎发送的提示词与工具上下文。

## 实验性 Windows Automation 状态

宿主侧 Windows UI Automation 执行器默认关闭。仅在本地启用偏好还不够：已连接的远程团队策略仍可拒绝它，而且每次执行都必须经过明确的**仅允许一次**权限审批。当前执行器接收进程 ID，只支持使用 Automation ID 和/或无障碍名称执行聚焦窗口、调用控件和设置值。输入的 set-value 文本不会回显到权限状态或完成事件。

设置页已经提供三种受支持操作的完整有界控件，但当前安装包不能描述为通用 Computer Use 功能。调用使用当前 Windows 用户的 UI 访问权限；执行器不是沙箱，不隔离目标进程，也不提供自主目标发现。更强的执行中断和针对真实目标的打包后测试仍是发布门禁。

## 升级与回滚

Portable 后台更新检查默认关闭。用户明确 opt-in 后，Portable 构建会周期执行已签名更新检查，并可能暂存已验证更新用于通知；它绝不会自动应用更新或重启 AgentDesk。用户也可以手动触发**检查更新**。两条路径都只有在同时验证更新器和应用 manifest 的固定公钥签名后才会暂存独立更新器。MSIX 构建不会运行这个 Portable 监视器，会报告不支持应用内更新，并继续使用 Windows 包安装流程。

- MSIX 升级遵循 Windows 软件包版本顺序，并要求发布者身份保持一致且可信。
- 可信 Portable 发布可使用应用内检查/应用流程。独立更新器会等待 AgentDesk 退出，验证版本/hash/signature/路径约束，替换 Portable 目录并重启应用。如果不存在已签名 AgentDesk 发布元数据，该流程必须失败，不能信任未签名产物。
- 手动 Portable 新版本应解压到新目录；完成验证前保留旧目录。
- 更改版本前备份 `%LOCALAPPDATA%\AgentDesk`。其中包含 UI 设置与本地会话索引；引擎管理的会话数据可能位于运行时的其他数据目录。

从第二个已签名发布开始，每个 tag 会提供 `AgentDesk-<当前版本>-rollback-to-<上一版本>.zip` 及 `.sha256` 文件。回滚包包含上一版本已签名的 x64/ARM64 MSIX 与 Portable 产物、原始校验和和 SBOM、机器可读清单及双语说明。使用前必须校验回滚归档。Windows 不允许低版本 MSIX 覆盖高版本，因此手动回滚 MSIX 需要先卸载当前包，再安装上一版本已签名包。

首个发布会提供说明文件，明确当前不存在可打包的更早签名版本。

## 移除 AgentDesk

- MSIX：在**设置 > 应用 > 已安装的应用**中移除 AgentDesk。
- Portable：关闭 AgentDesk，确认 sidecar 已退出，再删除解压后的程序目录。

卸载程序不代表会自动删除 `%LOCALAPPDATA%\AgentDesk`、引擎会话、worktree、工作区改动或 Windows 凭据管理器条目。请先备份需要的数据，再单独检查和删除保留内容。

可选自托管 Cloud Server 不属于桌面安装内容。只有用户明确配置后，桌面客户端才会离开 local-only；连接开发预览前请阅读 [cloud/README.zh-CN.md](../cloud/README.zh-CN.md)。
