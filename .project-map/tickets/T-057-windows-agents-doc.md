---
id: T-057
title: 补齐 AGENTS.md 的 Windows 检查、构建、真机验收与发布约定
type: task
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## Question

`AGENTS.md` 目前全部是 macOS 下的脚本、工具和发布规范（`scripts/*.sh`、`evidence/tools/`、`vdisplay`、`screencapture`/`cliclick`、DMG 发布、`/Applications/EnCopy.app` 等），在 Windows 上不成立。如何按 Windows 版的新边界补充这份协作约定（保留 Mac 原有说明不删）？

计划补充内容：

1. **项目地图命令**：补充 Windows 下的 `python` 检查命令路径。
2. **Windows 命令表（位于 `windows/scripts/`）**：
   - `windows/scripts/check.ps1`：本地静态门禁与隔离自动化检查（每次改完必跑，离屏运行不闪烁、不碰真实数据与系统剪贴板）；
   - `windows/scripts/build-app.ps1`：构建开发版 `EnCopy Dev`（默认）或日常版 `EnCopy`；
   - `windows/scripts/install.ps1`：装日常版到 `%LOCALAPPDATA%\Programs\EnCopy\`；
   - `windows/scripts/clean-dev.ps1`：退出 `EnCopy Dev` 并清掉 `%LOCALAPPDATA%\EnCopy Dev\`；
   - `windows/scripts/make-signing-identity.ps1`：本机建固定自签名证书；
   - `windows/scripts/make-package.ps1`：打发布包 `EnCopy-windows-x64.zip`。
3. **双版本目录与 Git 流程**：写明 Windows 日常版与开发版各自的安装位置、数据文件夹、单实例互斥与 `Alt+C` 独占；写明 Windows 分支用本机默认 git 身份提交（不加 `--author`），每张票按收尾顺序自己走完并整批汇报，开 PR 带 `--repo a1024053774/EnCopy --base main`。
4. **发布与 `EnCopy.dmg` 保护红线**：明确不得单独创建占用 `releases/latest` 的 Windows Release，不得改 `docs/index.html`；Windows 包（`EnCopy-windows-x64.zip`）只能与 Mac 版同发或发 `prerelease`，怎么发、什么时候发先问用户；发布说明写明无商业证书时遇 SmartScreen 点「更多信息 → 仍要运行」。
5. **Windows 真机验收规程**：验收辅助小程序（屏幕截图、`UIAutomation` 控件树读取与 `SendInput` 防卡键工具，替代 Mac 的 `screencapture` / `cliclick` / `vdisplay`）跟 Mac 一样放在已被 git 忽略的 `evidence/tools/` 下，绝不放进 `windows/` 或提交进公开仓库，每张工单的 `Resolution` 里写清用了哪些工具、怎么重建；验收前先问用户有无其他自动化并确认前台与剪贴板空闲；在记事本、微信（文件传输助手，只贴不发，贴完 `Ctrl+Z` 撤回）和 Windows Terminal 走查；截图存 `evidence/<工单号>/` 不进 git。
6. **清缓存**：补充清理 `windows/` 编译缓存、`windows/build/` 与 `clean-dev.ps1`。

请用户确认以上内容后修改 `AGENTS.md`，并更新 `.project-map/MAP.md` 中 `AGENTS.md` 的覆盖路径。

## Resolution

已完成（2026-11-25，依据用户确认）：

- 在保留 Mac 原文一字不删的前提下，向 [AGENTS.md](../../AGENTS.md) 补充了 Windows 版的完整协作约定：
  - 开工项目地图状态检查的 Windows `python` 命令路径（使用 `$env:USERPROFILE\.gemini\...`，不含本机用户名）；
  - `windows/scripts/` 下 `check.ps1`、`build-app.ps1`、`install.ps1`、`clean-dev.ps1`、`make-signing-identity.ps1`、`make-package.ps1` 的用途，以及离屏隔离运行、`windows/` 目录边界和 `.NET 10 SDK`（`Microsoft.DotNet.SDK.10`）要求；
  - 一张工单一个分支开 PR 合入 `main`（`--repo a1024053774/EnCopy --base main`、`--merge --delete-branch`）、使用本机默认 git 身份（不加 `--author`）、每张票按收尾顺序自己走完（整批做完再汇报），以及日常版（`%LOCALAPPDATA%\Programs\EnCopy\`、`%LOCALAPPDATA%\EnCopy\`）与开发版（`windows/build/EnCopy Dev/`、`%LOCALAPPDATA%\EnCopy Dev\`）隔离和 `Alt+C` 互斥规程；
  - Windows 发布包 `EnCopy-windows-x64.zip` 的本机固定自签名、SmartScreen「更多信息 → 仍要运行」说明，以及严禁单发占住 `releases/latest` 的非预发布 Release 或改动 `docs/index.html` 的红线；
  - Windows 真机验收规程：辅助工具放在已被 git 忽略的 `evidence/tools/`（不进 `windows/`、不入库，并在每张工单 `Resolution` 记录用到的工具及重建方式）、`SendInput` 抬起清零修饰键、120ms 点击按住、验收前向用户确认无其他自动化并检查前台/剪贴板空闲，以及记事本、微信文件传输助手（只贴不发并 `Ctrl+Z` 撤回）和 Windows Terminal 走查要求；
  - `windows/build/`、`bin/`/`obj/` 与 `clean-dev.ps1` 清缓存命令。
- 同步更新 [.project-map/MAP.md](../MAP.md) 中 `AGENTS.md` 的覆盖路径为 `` `scripts/**`, `windows/scripts/**` `` 并登记本工单结论。
