# 发布 AgentDesk

[English](RELEASING.md) | [简体中文](RELEASING.zh-CN.md)

这是目标公开仓库 `rkshadow999/AgentDesk` 的维护者清单。只有 tag workflow 成功，并且发布资产经过独立检查，发布才算完成。不得把失败或未签名 CI 产物手动改成正式发布。

## 当前发布证据

公开源码仓库已经建立在 [rkshadow999/AgentDesk](https://github.com/rkshadow999/AgentDesk)。在准确 Tag 的 workflow 成功且发布资产经过独立检查之前，仍不声称存在签名公开 MSIX、已发布更新通道或 Release Tag。

最新 Windows x64 证据包括：Web `245/245` 且 Web 构建成功；Core `37/37`、Engine `215/215`、App `627/627`、Windows 平台 `53/53`、Cloud Client `116/116`、真实 Kestrel `3/3`、Updater Core `114/114`、Provider smoke 单元测试 `8/8`、Process Job launcher `1/1`、Cloud Server `37/37`；桌面 Release solution 退出码为 0，应用构建为 0 warning、0 error。Rust proto/ACP 契约/Marketplace 聚焦回归/Memory/会话迁移记录为 `4/4`、`12/12`、`2/2`、`292/292`、`3/3`。ACP 契约结果使用必需的 `--test-threads=1`；这些测试会有意共享进程级 `OnceLock`，因此默认并行结果不能作为发布证据。真实进程 Cloud 数据库运维 E2E、发布契约脚本、最终 x64 sidecar/8 MiB 栈门禁，以及脱敏真实 Chat Completions 与 Responses Provider 冒烟均已通过，且没有保留响应正文或凭据。本地 package input 和未签名 MSIX 产物仍仅供开发。ARM64 已有项目/workflow 配置，但当前源码树没有真实设备启动记录。`NativeProtected` 发布时必须标为**本机兼容模式（非沙箱）**，`WslStrict` 仍保持 fail-closed，且没有真实 WSL2 环境中的可用结果。Portable 后台检查默认关闭，只有 opt-in 后才周期运行，并且绝不会自动应用更新；worktree 审查会先进入独立的可编辑准备步骤，再走标准提示词/权限链。Cloud Server、Runner 工作流与 Windows Automation 执行器仍为实验性，不构成生产发布声明，也不等价于官方 Codex 私有服务。

## 一次性仓库设置

仓库必须公开，并以 `main` 作为受保护默认分支。启用 Issues、Discussions、自动删除 head branch 与 GitHub Private Vulnerability Reporting，关闭未使用的 Wiki，并设置公开发布计划中的项目 Topic。

`main` 应要求 Pull Request、AgentDesk CI 成功、对话已解决，并禁止 force push/删除。只允许维护者创建 tag/release。条件允许时使用受保护 GitHub Environment 保存发布签名 secret，并要求 reviewer 批准。

可选的打包后 WebView2 门禁不得使用长期在线的 runner 或普通开发机。请从可信快照创建只执行一次已审查非 PR 任务的干净 x64 一次性 VM，并在其已登录 Windows 用户的前台交互会话中启动 JIT/ephemeral 自托管 runner。除标准 `self-hosted`、`Windows`、`X64` 标签外，还要添加 `agentdesk-interactive`；不得把它安装为 Windows 服务。必须使用专用非管理员本地账户，不得包含个人/签名凭据、SSH Agent、网络共享、可复用磁盘或内网访问，并在任务后立即销毁 VM 与注册。无法满足全部控制时，`AGENTDESK_RUN_INTERACTIVE_GUI_SMOKE` 必须保持为 `false`。仓库中的非 PR 条件只是纵深防护，并不能隔离可修改 workflow 的公开仓库；注册 runner 前要检查排队任务，GitHub 套餐支持时还应把 runner group 限定为此 workflow。即使未启用该可选 Job，托管 x64/ARM64 Job 仍会运行 CDP helper 测试、打包依赖闭包与产物上传。启用变量后，`github-release` 会等待此门禁，失败时不得发布；主动跳过的可选门禁则不阻断发布。

Tag 打包需要以下仓库 Secret：

- `AGENTDESK_MSIX_PFX_BASE64`：代码签名 PFX 的 Base64。
- `AGENTDESK_MSIX_PFX_PASSWORD`：PFX 密码。
- `AGENTDESK_UPDATE_ECDSA_PRIVATE_KEY_PKCS8_BASE64`：ECDSA P-256 私钥的 PKCS#8 DER Base64。

仓库固定信任的 MSIX Publisher 是 `CN=AgentDesk`；待验证包及其 manifest 都不能替换这个信任值。可选仓库变量 `AGENTDESK_MSIX_SIGNER_THUMBPRINT` 固定当前 Tag 使用的证书。存在已签名发布后，必需变量 `AGENTDESK_PREVIOUS_MSIX_SIGNER_THUMBPRINT` 固定用于回滚的紧邻上一版本证书。证书轮换时，当前 pin 设置为新的 40 位十六进制 SHA-1 指纹，而 previous pin 继续指向旧证书；新发布完成独立验收后，再为下一 Tag 推进 previous pin。PFX 必须保存在仓库外，只有回滚窗口不再依赖旧材料后才能删除失效 secret。分支和 Pull Request 构建不能获得发布签名 secret。

更新签名密钥必须离线生成并托管。私钥只能存在于受保护的 Secret Store，或 runner/系统临时目录中的短生命周期文件；不得提交到仓库、放入 workflow 命令参数或打印其 Base64。经过审查的当前公钥 pin 提交在 `desktop/update/AgentDesk-update-public-key.spki.base64`，其 SPKI SHA-256 指纹为 `a7350091fed6493ac0aa0d6222b4f2e0b80eb365c70fcf89d9040276e47b6e15`。Tag workflow 会在发布两份签名前证明私钥 Secret 与仓库 pin 匹配。存在已签名发布后，必需仓库变量 `AGENTDESK_PREVIOUS_UPDATE_PUBLIC_KEY_SHA256` 必须保存紧邻上一版本所发布 `AgentDesk-update-public-key.spki` 的 64 位十六进制 SHA-256。回滚验证会下载该旧 SPKI，但只有其稳定快照与这个独立 pin 一致时才会信任。

轮换更新密钥时，必须先完成经过审查的客户端信任迁移，再发布只使用新密钥签名的元数据。将仓库内当前 pin 和私钥 Secret 切换到新密钥时，首个轮换后 Tag 的 `AGENTDESK_PREVIOUS_UPDATE_PUBLIC_KEY_SHA256` 仍须指向旧发布密钥；该发布及其回滚包完成独立验收后，再把 previous-key 变量推进为新发布 SPKI 的 SHA-256，供下一个 Tag 使用。

## 版本规则

Tag 使用 `v<版本>`。接受的版本为：

- 稳定版：`major.minor.patch`，例如 `v0.1.0`。
- 预发布：`ci`、`alpha`、`beta`、`preview` 或 `rc` 加十进制编号，例如 `v0.2.0-alpha.1`。

其他后缀、build metadata、四段输入版本、前导零编号或超过 MSIX 区间的编号会被 `Build-AgentDeskPackage.ps1` 拒绝。Tag 带且只带一个前导 `v`，但直接传给打包脚本的 `-Version` 值必须省略它。稳定版映射到 MSIX revision `65535`，高于同一三段版本的预发布包；发布顺序为 `ci < alpha < beta < preview < rc < stable`。

## 发布前检查

在准备进入 `main` 的干净提交上运行：

请在 Visual Studio 2022 Developer PowerShell 中执行，并确保已安装“使用 C++ 的桌面开发”工作负载、MSVC v143 与 Windows 11 SDK 10.0.26100。仓库通过 `global.json` 固定 .NET SDK 10.0.302，通过 `rust-toolchain.toml` 固定 Rust 1.92.0；运行 Rust 命令前安装并导出仓库固定的 `protoc` 29.3：

```powershell
$env:PROTOC = .\scripts\agentdesk\Install-Protoc.ps1 `
  -Version 29.3 `
  -Destination "$env:TEMP\agentdesk-protoc"

Set-Location desktop/web
npm ci
npm test
npm run build
Set-Location ../..

dotnet test .\desktop\AgentDesk.sln --configuration Release -m:1
dotnet format .\desktop\AgentDesk.sln --verify-no-changes --no-restore
dotnet format .\cloud\src\AgentDesk.Cloud\AgentDesk.Cloud.csproj --verify-no-changes --no-restore
dotnet format .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj --verify-no-changes --no-restore
dotnet test .\cloud\tests\AgentDesk.Cloud.Tests\AgentDesk.Cloud.Tests.csproj --configuration Release
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskCloudDatabaseMaintenance.ps1

cargo fmt --package xai-proto-build --package xai-grok-pager-bin `
  --package xai-grok-sandbox --package xai-grok-shell -- --check
cargo test --locked -p xai-proto-build
cargo test --locked -p xai-grok-memory
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
cargo test --locked -p xai-grok-shell --test agentdesk_session_transfer -- --test-threads=1
cargo check --locked -p xai-grok-pager --lib
cargo build --locked -p xai-grok-pager-bin --profile release-dist --features release-dist

pwsh -NoProfile -File .\scripts\agentdesk\Generate-AgentDeskThirdPartyNotices.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskUpdateManifest.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskRollbackBundle.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskReleaseScripts.ps1
pwsh -NoProfile -File .\scripts\agentdesk\Test-AgentDeskPublicRepository.ps1
git diff --check
git status --short
```

Notice 生成器不能留下非预期 diff。审查每一项预期差异、依赖更新、二进制资产、上游修改声明和双语文档。确认 Windows Automation 始终显式启用、每次仅允许一次且不回显输入值；配对文件测试覆盖路径替换与原子写入；worktree 审查仍要求先准备可编辑请求，再进入标准提示词/权限链；Portable 后台更新检查仍默认关闭、只有 opt-in 后才周期运行且绝不会自动应用；远程 Cloud Profile 对所有可能加载代码的 Plugin/Marketplace 操作 fail-closed。完成路线图记录的 x64/ARM64 打包、真实目标 Windows Automation 与无障碍人工检查，不能用单元测试推断这些结果。

确认仓库不含真实 API Key、Cloud Token、PFX、密码、提示词、私有源码、用户名或本地路径。发布 workflow 绝不能使用真实生产凭据进行测试。另行授权的真实 Provider smoke 可以按[构建与测试](BUILD-AND-TEST.zh-CN.md#真实服务测试规则)中的仅环境变量流程在开发者进程运行；只能保留脱敏状态/耗时输出。

`Build-AgentDeskPackage.ps1` 会针对所选 RID 发布 self-contained single-file Updater，并放在 `package-input-<架构>/update-staging/AgentDesk.Updater.exe`。该目录刻意位于可替换的 `portable` 应用负载之外，避免运行中的 Updater 随待替换应用一起移动。本地未签名 package input 会包含 `DEVELOPMENT-ONLY.txt`，不得作为受信更新通道发布。只有同时获得两项可信密钥输入的 `New-AgentDeskUpdateManifest.ps1` 才能生成可发布更新元数据。

## 发布 Tag

只有公开仓库已经存在、具备可用认证、完成分支保护配置，并且准确提交进入受保护 `main` 后，才能创建已审查 tag：

```powershell
git tag -a v0.1.0 -m "AgentDesk v0.1.0"
git push https://github.com/rkshadow999/AgentDesk.git v0.1.0
```

Tag workflow 必须完成全部 job：

1. Linux x64/ARM64 sidecar 构建、强制沙箱测试、ELF/GLIBC 检查、SBOM 与校验和。
2. 可选 Cloud Server Release 集成测试，以及 Windows 真实进程停机备份/恢复/回滚 E2E 作业。
3. Windows x64/ARM64 Web/.NET/Rust/生命周期测试和签名 MSIX/Portable 输入。
4. 对完整 package input（包括外置 Updater）生成各架构 SPDX/CycloneDX，并汇总 Portable zip、MSIX、签名状态与 SHA-256。
5. 生成确定性的双架构更新清单，执行 P-256 DER detached 签名，并用独立公钥验证。
6. 将不可变的版本化 GitHub Release 暂存为 `draft`；生成回滚包前使用独立 previous-signer pin 对上一版本两份 MSIX 重新执行密码学验证，并使用上一 Release 的签名应用更新 manifest 和独立固定的上一版本 SPKI 验证两个 Portable 归档。
7. 严格推进固定签名 feed，重新解析远端 lightweight/annotated tag 并要求其提交等于 `GITHUB_SHA`，然后才公开版本化 draft。

任一 MSIX 签名 Secret、更新签名私钥 Secret 或仓库固定的当前公钥缺失/不匹配时，tag workflow 都会失败，不会发布未签名的正式包或更新元数据。存在上一签名发布后，任一上一版本信任 pin 缺失或不匹配也会阻断发布。不得绕过该门禁。

## 固定更新 Feed 与发布顺序

`update-prerelease` 是所有受支持 Tag 共用的固定签名 feed。稳定 Tag 还会推进 `update-stable`，因此稳定发布会同时推进两个固定 feed。这些固定 GitHub Release 会刻意标记为 prerelease，客户端不得依赖 GitHub 仅面向稳定版的 `/releases/latest/` 别名。

Tag 构建可以并行，但发布严格按 FIFO 进行。release job 在 checkout 或任何发布副作用之前，会查询同一 workflow 的 GitHub Actions；只要存在 `run_number` 更小、远端 Tag 能 peel 到该 run `headSha` 且尚未完成的 push run，就继续等待。只有同名 `v1.2.3` 分支而没有准确 Tag/commit 时不会误阻塞。较早 Tag run 如果完成但未成功，会阻断后续发布，直到该 run 成功重跑或经过有记录的事件决策明确弃用；记录决定并按需删除废弃 Tag 后，使用 `gh run delete <database-id>` 删除失败 Actions run，再重试后续 workflow。这样可以保留每个待发布 Tag，而不是依赖 GitHub concurrency 仅有且可被替换的单一 pending 槽位。条件允许时仍应一次只推送一个已审查发布 Tag；等待超过四小时会明确失败，并为总计六小时的 release job 留出剩余发布时间，不会静默重排发布。

即使选择 Tag ref，`workflow_dispatch` 也仅用于构建验证。发布签名凭据以及版本化 Release/feed/final 发布 job 只会在事件为 `push` 且 ref type 为 `tag` 时启用。

在任何 `--clobber` 之前，workflow 会用仓库固定的 P-256 公钥验证候选应用与 Updater manifest。随后下载所有目标 feed 的现有元数据，验证两份 detached signature、要求应用/Updater 版本一致，并按 `ci < alpha < beta < preview < rc < stable` 严格前进。旧元数据只能由当前密钥，或 `AGENTDESK_PREVIOUS_UPDATE_PUBLIC_KEY_SHA256` 独立固定的上一把密钥验证；更旧或未固定的密钥会 fail-closed，必须先完成经过审查的密钥/feed 迁移。

版本化 Release 在固定 feed 推进期间保持 `draft`。从 feed 上传到最终公开之间存在一个很短的窗口：已签名 manifest URL 可能指向未登录客户端无法下载的 draft 资产。此时更新检查会暂时 fail-closed，而不会取得未签名或旧字节。最终 job 会重新拉取准确的远端 Tag，把 lightweight 或 annotated tag peel 到提交，要求该提交等于 `GITHUB_SHA`，然后才设置 `draft=false`。

进行 `retry` 恢复时使用 GitHub 的 **re-run failed jobs**。如果 feed job 已成功但最终公开失败，只重跑失败的 finalize job；完整 workflow retry 会因为固定 feed 已是同版本而被刻意拒绝。如果运行在只更新了部分固定 feed 后被取消或失败，应停止自动化，检查签名 feed 字节和 draft 资产，并通过有记录的维护者事件决策完成或修复发布。不得用临时 clobber 绕过同版本/降级门禁。

## 检查发布资产

GitHub Release 必须为 `x64` 和 `arm64` 分别包含：

- `AgentDesk-<版本>-win-<架构>-portable.zip`
- `AgentDesk-<版本>-win-<架构>-updater.zip`（仅包含 `AgentDesk.Updater.exe`）
- `AgentDesk-<版本>-win-<架构>-UPDATE-STATUS.txt`
- `AgentDesk-<版本>-win-<架构>.msix`
- `AgentDesk-<版本>-win-<架构>.spdx.json`
- `AgentDesk-<版本>-win-<架构>.cyclonedx.json`
- `AgentDesk-<版本>-win-<架构>-MSIX-SIGNING-STATUS.txt`
- `AgentDesk-<版本>-win-<架构>-SHA256SUMS.txt`
- 匹配的 Linux sidecar 归档及 `.sha256`。

发布还必须包含以下共享更新元数据：

- `AgentDesk-update-manifest.json`
- `AgentDesk-update-manifest.json.sig`（二进制 RFC 3279 DER ECDSA 签名）
- `AgentDesk-updater-manifest.json`
- `AgentDesk-updater-manifest.json.sig`（二进制 RFC 3279 DER ECDSA 签名）
- `AgentDesk-update-public-key.spki`
- `AgentDesk-update-metadata-SHA256SUMS.txt`

首个发布还包含 `AgentDesk-<版本>-NO-PREVIOUS-ROLLBACK.txt`；后续发布包含 `AgentDesk-<当前版本>-rollback-to-<上一版本>.zip` 和 `.zip.sha256`。回滚生成会验证上一版本架构校验和中的全部条目，包括 Updater 与更新状态文件；手动回滚归档仅包含 Portable、MSIX、SBOM、签名状态和原始校验和。只有稳定 Portable 字节与上一 Release 的 ECDSA 签名应用 manifest 完全匹配，并且该 manifest 能由 `AGENTDESK_PREVIOUS_UPDATE_PUBLIC_KEY_SHA256` 指定的上一版本 SPKI 验证时才会接受。

重新下载已发布 MSIX/Portable，逐项校验 SHA-256，检查包内 `SOURCE-REVISION.txt`，在 Windows 上验证 MSIX Authenticode 与 Publisher，打开两种 SBOM，并确认许可证/源码说明齐全。在真实 Windows 11 x64 和 ARM64 设备上冒烟测试安装与启动。准确发布的两项检查都被记录前，不得声称 ARM64 已验证。

使用预期固定的 P-256 SPKI 公钥，对两份 manifest 的原始字节和各自 detached signature 做独立验证。确认每份清单严格包含一个 `x64` 和一个 `arm64` 资产，顺序固定。应用清单中的 URL、字节大小、小写 SHA-256 和 `AgentDesk.App.exe` 入口点必须与下载后的 Portable 归档一致。Updater 清单也必须与各架构 updater zip 一致，并声明 `AgentDesk.Updater.exe` 为入口点；staging 必须先解压出该文件再启动。随 Release 发布的公钥只用于检查，不得替代客户端内置的信任根。

公告前检查自动生成的发布说明，移除私有 Issue 引用、无证据声明或上游品牌误用。

## 失败与恢复

- 缺少架构、上一版本 MSIX signer pin、上一版本更新公钥 pin、上一 Release 的签名应用 manifest、更新/MSIX 签名无效、更新密钥或 Publisher 不一致、notice 过期、测试失败、缺 Updater/SBOM/校验和、源码 revision 错误，或在已有同版/更高版本后回填旧 Tag 都会阻断发布。
- 固定 feed 签名无效、应用/Updater 版本不一致、同版本、降级或签名密钥未被固定时，发布会被阻断，版本化 Release 继续保持 draft。
- 只有经过记录的维护者事件决策才能删除或标记损坏 GitHub Release/tag；不得静默替换已经公告的 tag 资产。
- 签名/云凭据泄露时立即撤销，并通过 GitHub Private Vulnerability Reporting 协调响应。
- 保留上一签名版本。公告前测试新回滚包，并遵循[安装指南](INSTALLATION.zh-CN.md)中的手动降级限制。

GitHub Release 资产可被维护者修改，因此校验和本身不是透明日志。受保护发布 Environment 和未来 artifact attestation 仍是[威胁模型](AGENTDESK-THREAT-MODEL.zh-CN.md)建议的供应链改进。
