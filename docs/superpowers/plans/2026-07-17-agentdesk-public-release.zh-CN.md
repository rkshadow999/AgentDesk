# AgentDesk 公开仓库实施计划

[English](2026-07-17-agentdesk-public-release.md) | [简体中文](2026-07-17-agentdesk-public-release.zh-CN.md)

> **面向 AI 代理的工作者：** 必需子技能：使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 逐任务实施本计划。使用下方复选框跟踪进度。

**目标：** 将已验证的 AgentDesk Alpha 合规发布为 `rkshadow/AgentDesk` 双语公开仓库。

**架构：** 保留完整 `xai-org/grok-build` 历史与许可证，只在 AgentDesk 自有入口替换上游产品身份；根据源码元数据生成桌面依赖声明，并在 CI 中用 PowerShell 校验器执行发布契约。上游 remote 改为只读，新社区仓库成为 `origin`。

**技术栈：** Git、GitHub CLI、PowerShell 7、Markdown、npm lockfile、.NET 10、Rust 1.92、GitHub Actions。

---

### 任务 1：增加失败关闭的公开仓库校验器

**文件：**
- 创建：`scripts/agentdesk/Test-AgentDeskPublicRepository.ps1`
- 修改：`.github/workflows/agentdesk-windows.yml`
- 测试：`scripts/agentdesk/Test-AgentDeskPublicRepository.ps1`

- [ ] **步骤 1：在必需文档尚不存在时先写校验器**

脚本枚举必需中英文文档对，拒绝根 README 中的上游品牌，验证安全/贡献入口、密钥忽略规则，并要求所有修改过的上游 Rust 文件包含 AgentDesk 修改声明。

```powershell
$requiredPairs = @(
    @("README.md", "README.zh-CN.md"),
    @("CONTRIBUTING.md", "CONTRIBUTING.zh-CN.md"),
    @("SECURITY.md", "SECURITY.zh-CN.md"),
    @("CODE_OF_CONDUCT.md", "CODE_OF_CONDUCT.zh-CN.md"),
    @("desktop/README.md", "desktop/README.zh-CN.md"),
    @("docs/ROADMAP.md", "docs/ROADMAP.zh-CN.md"),
    @("docs/UPSTREAM.md", "docs/UPSTREAM.zh-CN.md")
)
foreach ($pair in $requiredPairs) {
    foreach ($path in $pair) {
        if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $path) -PathType Leaf)) {
            throw "缺少必需公开文档：$path"
        }
    }
}
```

- [ ] **步骤 2：运行校验器并确认红灯**

运行：`pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1`

预期：FAIL，因为 `README.zh-CN.md` 等新文档尚不存在。

- [ ] **步骤 3：将校验器接入 Linux 与 Windows CI job**

```yaml
- name: Validate public repository contract
  shell: pwsh
  run: ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1
```

- [ ] **步骤 4：提交红灯发布契约**

```powershell
git add scripts/agentdesk/Test-AgentDeskPublicRepository.ps1 .github/workflows/agentdesk-windows.yml
git commit -m "test: define AgentDesk public repository contract"
```

### 任务 2：用双语社区入口替换上游产品身份

**文件：**
- 修改：`README.md`
- 创建：`README.zh-CN.md`
- 修改：`CONTRIBUTING.md`
- 创建：`CONTRIBUTING.zh-CN.md`
- 修改：`SECURITY.md`
- 创建：`SECURITY.zh-CN.md`
- 创建：`CODE_OF_CONDUCT.md`
- 创建：`CODE_OF_CONDUCT.zh-CN.md`

- [ ] **步骤 1：编写带独立性声明的英文 README**

```markdown
# AgentDesk

[English](README.md) | [简体中文](README.zh-CN.md)

AgentDesk is an independent, community-maintained Windows 11 desktop client
built on the open-source `xai-org/grok-build` runtime at commit `c68e39f`.
It is not affiliated with or endorsed by xAI, SpaceXAI, OpenAI, or Codex.
```

README 必须包含当前 Alpha 截图、架构、当前功能、精确构建入口、NativeProtected 警告、WslStrict 失败关闭状态、路线图与许可证/来源链接。不得复用上游 Logo 或官方安装命令。

- [ ] **步骤 2：编写完整中文对应版本**

章节顺序与英文一致，正文只链接中文社区文档。

- [ ] **步骤 3：替换贡献与安全策略**

贡献指南接受 Issue 与 PR，要求定向测试、保留上游声明并避免生成物/二进制噪音。安全说明把私密报告指向：

```text
https://github.com/rkshadow/AgentDesk/security/advisories/new
```

- [ ] **步骤 4：增加中英文 Contributor Covenant 2.1**

保留标准英文和忠实中文翻译，执行渠道使用仓库治理和私密安全报告，不引用上游公司联系人。

- [ ] **步骤 5：运行校验器**

运行：`pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1`

预期：仍因桌面文档、许可证声明或修改声明失败，但不再因根社区文档失败。

- [ ] **步骤 6：提交社区入口**

```powershell
git add README*.md CONTRIBUTING*.md SECURITY*.md CODE_OF_CONDUCT*.md
git commit -m "docs: establish bilingual AgentDesk community"
```

### 任务 3：发布双语桌面、路线图与上游说明

**文件：**
- 移动内容：`desktop/README.md` -> `desktop/README.zh-CN.md`
- 创建：`desktop/README.md`
- 创建：`desktop/THIRD-PARTY-SOURCE-NOTICE.zh-CN.md`
- 创建：`docs/ROADMAP.md`
- 创建：`docs/ROADMAP.zh-CN.md`
- 创建：`docs/UPSTREAM.md`
- 创建：`docs/UPSTREAM.zh-CN.md`

- [ ] **步骤 1：让英文成为默认桌面 README**

首屏必须说明：

```markdown
> [!WARNING]
> AgentDesk Alpha currently exposes only NativeProtected, which is not a kernel sandbox.
> WslStrict intentionally fails closed until complete child-network enforcement can be attested.
```

- [ ] **步骤 2：保留并更新中文桌面指南**

将现有中文内容移到 `desktop/README.zh-CN.md`，增加同样的前置警告，并保持中英文命令一致。

- [ ] **步骤 3：增加路线图与上游同步策略**

`docs/ROADMAP*` 分离 Alpha、Beta、高级智能体工作流与可选自托管云。`docs/UPSTREAM*` 记录基线 `c68e39f`、只读 `upstream`、合并流程、冲突审查和修改声明要求。

- [ ] **步骤 4：增加中文 MPL/源码说明**

显著说明冲突时以英文原文和实际许可证为准。

- [ ] **步骤 5：运行链接与发布检查**

```powershell
pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1
git diff --check
```

预期：校验器继续推进到依赖声明或源码修改声明。

- [ ] **步骤 6：提交文档集**

```powershell
git add desktop/*.md docs/ROADMAP*.md docs/UPSTREAM*.md
git commit -m "docs: add bilingual desktop and roadmap guides"
```

### 任务 4：生成并打包桌面第三方声明

**文件：**
- 创建：`scripts/agentdesk/Generate-AgentDeskThirdPartyNotices.ps1`
- 创建：`desktop/THIRD-PARTY-NOTICES.md`
- 修改：`scripts/agentdesk/Test-AgentDeskReleaseScripts.ps1`
- 修改：`scripts/agentdesk/Build-AgentDeskPackage.ps1`
- 修改：`scripts/agentdesk/AgentDesk.Packaging.targets`
- 修改：`.github/workflows/agentdesk-windows.yml`

- [ ] **步骤 1：先增加失败的发布脚本断言**

```powershell
$desktopNotice = Join-Path $repositoryRoot "desktop/THIRD-PARTY-NOTICES.md"
if (-not (Test-Path -LiteralPath $desktopNotice -PathType Leaf)) {
    throw "缺少桌面第三方声明。"
}
foreach ($dependency in @("react", "monaco-editor", "@xterm/xterm", "Microsoft.WindowsAppSDK")) {
    if (-not (Select-String -LiteralPath $desktopNotice -SimpleMatch $dependency -Quiet)) {
        throw "桌面声明缺少 $dependency。"
    }
}
```

- [ ] **步骤 2：运行发布测试并确认红灯**

运行：`pwsh ./scripts/agentdesk/Test-AgentDeskReleaseScripts.ps1`

预期：FAIL，报告缺少桌面第三方声明。

- [ ] **步骤 3：实现确定性声明生成**

生成器读取 `desktop/web/package-lock.json`，解析生产依赖许可证元数据和 `node_modules` 中的许可证文件，加入 Windows App SDK 再分发声明/来源，按包名排序并写入无 BOM UTF-8。生产依赖无法识别许可证时必须失败。

- [ ] **步骤 4：在 Portable 与 MSIX 输入中包含声明**

在 targets 中增加 `AgentDeskDesktopNoticesPath`，把 `desktop/THIRD-PARTY-NOTICES.md` 与根声明一起复制，并包含中英文源码获取说明。

- [ ] **步骤 5：执行红绿验证**

```powershell
pwsh ./scripts/agentdesk/Generate-AgentDeskThirdPartyNotices.ps1
pwsh ./scripts/agentdesk/Test-AgentDeskReleaseScripts.ps1
```

预期：PASS，第二次运行生成器不产生 diff。

- [ ] **步骤 6：提交声明与打包改动**

```powershell
git add desktop/THIRD-PARTY-NOTICES.md scripts/agentdesk .github/workflows/agentdesk-windows.yml
git commit -m "build: package desktop third-party notices"
```

### 任务 5：增加源码修改声明与密钥保护

**文件：**
- 修改：`.gitignore`
- 修改：相对 `c68e39f` 发生变更的所有已跟踪上游 Rust 源文件
- 测试：`scripts/agentdesk/Test-AgentDeskPublicRepository.ps1`

- [ ] **步骤 1：增加密钥文件与修改声明的失败校验**

必需忽略规则：

```gitignore
.env
.env.*
!.env.example
*.pfx
*.p12
*.pem
*.key
*.snk
.superpowers/
```

校验器用 `git diff --name-only c68e39f -- '*.rs'` 获取修改过的 Rust 文件，并要求以下精确首部声明：

```rust
// Modified by the AgentDesk project for Windows desktop integration and safety support.
```

- [ ] **步骤 2：运行校验器并确认红灯**

预期：FAIL 并列出缺少声明的 Rust 文件。

- [ ] **步骤 3：只添加声明，不改变代码行为**

在所有修改过的上游 Rust 文件的模块文档/import 之前加入声明。AgentDesk 新建文件不需要上游修改声明。

- [ ] **步骤 4：运行格式与发布检查**

```powershell
cargo fmt --package xai-proto-build --package xai-grok-pager-bin --package xai-grok-sandbox --package xai-grok-shell -- --check
pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1
git diff --check
```

预期：PASS。

- [ ] **步骤 5：提交合规保护**

```powershell
git add .gitignore crates/build/xai-proto-build crates/codegen/xai-grok-pager-bin crates/codegen/xai-grok-sandbox crates/codegen/xai-grok-shell
git commit -m "chore: record AgentDesk source modifications"
```

### 任务 6：验证并提交完整 Alpha 实现

**文件：**
- 验证所有 AgentDesk Alpha 源码、测试、CI、脚本与文档

- [ ] **步骤 1：串行运行 Web 测试与生产构建**

```powershell
Set-Location desktop/web
npm test
npm run build
Set-Location ../..
```

预期：全部 Vitest 文件通过，Vite 退出码为 0。

- [ ] **步骤 2：Web 构建后运行 .NET 测试与格式**

```powershell
dotnet test desktop/AgentDesk.sln --no-restore
dotnet format desktop/AgentDesk.sln --verify-no-changes --no-restore
```

预期：Core、Engine、Platform、App 测试全部通过，format 退出码为 0。

- [ ] **步骤 3：运行定向 Rust 与发布验证**

```powershell
cargo test --locked -p xai-proto-build
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
pwsh ./scripts/agentdesk/Test-AgentDeskReleaseScripts.ps1
pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1
git diff --check
```

预期：全部命令退出 0。Linux 强制沙箱 E2E 在已配置的 Linux/Docker 环境使用 `SANDBOX_E2E_REQUIRE_ENFORCEMENT=1` 运行。

- [ ] **步骤 4：请求独立复审并修复所有 Critical/Important**

- [ ] **步骤 5：提交剩余 Alpha 实现**

```powershell
git add -A
git commit -m "feat: publish AgentDesk Windows alpha"
```

### 任务 7：创建并配置 GitHub 公开仓库

**文件：**
- 只修改 Git remote 与 GitHub 仓库设置

- [ ] **步骤 1：安装并认证 GitHub CLI**

```powershell
winget install --id GitHub.cli --exact --silent --accept-package-agreements --accept-source-agreements
gh auth login --hostname github.com --git-protocol https --web
gh auth status
```

预期：以 `rkshadow` 登录，并具备 repo/workflow 权限。

- [ ] **步骤 2：创建空公开仓库**

```powershell
gh repo create rkshadow/AgentDesk --public --description "Independent bilingual Windows 11 desktop client for the grok-build agent runtime"
```

- [ ] **步骤 3：安全重接 remote**

```powershell
git remote rename origin upstream
git remote set-url --push upstream DISABLED
git remote add origin https://github.com/rkshadow/AgentDesk.git
git branch -M main
git push -u origin main
```

- [ ] **步骤 4：配置社区仓库功能**

```powershell
gh repo edit rkshadow/AgentDesk --enable-issues --enable-discussions --enable-delete-branch --disable-wiki
gh api --method PUT repos/rkshadow/AgentDesk/private-vulnerability-reporting
gh repo edit rkshadow/AgentDesk --add-topic windows-11,winui3,dotnet,rust,acp,ai-agent,chinese
```

- [ ] **步骤 5：首次推送后保护 main**

通过 GitHub branch protection API 要求 PR 并禁止强制推送。第一次 workflow 产生实际 check name 之前，不配置不存在的必需检查。

- [ ] **步骤 6：验证公开仓库**

```powershell
gh repo view rkshadow/AgentDesk --json nameWithOwner,isPrivate,defaultBranchRef,url
gh run list --repo rkshadow/AgentDesk --limit 5
git remote -v
```

预期：仓库公开，默认分支为 `main`，新仓库是 `origin`，上游只读，首次 CI 可见。
