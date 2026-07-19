# AgentDesk Windows 客户端

[English](README.md) | [简体中文](README.zh-CN.md)

> [!WARNING]
> AgentDesk Alpha 当前只开放本机兼容模式（非沙箱，`NativeProtected`）。
> `WslStrict` 会有意保持 fail-closed（失败即关闭），直至能够证明全部子进程都受到网络限制。
> 仅对你信任的工作区使用本机执行。

## 架构

AgentDesk 桌面端使用 .NET 10、WinUI 3 和 WebView2。Rust 引擎通过 ACP/NDJSON stdio 作为独立 sidecar 运行，桌面端不会直接链接上游 Rust 内部 API。

WinUI 宿主负责应用窗口、工作区选择器、Windows 凭据管理器集成、sidecar 生命周期、执行配置强制措施、权限决定、Windows 通知、实验性 Windows UI Automation、Cloud 协调与 WebView2 边界。React 界面提供工作台和检查器。共享 .NET 契约不依赖 UI 层。

桌面端管理的 API Key 不会进入本机或 WSL sidecar 的初始环境。宿主会通过 sidecar 私有的重定向 stdio 通道，在标准 ACP `initialize` 请求前调用逻辑扩展名 `agentdesk/v1/credential`；NDJSON 传输会按 ACP 扩展方法要求在线缆上写入 `_agentdesk/v1/credential`。引擎只将 Key 保存到进程内存槽且不会回显。凭据桥接缺失、被拒绝或响应格式错误时，启动会在认证和创建会话前中止。

当前源码 UI 包含执行/计划模式协商、可搜索/分页的引擎会话、加载/重命名/归档/分叉/压缩/回退、有界会话导入/导出、本地备份/恢复、Provider Base URL/模型/backend 设置、经过验证的图片附件、有界工作区文件引用、`AGENTS.md` 编辑器、按能力启用的 Memory 浏览器、终端流、diff/计划检查、权限、取消、语言选择和可选通知。Memory 写入和删除必须使用引擎声明的两阶段确认契约，宿主会拒绝不受确认保护的修改能力。活动会话 Runtime Dashboard 可列出后台任务与运行中 subagent、kill 任务、打开 subagent 详情并 cancel subagent。

Worktree 视图已接入创建/列表/详情/应用/移除/GC，包括 destination、copy mode、Git ref、merge/overwrite 确认、冲突和 dry-run 清理。审查有意采用两阶段流程：第一次操作只生成有界、可编辑的请求，独立的“开始审查”操作才会按标准 `engine/prompt` 提交，因此仍经过本机风险确认和引擎权限队列。设置页通过有界桥接接入 MCP、Skills、Hooks、Plugins 与 Marketplace 目录/操作。环境变量名可以作为 Secret 引用跨过桥接；Secret 值、Hook 命令/URL 和 Skill metadata 不会投影到 WebView2。远程 Cloud Profile 会对所有 Plugin 变更，以及可能重建或重载注册表的 Marketplace install/update/uninstall 操作 fail-closed；客户端提供的发布者 ID 不可信，目录列表/刷新仍可使用。

Portable 构建提供使用固定公钥验证的更新流程与独立更新器。手动检查始终可用；后台可用更新轮询会持久化设置但默认关闭，只有用户明确 opt-in 后才启动，也不会自动应用更新。MSIX 不使用该替换路径。可选 Cloud Client 默认 local-only，支持明确的远程设置、加密会话同步/导入、加固的恢复密钥配对、跨设备 handoff、策略、Runner 注册/入队/领取/完成，以及针对独立开发预览 Server 的自动化创建/列表/停用。

组件与数据归属见[架构文档](../docs/ARCHITECTURE.zh-CN.md)，安全假设见[威胁模型](../docs/AGENTDESK-THREAT-MODEL.zh-CN.md)。

## 前置条件

- x64 或 ARM64 架构的 Windows 11。ARM64 配置仍需要准确发布对应的成功 ARM64 CI 与真实设备启动证据。
- PowerShell 7 和 Git。
- Node.js 24 与 npm。
- .NET SDK 10.0.302，由 [`global.json`](../global.json) 固定。
- Rust 1.92，由 [`rust-toolchain.toml`](../rust-toolchain.toml) 固定。
- Microsoft Edge WebView2 Runtime，受支持的 Windows 11 安装中已包含。
- 仅在准备当前仍受阻断的 `WslStrict` payload 时需要 WSL2。

项目以 Windows SDK 10.0.26100 为目标，框架最低版本为 10.0.19041，但 AgentDesk 社区目前只测试和支持 Windows 11。

## 本地构建

先构建 Web 资源和 Rust sidecar，再运行参数化打包入口：

```powershell
Set-Location desktop/web
npm ci
npm test
npm run build
Set-Location ../..

cargo build --locked -p xai-grok-pager-bin --profile release-dist --features release-dist

./scripts/agentdesk/Build-AgentDeskPackage.ps1 `
  -Architecture x64 `
  -Mode All `
  -NativeEnginePath ./target/release-dist/xai-grok-pager.exe `
  -WslEnginePath C:\path\to\linux-x64\agentdesk-engine `
  -OutputRoot ./artifacts/agentdesk
```

只有在 ARM64 构建主机上才能使用 `-Architecture arm64` 与匹配的 Linux ARM64 payload；CI 与真实设备启动通过前不得声称已验证。`-WslEnginePath` 接受来自 `agentdesk-engine-linux-x64` 或 `agentdesk-engine-linux-arm64` CI artifact、与架构匹配的 Linux 二进制。省略该参数时，本地包只开放本机兼容模式（非沙箱），也不会包含 WSL 安装脚本。

[`AgentDesk.App.csproj`](src/AgentDesk.App/AgentDesk.App.csproj) 支持 `PackageMode=Portable` 和 `PackageMode=MSIX`。构建脚本会将本机 sidecar 重命名为应用默认路径 `agentdesk-engine.exe`，并通过 [`AgentDesk.Packaging.targets`](../scripts/agentdesk/AgentDesk.Packaging.targets) 将引擎和法律声明加入 MSIX。运行时使用 `Path.Combine(AppContext.BaseDirectory, "agentdesk-engine.exe")` 解析本机引擎，因此桌面 controller 在默认本机配置下不需要自定义路径。

准确 restore/test 命令以及本地证据与 CI 证据的区别见[构建与测试](../docs/BUILD-AND-TEST.zh-CN.md)。安装、校验、升级和移除软件包见[安装指南](../docs/INSTALLATION.zh-CN.md)。

## 执行配置

### NativeProtected

`NativeProtected` 是为兼容 Windows 工具链而保留的协议枚举。界面将它标为**本机兼容模式（非沙箱）**。它使用独立的 AgentDesk 数据目录、清理继承的凭据环境变量、保留权限审批，并将 sidecar 加入启用 kill-on-close 的 Windows Job Object，使宿主释放句柄或崩溃时都能终止进程树。

它仍以当前 Windows 用户的完整文件系统与网络权限运行，不构成安全边界，也不能描述为沙箱。不得在此模式中打开陌生或不可信的仓库。

### WslStrict

Windows 可执行文件不能作为 WSL sidecar 使用。Portable 包可以包含 `wsl/agentdesk-engine`，即与架构匹配的 Linux x64 或 ARM64 二进制。Alpha 不会自动导入它。从解压后的 Portable 包开始安装时，命令为：

```powershell
./Install-AgentDeskWslEngine.ps1
# 安装了多个可用发行版时：
./Install-AgentDeskWslEngine.ps1 -DistributionName Ubuntu
```

安装器会忽略 Docker Desktop 的内部发行版。恰好存在一个非 Docker 发行版时会自动选择；否则必须显式传入 `-DistributionName`。非 root 发行版通过 `sudo` 安装，root-only 测试或自定义发行版则直接安装。payload 会写入 `/usr/local/bin/agentdesk-engine`、设置可执行权限，并与随包源文件做 SHA-256 一致性校验。如果系统存在多个可用发行版，启动 AgentDesk 前还要把 `AGENTDESK_WSL_DISTRIBUTION` 设为同一个已安装名称。桌面端只执行这个发行版内路径；可用性还要求源文件是当前架构 ELF，且安装文件 SHA-256 与随包 payload 完全一致。路径转换与 sidecar 启动都会使用同一个显式发行版；选择缺失/有歧义、安装缺失/过期/不可执行或架构不匹配时都会 fail-closed。MSIX 包含同一 payload，但当前界面无法从受保护的安装目录导入它，因此 Alpha 安装脚本仅支持从 Portable 包运行。

桌面启动 WSL sidecar 时会固定 `GROK_SANDBOX=strict` 和 `GROK_SANDBOX_REQUIRE_ENFORCEMENT=1`。Landlock/Seatbelt 强制措施必须实际生效，否则引擎退出，不能静默降级。当前 Alpha 会选择一个具名的现有发行版，但尚未自动创建关闭 Windows interop 与 automount 的专用发行版，因此该配置不能替代未来的隔离 Runner。

启动流程会在认证或创建会话前调用 `agentdesk/v1/health`。只有引擎返回完整的结构化证明，并同时满足以下条件时，桌面端才允许 `WslStrict` 继续：

- `configuredProfile` 和 `activeProfile` 都是 `strict`。
- `active`、`childNetworkRestricted` 和 `enforcementRequired` 都是 `true`。

如果缺少 `agentdesk/v1/initialize` 或 `agentdesk/v1/health`、返回 `-32601`、遗漏证明字段，或任一条件不满足，桌面端会在认证和创建会话前停止 WSL sidecar，绝不会自动降级到本机兼容模式（非沙箱）。早于本仓库 AgentDesk 扩展的 WSL payload 与当前客户端不兼容，必须替换为同一发布版本附带的 payload。本机兼容模式（非沙箱）不要求 WSL health 证明，但桌面端管理的凭据仍要求版本化凭据桥接。

当前 seccomp 网络限制覆盖已接入的命令启动路径，但尚未证明 helper、插件、Hook、PTY 以及所有其他子进程入口都得到完整覆盖。因此引擎会报告 `childNetworkRestricted: false`，桌面端会拒绝握手并停止 sidecar。Alpha 中的 `WslStrict` 有意采用 fail-closed；只有全局子进程网络限制可验证，并且 health 响应能够如实返回 `true` 后，该模式才会开放。

## 实验性 Windows Automation

原生 FlaUI/UIA3 执行器、宿主桥接和设置页操作面支持针对明确进程 ID，以及 Automation ID 和/或无障碍名称执行聚焦窗口、调用控件和设置值。该路径默认关闭，必须同时通过本地偏好和当前团队策略，操作会串行执行，并在触碰目标前显示“仅允许一次”权限审批。完成/状态事件不会回显输入值，检查器 WebView2 也不会收到自动化或权限事件。

该执行器使用当前 Windows 用户的 UI 权限。`NativeProtected` 是本机兼容模式（非沙箱），不能隔离该执行器；执行器不提供操作系统隔离，也不是通用自主 Computer Use 实现。单元/组件测试覆盖验证、策略、权限关联、取消、事件脱敏和宿主路由；打包后针对真实目标的 FlaUI 测试仍是人工发布门禁。

## 测试

运行会检查打包界面发现逻辑的 .NET 测试前，先构建 Web 资源：

```powershell
Set-Location desktop/web
npm ci
npm test
npm run build
Set-Location ../..

dotnet test desktop/AgentDesk.sln -m:1
dotnet format desktop/AgentDesk.sln --verify-no-changes --no-restore

cargo fmt --package xai-proto-build --package xai-grok-pager-bin `
  --package xai-grok-sandbox --package xai-grok-shell -- --check
cargo test --locked -p xai-proto-build
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
cargo check --locked -p xai-grok-pager --lib

pwsh ./scripts/agentdesk/Test-AgentDeskReleaseScripts.ps1
pwsh ./scripts/agentdesk/Test-AgentDeskUpdateManifest.ps1
pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1
git diff --check
```

强制执行的 Linux 沙箱测试集在已配置的 Linux 或 Docker 环境中运行，并设置 `SANDBOX_E2E_REQUIRE_ENFORCEMENT=1`。

最新本地证据记录在[构建与测试](../docs/BUILD-AND-TEST.zh-CN.md)中，并会在发布前重新生成。公开源码仓库为 [rkshadow999/AgentDesk](https://github.com/rkshadow999/AgentDesk)。这不代表已获得签名公开 MSIX、ARM64 真实硬件、打包后无障碍或生产 Cloud/Runner 证据。

## CI 与发布

`.github/workflows/agentdesk-windows.yml` 执行：

- Windows x64 和 ARM64 的 Node.js 24、.NET 10 与 Rust 1.92 测试和构建。
- protobuf 29.3 的固定版本下载与 SHA-256 校验。
- Linux x64 和 ARM64 WSL sidecar 构建，并与对应 Windows 包配对。
- 在 Ubuntu 22.04 上构建 Linux sidecar，检查 ELF 架构及最高 `GLIBC_2.35` 依赖，并记录到 `RUNTIME-COMPATIBILITY.txt`。
- 为分支和 Pull Request 生成 Portable 包、独立 Updater 输入、未签名 MSIX、SPDX/CycloneDX SBOM、SHA-256 文件与归档。
- 为 `v*` Tag 发布 GitHub Release，包括已签名更新 manifest，并从第二个 Tag 起附带经过验证的上一签名 Windows 发布回滚包。
- 对可选自托管云端开发预览运行 Release 配置集成测试。
- Windows 真实进程 Cloud 运维作业，覆盖停机备份、恢复、回滚证据，以及安装后校验失败时的自动回滚。

这些只是 workflow 定义，不代表发布已经实际运行。源码仓库已经公开，但当前审计不声称存在签名 Tag MSIX 或已验证的 ARM64 设备启动。

发布版本只接受稳定版 `major.minor.patch`，或带编号的 `ci`、`alpha`、`beta`、`preview` 和 `rc` 预发布版本。每个预发布通道映射到互不重叠的 MSIX revision 区间。稳定版使用 revision `65535`，因此稳定包可以升级相同 `major.minor.patch` 的预发布包。其他后缀会被拒绝。

分支和 Pull Request 构建生成未签名 MSIX，仅用于开发与 CI 验证。`v*` Tag 必须配置以下 MSIX 签名对与更新签名密钥；缺少任意一项时，workflow 会失败，不会发布未签名 Tag MSIX 或未签名更新元数据：

- `AGENTDESK_MSIX_PFX_BASE64`
- `AGENTDESK_MSIX_PFX_PASSWORD`
- `AGENTDESK_UPDATE_ECDSA_PRIVATE_KEY_PKCS8_BASE64`

验证器把仓库受信 Publisher 固定为 `CN=AgentDesk`，并分别将包内 manifest 与实际 signer subject 对照该固定值；不会从待验证包中推导预期身份。可选仓库变量 `AGENTDESK_MSIX_SIGNER_THUMBPRINT` 可再固定证书指纹。更新元数据使用 ECDSA P-256 签名，并独立对照 `desktop/update` 中固定的公钥检查。Updater 资产为包含 `AgentDesk.Updater.exe` 的 zip，与 updater core 的解压契约一致。PFX/私钥只在 runner 临时目录中生成临时文件，并在 workflow 结束时删除。任何证书或密钥都不得提交到仓库。

每个 artifact 目录包含 `AgentDesk-<版本>-win-<架构>-MSIX-SIGNING-STATUS.txt`，值为 `signed` 或 `unsigned`。未签名 MSIX 不是正式签名发布包。

发布包包含根目录的 `LICENSE` 与 `THIRD-PARTY-NOTICES`、桌面端依赖声明、中英文源码可获取性说明，以及记录准确源码仓库和提交的 `SOURCE-REVISION.txt`。

AgentDesk 不会静默应用更新。Portable 后台可用更新检查默认关闭，只有用户在设置中明确 opt-in 后才运行；固定 P-256 信任根验证更新元数据后，它只发布版本通知，应用更新仍必须由用户显式触发。MSIX 会报告不支持应用内路径。从第二个已签名 Tag 开始，CI 会下载上一条已发布 Windows 资产，校验两个架构的 SHA-256 清单和签名状态，再发布包含双语手动说明的 `AgentDesk-<当前版本>-rollback-to-<上一版本>.zip` 及其 `.sha256`。Windows 不允许低版本覆盖高版本，因此 MSIX 降级必须手动完成。

## 项目结构

| 路径 | 职责 |
| --- | --- |
| `src/AgentDesk.App` | WinUI 应用、WebView2 宿主/桥接、Windows Automation、Cloud 协调、通知与原生对话框 |
| `src/AgentDesk.Core` | 不依赖 UI 的引擎、执行与安全契约 |
| `src/AgentDesk.Engine` | ACP 传输与本机/WSL sidecar 生命周期 |
| `src/AgentDesk.Platform.Windows` | Credential Manager、SQLite、设置、备份与 Windows 集成 |
| `src/AgentDesk.Cloud.Client` | 加密自托管 Cloud Client 与恢复密钥工作流 |
| `src/AgentDesk.Updater.Core`、`src/AgentDesk.Updater` | 已签名 Portable 更新验证与替换 |
| `tests` | .NET 单元与集成测试 |
| `web` | React 工作台、检查器、Monaco 与 xterm.js 界面 |
| `../scripts/agentdesk` | 打包、签名、WSL 安装与验证脚本 |

## 许可证与源码

请参阅根目录的[许可证与来源说明](../README.zh-CN.md#许可证与来源)、[桌面端依赖声明](THIRD-PARTY-NOTICES.md)和[第三方源码可获取性说明](THIRD-PARTY-SOURCE-NOTICE.zh-CN.md)。安全敏感问题必须遵循根目录的[安全策略](../SECURITY.zh-CN.md)。独立的 [Cloud 开发预览](../cloud/README.zh-CN.md)是可选组件，默认关闭，只有明确配置远程 Profile 后才由桌面端使用。
