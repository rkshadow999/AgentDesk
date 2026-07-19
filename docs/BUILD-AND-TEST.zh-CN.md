# 构建与测试 AgentDesk

[English](BUILD-AND-TEST.md) | [简体中文](BUILD-AND-TEST.zh-CN.md)

## 支持的开发环境

桌面构建目标为 Windows 11 x64 与 ARM64，并要求：

- PowerShell 7 与 Git。
- Node.js 24 与 npm。
- Visual Studio 2022 Build Tools，并安装**使用 C++ 的桌面开发**工作负载与 MSVC v143 工具集；构建 ARM64 时还要安装匹配的 C++ ARM64 build tools 组件。Windows Rust target 使用 MSVC linker，只有 .NET SDK 不足以完成构建。
- Windows 11 SDK 10.0.26100，包括 Windows 构建所需的本机库与打包/签名工具。`Microsoft.Windows.SDK.BuildTools` NuGet 包不能替代 Rust 所需的本机 MSVC 工具链。
- `global.json` 选择的 .NET SDK 10.0.302。
- `rust-toolchain.toml` 选择的 Rust 1.92.0。
- 仓库固定的 protobuf compiler 29.3，用于会生成 protobuf 的 Rust 包。请使用 `scripts/agentdesk/Install-Protoc.ps1`；该脚本会先校验受支持归档的 SHA-256，再暴露 `protoc`。

Windows 构建应在 Visual Studio 2022 Developer PowerShell 中运行，或确保当前 PowerShell 已加载匹配的 MSVC v143 与 Windows SDK 环境。排查构建问题前，可用 `dotnet --version`、`rustc --version`、`& $env:PROTOC --version` 和 `where.exe link` 核对实际工具链。

必须使用架构匹配的本机 Rust sidecar。Windows x64 可执行文件不能放入 ARM64 包，Windows 可执行文件也不能作为 WSL payload。ARM64 项目/CI 配置不等于成功 ARM64 构建或真实设备启动证据；只有准确提交对应的 job 和人工检查实际完成后才能作此声明。

## Web 工作台

```powershell
Set-Location desktop/web
npm ci
npm test
npm run build
Set-Location ../..
```

`npm run build` 生成 WinUI 项目使用的发布 Web 资源。不得提交 `node_modules` 或 `desktop/web/dist`。

## 桌面 .NET 测试集

完成 Web 构建后运行：

```powershell
dotnet restore .\desktop\AgentDesk.sln
dotnet test .\desktop\AgentDesk.sln --configuration Release -m:1
dotnet format .\desktop\AgentDesk.sln --verify-no-changes --no-restore
```

解决方案覆盖 Core 契约、ACP/sidecar 行为、Windows 存储、宿主状态、Provider 验证、权限、会话/历史、worktree、扩展、通知、备份/恢复、Portable 更新、加密 Cloud 工作流、Windows Automation 策略/协调器/协议行为、配对包文件 I/O 加固、远程 Plugin/Marketplace fail-closed 策略、关闭和 Web 资产发现。它还包含真实 Kestrel 的桌面/Cloud 集成项目，以及脱敏真实 Provider smoke 工具的单元测试。解决方案应串行运行，因为 CI 会有意避免时间敏感的 sidecar 生命周期测试在多个 testhost 之间争用。这些属于单元、组件或集成测试，不能证明打包后的 FlaUI 执行器已经控制真实目标应用，也不能证明 Narrator、IME、高对比度、缩放或真实打包桌面流程已经执行。

为当前主机构建 WinUI 应用：

```powershell
$platform = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }
dotnet build .\desktop\src\AgentDesk.App\AgentDesk.App.csproj `
  --configuration Release `
  -p:Platform=$platform `
  -p:PackageMode=Portable `
  -p:WindowsPackageType=None
```

## Rust sidecar

系统中没有 `protoc` 时安装固定版本：

```powershell
$env:PROTOC = .\scripts\agentdesk\Install-Protoc.ps1 `
  -Version 29.3 `
  -Destination "$env:TEMP\agentdesk-protoc"
```

运行 AgentDesk 相关格式与契约检查：

```powershell
cargo fmt `
  --package xai-proto-build `
  --package xai-grok-pager-bin `
  --package xai-grok-sandbox `
  --package xai-grok-shell `
  -- --check

cargo test --locked -p xai-proto-build
cargo test --locked -p xai-grok-memory
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
cargo test --locked -p xai-grok-shell --test agentdesk_session_transfer -- --test-threads=1
cargo check --locked -p xai-grok-pager --lib
cargo build --locked -p xai-grok-pager-bin --profile release-dist --features release-dist
```

Windows 引擎输出为 `target/release-dist/xai-grok-pager.exe`。Windows MSVC 构建会为主线程预留 8 MiB 栈；PE 架构错误、可选头无效或 `SizeOfStackReserve` 低于 8 MiB 时，`Test-AgentDeskEngineArchitecture.ps1` 会 fail closed，打包脚本会自动运行该校验。被忽略的本机生命周期测试需要该二进制：

```powershell
$env:GROK_BINARY = (Resolve-Path .\target\release-dist\xai-grok-pager.exe).Path
cargo test --locked `
  -p xai-grok-shell `
  --test test_built_binary_e2e `
  test_windows_agentdesk_stdio_lifecycle_and_clean_shutdown `
  -- --ignored --exact --test-threads=1
```

Linux CI 还会为 `xai-grok-sandbox` 设置 `SANDBOX_E2E_REQUIRE_ENFORCEMENT=1`。只运行 Windows 测试或跳过测试，不能宣称严格沙箱已经强制执行。

## Cloud Server 与桌面客户端

```powershell
dotnet restore .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj
dotnet format .\cloud\src\AgentDesk.Cloud\AgentDesk.Cloud.csproj `
  --verify-no-changes --no-restore
dotnet format .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj `
  --verify-no-changes --no-restore
dotnet test .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj `
  --configuration Release

pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskCloudDatabaseMaintenance.ps1
```

Server 测试集会使用隔离 SQLite 数据库在进程内启动 ASP.NET Core 应用，覆盖认证/角色、不透明 revision 同步、Runner lease、入队/领取/完成、策略、handoff、插件签名、自动化创建/调度和需认证的 SignalR negotiation。桌面解决方案测试还覆盖 Credential Manager Token/恢复密钥适配器、AES-GCM 元数据绑定、回滚检测、加固的配对文件处理、加密 Runner 任务/结果正文、自动化创建/列表/停用、引擎会话导入/导出，以及验证服务器不存储明文的真实 Kestrel 往返。运维 E2E 会启动真实 Cloud 进程，覆盖服务 lease 拒绝、经验证的停机备份、完全相同字节的恢复、回滚证据，以及安装后校验失败时的自动回滚。这些测试不能证明生产 TLS、在线或多实例恢复、生产 Token/密钥运维、生产 Runner 或打包后的多设备行为。

## 仓库与发布契约

```powershell
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskRollbackBundle.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskUpdateManifest.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskReleaseScripts.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskPublicRepository.ps1
git diff --check
```

公开仓库测试要求双语文档、Secret 忽略规则、GitHub 社区文件、上游来源，以及每个相对 `c68e39f` 修改的继承 Rust 文件首行 AgentDesk 修改声明。发布测试覆盖版本映射、架构拒绝、依赖声明闭包、显式非 Docker WSL 发行版选择、MSIX/更新签名门禁、Cloud Server 与运维 CI、更新 manifest 信任，以及回滚哈希/路径/签名失败。

## 打包输入

先构建 Web 资源与本机 sidecar，再运行：

```powershell
.\scripts\agentdesk\Build-AgentDeskPackage.ps1 `
  -Architecture x64 `
  -Mode All `
  -Version 0.1.0-alpha.2 `
  -NativeEnginePath .\target\release-dist\xai-grok-pager.exe `
  -OutputRoot .\artifacts\agentdesk `
  -SourceRepository https://github.com/rkshadow999/AgentDesk `
  -SourceRevision (git rev-parse HEAD)
```

只有引擎和构建主机都是 ARM64 时才能使用 `arm64`，并且必须记录成功 CI 与真实 ARM64 启动证据后才能作支持声明。`-WslEnginePath` 只能接收架构匹配的 64 位小端 Linux ELF payload。打包时会自动运行 `Test-AgentDeskLinuxEngineArchitecture.ps1`，遇到格式损坏或 x64/ARM64 架构不匹配时 fail-closed。通过校验的 payload 会为未来严格模式随包提供，但当前桌面 health 门禁仍会阻止 `WslStrict`。

不传 `-CertificatePath` 与 `-CertificatePassword` 时，MSIX 保持未签名，只适用于开发。它不是正式发布包，不得按正式包分发。包输入还包含独立单文件 Portable 更新器；只有已签名 Tag 元数据可以建立其信任通道。正式 Tag 由 CI 打包，要求 MSIX 与更新签名 Secret、验证两条信任链、生成 SPDX/CycloneDX SBOM、最终归档和 SHA-256，并发布经过验证的上一版本回滚包。

打包脚本支持不写文件的验证路径：

```powershell
.\scripts\agentdesk\Build-AgentDeskPackage.ps1 `
  -Architecture x64 `
  -Mode All `
  -NativeEnginePath .\missing-engine.exe `
  -OutputRoot .\artifacts\dry-run `
  -DryRun
```

## CI 证据

| Job | 平台 | 产生的证据 |
| --- | --- | --- |
| `linux-sidecar` | Ubuntu 22.04 x64/ARM64 | Rust 契约与强制沙箱测试、真实 strict sidecar health fail-closed 探测、ELF/GLIBC 检查、Linux SBOM 与校验和 |
| `cloud-tests` | Ubuntu 24.04 | Release 配置 Cloud Server 集成测试集 |
| `cloud-maintenance` | Windows 2025 | 真实进程停机数据库备份/恢复/回滚 E2E，包括 lease 拒绝与失败恢复自动回滚 |
| `windows-build` | Windows 2025 x64 / Windows 11 ARM | Web 测试/构建、全部桌面测试项目（含 Cloud Client/更新器/Provider smoke 测试）、Rust 契约/生命周期、本机与 WSL 包输入、签名门禁 |
| `assemble-release` | Ubuntu 24.04 | Portable zip、独立更新器、MSIX、SPDX/CycloneDX 配套文件、签名状态、更新 manifest/signature、SHA-256 清单 |
| `github-release` | Windows 2025，仅 Tag | 当前/上一 MSIX Authenticode/Publisher 重新验证、签名产物发布和回滚包 |

只有 workflow 针对准确提交成功运行后，CI 配置才构成证据。本地 x64 测试通过不是 ARM64 证据，未签名分支产物也不是签名发布。

## 已记录的本地证据

截至 2026-07-19，最近记录的 Windows x64 组件级验证包括：

| 检查 | 已记录结果 | 边界 |
| --- | --- | --- |
| Web 界面 | `245/245` 通过；`npm run build` 通过 | React/WebView2 组件与发布资源构建；不是打包 WinUI 运行证据 |
| Core、Engine、App 与 Windows 平台测试集 | `37/37`、`215/215`、`627/627`、`53/53` 通过 | 源码级契约、宿主投影与 Windows 服务；不是打包 UI 运行证据 |
| Cloud Client 与真实 Kestrel 集成 | `116/116`、`3/3` 通过 | 加密客户端行为与回环 Server 集成；不是生产 Cloud 证据 |
| Updater Core、Provider smoke 与 Process Job launcher 测试集 | `114/114`、`8/8`、`1/1` 通过 | 更新器/Provider/进程约束工具行为；不是签名更新通道、真实 Provider 结果或打包后进程逃逸证明 |
| Cloud Server | `37/37` 通过 | 进程内开发预览 Server；不是生产部署证据 |
| Cloud 数据库运维 E2E | 通过 | 真实进程停机备份、完全相同字节的恢复、回滚证据与失败恢复自动回滚 |
| Rust proto、ACP 契约、Marketplace 聚焦回归、Memory 与会话迁移测试 | `4/4`、`12/12`、`2/2`、`292/292`、`3/3` 通过 | ACP 契约结果使用必需的 `--test-threads=1`；Marketplace 聚焦筛选属于该契约，不能再相加成人为总数 |
| 发布契约与 WebView2 CDP helper | 通过；CDP helper `15/15` | 验证脚本与测试 helper，不代表 GitHub 发布或打包后人工 UX |
| 最终 sidecar 与授权真实 Provider | x64 SHA-256 `1DB8E791EEB84FF75581374916403BC084F8D44CD58BD1795636AFFEE9C906A8`；PE 栈 8 MiB；`/models` HTTP 200、4,250 ms；Chat Completions 启动 3,609 ms、握手 749 ms、提示 45,836 ms、15 个流式事件，取消 15,654 ms 并返回 `Cancelled`；无效模型以 `JsonRpcException` 受控失败；诊断中无凭据 | 用户明确授权提供的兼容端点使用明文 HTTP。耗时取决于 Provider 与网络，未保留响应正文，也不构成通用性能或传输安全声明 |

Release 桌面解决方案已以退出码 0 完成，应用构建为 0 warning、0 error。不得把不同范围的测试集相加后发布一个人为总数。ACP 契约必须使用 `--test-threads=1`；测试会有意共享进程级 `OnceLock`，因此默认并行结果不能作为发布证据。补齐声明后的发布二进制重建与脱敏真实 Provider 门禁已经完成。另一次授权的 Responses 后端运行也成功产生流式事件、确认取消、干净退出，并且诊断中未出现凭据。

这些结果不能证明签名公开 MSIX、成功 GitHub 推送、真实 ARM64 硬件、真实 WSL2 环境中可用的 `WslStrict`、打包后无障碍、真实目标 Windows Automation、生产 Runner 隔离、生产 Cloud 运行，或与任何官方 Codex 私有服务等价。发布前必须针对最终提交和安装包重新执行全部适用命令。

## 真实服务测试规则

- 使用本地 mock OpenAI 兼容服务器确定性覆盖 401、429、取消、流式响应和工具调用。
- 绝不把真实 API Key 放入源码、命令历史、测试夹具、截图、workflow 日志或 Issue。
- 手动真实提供商冒烟测试必须使用 HTTPS；除非测试者明确接受明文传输风险。只报告脱敏状态和耗时，不得报告请求/响应正文。
- 发布前还需分别完成 x64/ARM64 打包冒烟、真实目标 Windows Automation 的允许/拒绝/取消检查、中文 IME、纯键盘、Narrator、高对比度、125%-200% 缩放、崩溃恢复和更新/回滚检查。这些人工门禁目前尚未由仓库自动化。

Smoke 工具只从 `GROK_THIRD_PARTY_API_KEY` 读取 Key，在启动 sidecar 前从子进程环境移除它，检查握手、流式提示、取消、干净退出和诊断 Secret 扫描，并输出不含响应正文的有界 JSON。Key 应由 Secret Manager 或父进程设置，不能作为命令字面量。以下为 HTTPS Responses 端点示例：

```powershell
if (-not $env:GROK_THIRD_PARTY_API_KEY) {
  throw "请先通过进程级 Secret 来源设置 GROK_THIRD_PARTY_API_KEY。"
}

$env:AGENTDESK_REAL_PROVIDER_BASE_URL = "https://provider.example/v1"
$env:AGENTDESK_REAL_PROVIDER_MODEL = "provider-model-id"
$env:AGENTDESK_REAL_PROVIDER_BACKEND = "responses"
$env:AGENTDESK_REAL_PROVIDER_ALLOW_INSECURE_HTTP = "0"
$env:AGENTDESK_REAL_PROVIDER_ENGINE = (Resolve-Path `
  .\target\release-dist\xai-grok-pager.exe).Path
$env:AGENTDESK_REAL_PROVIDER_WORKSPACE = (Resolve-Path .).Path

try {
  dotnet run --project .\desktop\tools\AgentDesk.ProviderSmoke\AgentDesk.ProviderSmoke.csproj `
    --configuration Release
}
finally {
  @(
    "GROK_THIRD_PARTY_API_KEY",
    "AGENTDESK_REAL_PROVIDER_BASE_URL",
    "AGENTDESK_REAL_PROVIDER_MODEL",
    "AGENTDESK_REAL_PROVIDER_BACKEND",
    "AGENTDESK_REAL_PROVIDER_ALLOW_INSECURE_HTTP",
    "AGENTDESK_REAL_PROVIDER_ENGINE",
    "AGENTDESK_REAL_PROVIDER_WORKSPACE"
  ) | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
}
```

只有明确授权的明文 HTTP 测试才能把 Base URL 设为对应测试端点，并设置 `AGENTDESK_REAL_PROVIDER_ALLOW_INSECURE_HTTP=1`。该选项只绕过传输拒绝，不会让 HTTP 具备保密性或抗篡改能力，不能用于常规发布冒烟。
