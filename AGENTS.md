# AgentDesk — 给开源用户与贡献者的说明

> 仓库：[github.com/rkshadow999/AgentDesk](https://github.com/rkshadow999/AgentDesk)  
> 当前公开预览版本：**0.1.0-alpha.13**（Windows 11 x64）  
> 更新时间：2026-07-21

本文面向两类读者：

1. **想直接使用 AgentDesk 的用户**（下载、安装、自动更新）
2. **想从源码构建 / 贡献 / 二次分发的开发者**

更完整的产品说明见根目录 [README.md](README.md) / [README.zh-CN.md](README.zh-CN.md)。  
安全与执行模式细节见 [desktop/README.md](desktop/README.md)。

---

## 1. 终端用户：如何安装（推荐）

AgentDesk 目前提供 **社区自托管的 Windows 预览包**（Inno Setup 安装程序 + Portable 压缩包），**不是** Microsoft Store 应用，也**尚未**提供受信任的代码签名 MSIX。

| 方式 | 链接 |
| --- | --- |
| **下载页（推荐先打开这里）** | https://update.rkshadow.com/install/ |
| **Windows Setup.exe**（无需管理员，默认装到 `%LOCALAPPDATA%\AgentDesk`） | https://update.rkshadow.com/install/AgentDesk-0.1.0-alpha.13-win-x64-Setup.exe |
| **Portable zip**（解压即用） | https://update.rkshadow.com/install/AgentDesk-latest-win-x64-portable.zip |
| **自动更新 Feed**（客户端内置） | https://update.rkshadow.com/feed/ |

### 安装时你会看到什么

- 安装包为 **development / 社区构建**，没有微软或企业 Authenticode 签名。
- Windows **SmartScreen** 可能提示「未知发布者」——这是未签名预览包的正常现象。请在确认下载来源为上述域名后，选择「仍要运行 / 更多信息 → 仍要运行」。
- Setup 使用 `PrivilegesRequired=lowest`，**不需要管理员权限**。
- 当前公开通道以 **x64** 为主；ARM64 依赖 CI / 真机，本仓库不声称本机已验证 ARM64 包。

### 自动更新如何工作

1. 客户端内置 **ECDSA P-256 公钥 pin**，只信任清单上的分离签名（`.sig`）。
2. 默认检查：
   - `https://update.rkshadow.com/feed/AgentDesk-update-manifest.json`
   - `https://update.rkshadow.com/feed/AgentDesk-updater-manifest.json`
   - 及对应 `.sig`
3. 资源下载自 `https://update.rkshadow.com/releases/v<version>/…`
4. **不会静默覆盖你的安装**。设置中开启「检查更新」后，发现新版本会提示你确认再应用。
5. 信任主机策略见 `UpdateOriginPolicy`（含 `update.rkshadow.com`，并仍兼容 GitHub Release 主机名，便于以后切通道）。

**公钥指纹（可核对）：**

```text
SHA-256(c9b3ccf2dd92519a17720056dc43c1f3bb55f4652a1d99e68f99160657611e37)
```

仓库内对应文件：

- `desktop/update/AgentDesk-update-public-key.spki.base64`
- `desktop/update/AgentDesk-update-public-key.sha256`

> **重要：** 更新 **私钥绝不进 Git**。维护者本地路径仅为示例：`%USERPROFILE%\.agentdesk\AgentDesk-update-private.pkcs8.der`。任何人都不该把私钥、API Key、用户对话正文提交到本仓库。

### 从更旧预览包升级

| 你手里的包 | 建议 |
| --- | --- |
| 含自托管公钥的 alpha.6 selfhost / alpha.7–alpha.12 | 设置中检查更新 → 升到 **alpha.13** |
| 更早的 `alpha.6-fixed` 等**不含新公钥**的包 | **无法校验** `update.rkshadow.com` 清单 → 请重新下载上面的 Setup / Portable |

### 版本号约定（开源用户 / 维护者）

- **每次对外发布必须递增版本**（更新清单按版本比较；重复版本无法触发升级）。
- 当前阶段使用 **预发布号** `0.1.0-alpha.N`：`N` 每发一包 **+1**（小步迭代，不涨主版本）。
- 将来若有破坏性变更或不兼容更新通道，再考虑 `0.2.0` / `1.0.0` 等主/次版本。
- 客户端比较的是 **InformationalVersion**（如 `0.1.0-alpha.10`），不是仅看文件版本 `0.1.0.0`。

---

## 2. 当前 alpha.13 已交付的能力（用户可感知）

| 能力 | 说明 |
| --- | --- |
| 中文优先桌面 UI + 英文切换 | Web 文案即时切换；部分原生字符串可能需重启 |
| **应用图标（RK 品牌）** | exe / 开始菜单 / Setup 向导 / 桌面快捷方式使用统一 `.ico` 与磁贴 PNG |
| **会话完成桌面通知** | 任务完成/失败时 Windows 通知；**多会话各弹一条**；点击可回到对应会话 |
| **真正多会话并行** | 同一引擎进程内多会话可同时 running；**切换会话默认不中断** 其它 turn；侧栏可显示多个运行中 |
| **`/` 模式命令** | `/plan` `/execute` `/agent` 立即切换会话模式；`/goal` 与引擎 skills 一并出现在命令面板 |
| **Composer 模型选择** | 聊天框右下角模型 chip（Codex 风格）；可选 curated 列表或自定义 ID，走 `provider/save` |
| **图片附件** | 回形针在引擎能力未上报前可用；**粘贴 / 拖入** PNG/JPEG/GIF/WebP 经宿主 `attachment/stage` 暂存 |
| 工作区选择 / 最近工作区 | 本地持久化最近路径，侧栏可切换、添加、移除 |
| 会话中心 | 新建会话、打开会话、重命名、归档等（以 UI 实际暴露为准） |
| **会话线程本地缓存** | 切换会话时保留投影；后台 stream 继续写入对应会话缓存 |
| 检查器（变更 / 终端 / 计划） | 按会话绑定；**alpha.9+** 修复：空「更改」面板不再盖住「计划/终端」 |
| 原生执行风险确认 | 首次本机执行需确认；**同一工作区**内可复用确认 |
| 完全访问 / 字体缩放 / 检查器宽度 | 见设置与桌面控件；完全访问有独立原生确认 |
| Portable 签名校验更新 | 见上一节 |

**桌面通知说明：**

- 默认开启（新安装 / 无旧偏好时）。设置 →「启用桌面通知」可关闭。
- 若你以前手动关过，升级后仍保持关闭，需在设置里重新打开。
- 切换到其它会话后，原会话完成仍会 toast（多会话兼容）。
- 通知文案只用会话标题，**不含**提示词或文件正文。

### 请诚实理解的边界（开源预览）

1. **单引擎进程内多会话并行（alpha.13+）**  
   同一 sidecar 进程可同时跑多个会话的 turn；切换会话 **默认不中断** 其它会话。  
   仍共享工作区/凭据/API 限流；**不是** 多进程 worker 池。同工作区并行改文件仍可能冲突。

2. **冷启动「完整历史回放」仍有限**  
   侧栏缓存 + 当次会话投影是主要路径；不要默认「关掉软件再开，一定能 100% 还原所有云端/引擎历史」。

3. **`NativeProtected` ≠ 沙箱**  
   UI 写明「本机兼容（非沙箱）」。命令以当前 Windows 用户权限运行。  
   **`WslStrict` 继续 fail-closed**，直到子进程网络限制可被证明。

4. **无受信任 MSIX / 无正式 Authenticode**  
   正式商店或企业分发需要你自己的证书与 CI 签名流水线。

5. **Alpha 软件有风险**  
   可能读文件、改文件、跑命令。只在你信任的工作区使用，并自行保管 API Key。

---

## 3. 从源码构建（开发者）

没有「官方一键安装命令」。在发布受信任签名包之前，推荐：

```powershell
# 工具链要求见 README / docs/BUILD-AND-TEST.md
$env:PROTOC = ./scripts/agentdesk/Install-Protoc.ps1 `
  -Version 29.3 `
  -Destination "$env:TEMP/agentdesk-protoc"

Set-Location desktop/web
npm ci
npm test
npm run build
Set-Location ../..

cargo build --locked -p xai-grok-pager-bin --profile release-dist --features release-dist

./scripts/agentdesk/Build-AgentDeskPackage.ps1 `
  -Architecture x64 `
  -Mode Portable `
  -NativeEnginePath ./target/release-dist/xai-grok-pager.exe `
  -OutputRoot ./artifacts/agentdesk
```

文档：

- [docs/INSTALLATION.md](docs/INSTALLATION.md)
- [docs/BUILD-AND-TEST.md](docs/BUILD-AND-TEST.md) / [docs/BUILD-AND-TEST.zh-CN.md](docs/BUILD-AND-TEST.zh-CN.md)

Web 工作台单测（本轮验证）：**270 passed**。

---

## 4. 维护者：发布社区更新包（可选自托管）

社区默认更新源为 **update.rkshadow.com**（Cloudflare 代理到维护者服务器）。  
若你 fork 并自建更新站，请：

1. 生成自己的 ECDSA P-256 密钥对，**只把公钥**放进你分发的客户端；
2. 修改 `AgentDeskUpdateDefaults` / `UpdateOriginPolicy` 中的 feed 与信任主机；
3. 使用下列脚本签名并上传（**不要把私钥写进仓库或 Issues**）。

| 脚本 | 作用 |
| --- | --- |
| `scripts/agentdesk/Build-AgentDeskWindowsInstaller.ps1` | 生成 Inno Setup `Setup.exe` |
| `scripts/agentdesk/Publish-AgentDeskSelfHostedUpdate.ps1` | 签名清单 + 上传 feed/releases/install |
| `scripts/agentdesk/New-AgentDeskUpdateManifest.ps1` | 生成更新清单（支持自定义 Asset Base URL） |
| `scripts/agentdesk/update.rkshadow.com.nginx.conf` | nginx 站点参考配置 |

示例：

```powershell
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:USERPROFILE\.dotnet;C:\Program Files\PowerShell\7;" + $env:PATH

pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\agentdesk\Publish-AgentDeskSelfHostedUpdate.ps1 `
  -Version "0.1.0-alpha.13" `
  -ReleaseDirectory ".\artifacts\release-alpha.13-selfhost\AgentDesk-0.1.0-alpha.13-win-x64"
```

发布产物目录（本机，已 gitignore）：`artifacts/release-alpha.*`、`artifacts/selfhosted-feed/`。

---

## 5. 本仓库近期功能变更摘要（便于 Code Review）

- **桌面宿主**：最近工作区持久化、会话 open/new 与 supersede 协作、自托管更新默认 feed 与公钥 pin。
- **Workbench（React）**：会话线程缓存、运行中切换确认、侧栏拖拽宽度、首轮发送/流式在 session 绑定前可绘制、`prompt/completed` 正确解锁输入。
- **Inspector**：会话绑定与活动标签显示修复。
- **品牌图标**：`desktop/src/AgentDesk.App/Assets/AgentDesk-icon-source.png` → 运行 `Generate-AppAssets.ps1` 生成 `AgentDesk.ico` 与各尺寸磁贴 PNG；`ApplicationIcon` 与 Inno `SetupIconFile` 已接入。
- **打包**：Inno Setup 安装包 + 签名 Portable 更新通道。

---

## 6. 安全与贡献约定

- **禁止**在仓库、文档、日志、Issue、PR 中写入：API Key、Credential Manager 内容、更新私钥、用户文件正文或对话原文。
- 不要在本机构建说明里附带他人机器上的私有路径作为「唯一安装方式」；对外链接以 **https://update.rkshadow.com/** 与 GitHub 仓库为准。
- `NativeProtected` / Full Access / Windows UI Automation 都是高权限能力，贡献相关代码时请保持确认门与 fail-closed 行为。
- 本项目基于开源 `xai-org/grok-build` 运行时构建社区 Windows 客户端，**与 xAI / OpenAI 等无官方隶属关系**。

---

## 7. 问题反馈

- Issues：https://github.com/rkshadow999/AgentDesk/issues  
- 请注明：Windows 版本、x64/ARM64、安装方式（Setup / Portable / 源码）、版本号（设置或 `SOURCE-REVISION`）、复现步骤。  
- **不要**在 Issue 中粘贴 API Key 或私有代码内容。
