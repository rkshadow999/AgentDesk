# AgentDesk 公开仓库设计

[English](2026-07-17-agentdesk-public-release-design.md) | [简体中文](2026-07-17-agentdesk-public-release-design.zh-CN.md)

**状态：** 2026-07-17 已批准实施

## 目标

将当前 AgentDesk Alpha 发布为 `rkshadow999/AgentDesk` 公开社区项目，使其他开发者可以 Fork 和二次开发，同时不把项目呈现为 xAI、SpaceXAI、OpenAI 或 Codex 的官方产品。

## 仓库身份

- 仓库名为 `AgentDesk`，默认分支为 `main`。
- 保留完整上游 Git 历史。AgentDesk 基于 `xai-org/grok-build` 提交 `c68e39f` 开始开发。
- 根 README 只使用 AgentDesk 自有文字与截图，不复用上游 Logo、产品截图或官方安装命令。
- 所有显著入口都说明 AgentDesk 是独立社区项目，与 xAI、SpaceXAI、OpenAI、Codex 不存在隶属、授权或背书关系。
- 现有 `origin` 重命名为只读 `upstream`，新的公开仓库成为 `origin`。

## 双语文档边界

所有由 AgentDesk 维护的公开说明文档都提供英文与简体中文版本。英文使用 GitHub 标准文件名，中文统一使用 `.zh-CN.md`。

必须成对提供：

- `README.md` / `README.zh-CN.md`
- `CONTRIBUTING.md` / `CONTRIBUTING.zh-CN.md`
- `SECURITY.md` / `SECURITY.zh-CN.md`
- `CODE_OF_CONDUCT.md` / `CODE_OF_CONDUCT.zh-CN.md`
- `desktop/README.md` / `desktop/README.zh-CN.md`
- `desktop/THIRD-PARTY-SOURCE-NOTICE.md` / `desktop/THIRD-PARTY-SOURCE-NOTICE.zh-CN.md`
- `docs/ROADMAP.md` / `docs/ROADMAP.zh-CN.md`
- `docs/UPSTREAM.md` / `docs/UPSTREAM.zh-CN.md`
- `docs/superpowers/` 下由 AgentDesk 编写的设计与实施文档

继承的上游 changelog、提示词、Skills、测试夹具、许可证正文和 Rust 用户指南保留原始语言。全面复制翻译会形成难以长期同步的失真分支，因此不在本项目双语承诺范围内，根文档会明确说明这一边界。

每对文档在顶部互相链接。许可证和版权正文不做修改；中文许可证说明仅帮助理解，并明确以英文原文和实际许可证为准。

## 社区与安全

- 通过明确的测试和审查流程接受 Issue 与 Pull Request。
- 行为准则采用 Contributor Covenant 2.1，执行渠道使用 AgentDesk 仓库治理，不引用上游公司联系人。
- 安全问题通过 GitHub Private Vulnerability Reporting 提交：`https://github.com/rkshadow999/AgentDesk/security/advisories/new`。
- 未公开漏洞、凭据、提示词或私有源码不得发布到公开 Issue。
- 仓库本地 Git 作者身份使用 `rkshadow` 的 GitHub noreply 邮箱。

## 许可证与来源

- 保留根 Apache-2.0 `LICENSE`、`THIRD-PARTY-NOTICES`、MPL 源码获取说明和全部上游版权声明。
- 每个修改过的上游源文件加入简短、显著的 AgentDesk 修改声明，以满足 Apache-2.0 第 4(b) 条。
- 桌面发布输入包含 AgentDesk 桌面第三方声明，覆盖生产 npm 包和 Windows App SDK 再分发声明。
- 第三方声明从 lockfile/包元数据确定性生成，并由发布脚本测试验证。
- `.gitignore` 阻止 `.env*`、私钥、PFX/P12 证书、签名密钥、本地凭据、构建产物和视觉头脑风暴文件进入版本控制。
- 发布文档明确区分未签名 CI MSIX 与签名 tag 正式发布。

## GitHub 仓库设置

- 可见性：公开。
- 启用 Issues、Discussions、私密漏洞报告和合并后删除分支。
- 禁用 Wiki，文档随源码版本化。
- Topics 包含 `windows-11`、`winui3`、`dotnet`、`rust`、`acp`、`ai-agent`、`chinese`。
- `main` 为默认分支。首次导入后禁止强制推送和删除主分支。
- 首次推送只包含源码与文档；在缺少签名证书和 WSL payload 时，不宣称或发布签名二进制。

## 发布门禁

满足以下条件后才创建并推送公开仓库：

1. 密钥与大文件扫描未发现真实凭据或意外产物。
2. 中英文导航链接全部有效。
3. Rust、Web、.NET、发布脚本、格式和空白检查全部通过。
4. 源码与包输入中包含所需许可证和修改声明。
5. 独立代码审查没有 Critical 或 Important 问题。
