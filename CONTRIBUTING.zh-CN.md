# 参与 AgentDesk 贡献

[English](CONTRIBUTING.md) | [简体中文](CONTRIBUTING.zh-CN.md)

AgentDesk 是一个独立的社区项目。欢迎提交 Issue、文档改进、范围集中的修复和 Pull Request。所有参与者都应遵守[行为准则](CODE_OF_CONDUCT.zh-CN.md)。

## 选择正确的渠道

- 使用 [GitHub Issues](https://github.com/rkshadow999/AgentDesk/issues) 报告可复现的 Bug、范围明确的功能建议、文档缺口和无障碍问题。
- 使用 [GitHub Pull Requests](https://github.com/rkshadow999/AgentDesk/pulls) 提交针对明确问题、便于审查的更改。
- 漏洞、凭据泄露、沙箱或权限边界绕过以及其他敏感问题应遵循[安全策略](SECURITY.zh-CN.md)。不得在公开 Issue 中披露此类信息。

新建内容前请先搜索现有 Issue 和 Pull Request。如果行为变更同时涉及桌面宿主、Web UI、Rust sidecar、协议或打包流程，请先创建 Issue，以便在实现前明确契约和测试范围。

## 开发环境

受支持的桌面构建环境使用 Windows 11、PowerShell 7、Node.js 24、.NET SDK 10.0.302 和 Rust 1.92。构建或测试 WinUI 应用前需要先构建 Web 资源：

```powershell
Set-Location desktop/web
npm ci
npm test
npm run build
Set-Location ../..

dotnet test desktop/AgentDesk.sln --no-restore
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
```

准确的组件、云端、打包和不同架构命令见[构建与测试](docs/BUILD-AND-TEST.zh-CN.md)。

## 保持更改聚焦

- 每个 Pull Request 只解决一个连贯问题。上游同步、依赖升级、格式化和功能开发应分别提交。
- 遵循现有边界：WinUI 宿主负责 Windows 集成；共享 .NET 契约不依赖 UI；Rust 运行时通过 ACP/NDJSON stdio 通信。
- 保留全部上游版权、许可证、源码可获取性和修改说明。编辑继承文件时遵循[上游同步策略](docs/UPSTREAM.zh-CN.md)。
- 未经过明确的安全设计和回归测试，不得削弱权限提示、本机执行警告、沙箱证明或 fail-closed 行为。
- 不得提交凭据、私有源码、用户提示词、本地路径、生成的构建目录、打包产物、本地测试截图或无关的二进制变更。
- 只有在锁文件或生成文件的变化是本次更改直接且可复现的结果时才提交。若有意更新二进制资源，应说明原因，并在可行时保留可编辑源文件。
- 避免对全仓进行格式化，只格式化本次范围内的包或文件。

## 测试更改

先添加能够证明新行为的最小回归测试，再运行聚焦测试及相关的更大测试集。常用检查如下：

```powershell
# Web 界面
Set-Location desktop/web
npm test
npm run build
Set-Location ../..

# .NET 宿主与类库
dotnet test desktop/AgentDesk.sln --no-restore
dotnet format desktop/AgentDesk.sln --verify-no-changes --no-restore

# AgentDesk Rust 契约与格式
cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1
cargo fmt --package xai-grok-shell -- --check

# 公开仓库与发布契约
pwsh ./scripts/agentdesk/Test-AgentDeskPublicRepository.ps1
pwsh ./scripts/agentdesk/Test-AgentDeskRollbackBundle.ps1
pwsh ./scripts/agentdesk/Test-AgentDeskReleaseScripts.ps1
git diff --check
```

针对你修改的每个组件，还应运行对应包的测试。如果某项检查依赖当前不可用的架构、签名证书、Linux 强制执行环境或外部服务，请准确说明哪些检查没有运行，并在合并前等待对应 CI 任务通过。

## Pull Request 要求

Pull Request 应包含：

- 面向用户或技术层面的问题，以及本次选择的范围。
- 简明的实现摘要，以及安全或兼容性取舍。
- 新增的测试和实际运行的完整验证命令。
- 仅在可见 UI 发生变化时提供截图，并移除密钥与私有路径。
- 当命令、协议、安全保证或用户工作流改变时同步更新文档。
- AgentDesk 自有公开文档同步更新英文与简体中文版本。
- 对依赖、生成文件、二进制、许可证或上游源码变化作出明确说明。

合并前，维护者可能要求缩小改动、补充测试、修复无障碍问题、同步更新双语文档，或将上游同步与功能改动彻底分离。

## 许可

提交贡献即表示你有权提交相关内容，并同意它按照受影响文件适用的许可证进行分发。AgentDesk 第一方源码和继承的第一方源码通常采用 Apache-2.0；第三方与 vendored 文件保留其原始许可证。不得替换或移除适用的许可证声明。
