# AgentDesk 开发交接记录

更新时间：2026-07-20

## 当前已完成

- 修复 WinUI Portable 客户端启动崩溃：`NativeStringResources` 现在把 `.resw` 资源键中的点号转换为 WinAppSDK `ResourceMap` 使用的 `/` 层级路径。
- 新增 `NativeStringResourcesTests`，覆盖带层级资源键和普通资源键。
- 已用真实 Windows x64 环境复现并定位原始异常：`0x80073B17 NamedResource`，堆栈位于 `ConfigureLocalizedShell`；修复后的临时发布包可保持进程运行并创建窗口句柄。
- alpha.6 之前的完整功能变更、中文界面、完全访问确认、字体缩放、检查器宽度持久化、Git/worktree 和 Enter 发送修复均保留。

## 尚未完成 / 交接事项

1. **必须重新生成正式 alpha.6 包**

   `artifacts/agentdesk-alpha.6-fa0e8b0/package-input-x64` 是修复前生成的包，不能直接交付。提交本修复后重新运行：

   ```powershell
   $params = @{
     Architecture     = "x64"
     Mode             = "All"
     Version          = "0.1.0-alpha.6"
     NativeEnginePath = ".\target\release-dist\xai-grok-pager.exe"
     OutputRoot       = ".\artifacts\agentdesk-alpha.6-fixed"
     SourceRepository = "https://github.com/rkshadow999/AgentDesk"
     SourceRevision   = (git rev-parse HEAD)
   }
   .\scripts\agentdesk\Build-AgentDeskPackage.ps1 @params
   .\scripts\agentdesk\Finalize-AgentDeskPackage.ps1 `
     -Architecture x64 `
     -Version 0.1.0-alpha.6 `
     -PackageInputRoot .\artifacts\agentdesk-alpha.6-fixed\package-input-x64 `
     -OutputRoot .\artifacts\release-alpha.6-fixed
   ```

   重新打包后必须启动新的 `portable\AgentDesk.App.exe`，确认不再出现 `NamedResource`，并核对 `SOURCE-REVISION.txt`、SHA256、SBOM 和 unsigned MSIX 状态。

2. **桌面自动化验收未完成**

   已完成启动烟雾测试，但以下真实 UI 流程尚未逐项记录：

   - `C:\Users\rksha\Documents\sub2` 的 Git/worktree 识别；
   - Enter 发送、Shift+Enter 换行及中文 IME；
   - 完全访问开关的原生确认、拒绝和自动 `AllowOnce`；
   - 检查器侧栏拖拽、键盘调整、复位和重启持久化；
   - 90/100/110/125/140% 字体缩放；
   - 真实 Provider 流式提示和取消在新包中的端到端显示。

   上一轮 Computer Use 被物理 Escape 中断，因此不要把这些项目标记为已验收。

3. **CI / 架构边界**

   - GitHub Actions run `29736714751` 对提交 `fa0e8b0` 的最终结论需要继续查询。
   - 本机为 AMD64，ARM64 sidecar 和 ARM64 原生客户端不能在本机声称已验证；依赖 `windows-11-arm` runner。
   - 当前没有受信任的 PFX 或更新私钥；MSIX/Updater 只能作为 unsigned/development 产物。

4. **发布前检查**

   - 运行完整 `.NET`、Web、Rust、脚本测试和 `dotnet format --verify-no-changes`。
   - 确认诊断日志不包含 API key、提示词或文件正文。
   - 推送后再检查 GitHub Actions 是否针对包含本修复的新提交重新运行。

## 安全边界

- 不要在仓库、文档、日志或提交信息中写入 API key、Credential Manager 内容或用户文件正文。
- 不要启动 Docker，也不要删除不属于 AgentDesk 的 Docker 数据。
- `NativeProtected` 仍是兼容模式，不等同于沙箱；`WslStrict` 继续 fail-closed，直到子进程网络限制可证明。
