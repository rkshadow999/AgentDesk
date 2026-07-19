# 上游来源与同步策略

[English](UPSTREAM.md) | [简体中文](UPSTREAM.zh-CN.md)

## 项目身份

AgentDesk 是一个由社区独立维护的项目，与 xAI、SpaceXAI、OpenAI 或 Codex 均无隶属或合作关系，也未获得这些组织或产品的认可。仓库保留完整的 `xai-org/grok-build` 历史，用于许可证合规、审查与后续同步；保留历史并不代表 AgentDesk 是上游官方发行版。

## 记录的基线

- 上游仓库：`https://github.com/xai-org/grok-build.git`
- AgentDesk 初始基线：`c68e39f`（`Publish harness and TUI open-source`）
- 当前已同步的上游提交：`c68e39f`

初始基线是永久保留的来源标记。上游同步被接受后，应在本文件的中英文版本中更新“当前已同步的上游提交”，并更新所有用于对比 AgentDesk 修改与已同步上游源码树的验证逻辑。

## 远端策略

社区规范仓库命名为 `origin`。原始仓库命名为 `upstream`，只用于 fetch，并有意将 push URL 禁用：

```powershell
git remote -v
git remote get-url upstream
git remote get-url --push upstream
```

预期的上游 fetch URL 为 `https://github.com/xai-org/grok-build.git`，预期的 push URL 为 `DISABLED`。维护者配置新工作区时使用：

```powershell
git remote add upstream https://github.com/xai-org/grok-build.git
git remote set-url --push upstream DISABLED
```

绝不能向 `upstream` 推送 AgentDesk 分支、Tag、发布元数据或凭据。如果某个通用修复适合贡献给上游，应通过合适的 Fork 单独准备，并遵循上游贡献流程，不得暗示上游认可 AgentDesk。

## 同步流程

上游同步必须使用独立 Pull Request，不得与 AgentDesk 功能、重构、依赖升级或全仓格式化合并进行。

1. 从记录的“当前已同步的上游提交”开始，获取并检查候选范围：

```powershell
git fetch --prune upstream
git switch main
git pull --ff-only origin main
git switch -c chore/sync-upstream-YYYY-MM-DD
git log --oneline c68e39f..upstream/main
git diff --stat c68e39f..upstream/main
```

2. 合并前阅读上游发布说明，并检查许可证、依赖、生成的 workspace、认证、工具执行、沙箱、ACP 和打包变化。

3. 保留上游历史进行合并：

```powershell
git merge --no-ff upstream/main
```

4. 理解冲突两侧内容后逐项解决。不得统一接受 `ours` 或 `theirs`。应重点审查 AgentDesk ACP 扩展、认证与凭据清理、权限映射、沙箱与子进程网络限制、进程生命周期、生成的 Cargo 元数据、公开品牌以及法律声明。

5. 必要时以小提交重新应用或调整 AgentDesk 更改。除非经过单独审查的安全设计证明替代方案成立，否则保留用户可见的风险文案与 fail-closed 行为。

6. 将上方“当前已同步的上游提交”更新为已合并的上游提交，并在同一更改中更新英文版本。

7. 运行受影响的 Rust 测试集，以及 [`desktop/README.zh-CN.md`](../desktop/README.zh-CN.md#测试)中记录的全部 AgentDesk Web、.NET、发布、公开仓库、格式和空白检查。强制 Linux 沙箱测试必须在能够要求强制措施生效的环境中运行。

8. 在 Pull Request 中记录新旧上游提交、审查过的上游范围、冲突及解决方案、AgentDesk 适配、声明变化、验证证据与任何延后的不兼容问题。涉及信任边界的变化必须经过独立审查。

后续同步时，应将范围检查命令中的 `c68e39f` 替换为记录的“当前已同步的上游提交”。“初始基线”字段始终保留 `c68e39f`。

## 冲突审查规则

- 保持 AgentDesk 自有的根目录和桌面入口独立且双语；不得重新引入上游 Logo、产品截图、官方安装命令或公司支持联系人。
- 保留 ACP/NDJSON 进程边界。上游内部 API 不能替代经过协商的桌面协议。
- 将认证、环境变量继承、权限、进程创建、WSL、沙箱和网络访问变化视为安全敏感变更。
- 始终明显标注 `NativeProtected` 为非沙箱。除非完整满足证明契约，否则 `WslStrict` 保持 fail-closed。
- 审查 `Cargo.toml`、锁文件、生成文件、vendored 代码与包元数据变化对可复现性和许可证的影响。
- 保留无障碍行为、双语字符串、架构一致性和发布签名门禁。

## 许可证与修改声明

不得删除或改写上游版权声明、`LICENSE`、`THIRD-PARTY-NOTICES`、vendored 声明或源码可获取性说明。第三方文件继续适用其原始许可证。

相对于记录的已同步上游源码树、由 AgentDesk 修改的继承 Rust 源文件，必须在模块文档和 import 之前保留以下完全一致的首部声明：

```rust
// Modified by the AgentDesk project for Windows desktop integration and safety support.
```

AgentDesk 全新文件不需要上游修改声明，但仍需遵循正确的仓库许可证和第三方归属要求。记录的同步提交改变后，应更新修改审计与公开仓库 validator，避免把仅来自上游的变化错误标记为 AgentDesk 修改，同时确保本地修改仍有完整声明。
