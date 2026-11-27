---
id: T-061
title: Windows 复制的图片与文件、按 App 规则、保留清理与设置窗口
type: build
status: open
blocked_by: [T-060]
claimed_by: antigravity
supersedes:
---

## What to build

补齐除 OCR 和截图接管之外的全部剪贴板数据类型、隐私治理与持久化清理体系，以及跟主题走的设置窗口，对齐 [T-004](T-004-retention-rules.md)、[T-005](T-005-manual-cleanup.md) 与 [T-048](T-048-files-retention-settings.md)。

包含内容：

- **复制图片与资源管理器文件**：支持在设置开关控制记录复制的图片（存为 PNG 进 EnCopy 文件夹并按像素/字节哈希去重，复制相同图回绳头不重复存）；支持记录在资源管理器里复制的单/多个文件或文件夹，在 `Files/*.files` 写入清单而不复制原文件（一次复制多个为「N 个文件」一叠卡片，粘贴全部放回；篮子列表里按 `Ctrl+↓` 展开一叠单独挑贴其中某一个；原文件全不在时标「已移走」）。
- **来源 App 图标、按 App 三档与验证码**：读取剪贴板/前台进程路径提取应用名和图标显示在卡片与篮子行右下角（可开关）；设置页支持「按 App 记录」三档（照常记、一小时后清、不记），预设密码管理器（如 `1Password`、`Bitwarden`、`KeePass*`）不记，并支持过滤 Windows 标准隐私剪贴板格式；识别 4–8 位验证码留 10 分钟后直接删除，不进「最近清掉」。
- **保留规则与手动清理**：每天与启动时执行保留规则（不碰绳上和收藏）：文字/链接/代码/文件清单留 1 年；图片原图 30 天后或总占用超过上限（默认 1 GB，从最旧原图起）缩成缩略图，缩略图留 1 年；到期非永久删除项先入「最近清掉」保留 7 天，可按批次整批放回。提供菜单栏与 `Ctrl+Shift+Backspace` 唤起的「清理历史」面板（支持按类型、时间、来源 App、关键词与是否含收藏筛查，清前给出预览清单与腾出空间估算）。
- **跟主题走的设置窗口与首次启动引导**：用当前主题纸张样式绘制五页设置窗口（通用、挂上复制的内容、截图、晾衣绳、保留），包含数据存储方式说明与打开数据文件夹按钮；首次启动说明记录范围与文件夹位置，支持一键暂停记录。

## Acceptance

- [x] 复制的图片、资源管理器单/多文件都能上绳、进篮子、写回剪贴板与贴出；一叠文件在篮子里可用 `Ctrl+↓` 展开并单独贴出其中一件，原文件被删/移走后卡片灰化标「已移走」。
- [x] 来源 App 图标正确显示；设为「不记」或隐私标记的复制完全不进历史与索引；设为「一小时后清」及验证码到点直接删除，不进「最近清掉」。
- [x] 保留规则的预览数量/腾出空间与实际执行一致；超过 30 天或超过上限的原图能缩成缩略图且保留原图识别词；手动清理与自动过期进入「最近清掉」，7 天内可整批放回原位置。
- [x] 设置窗口随六套主题即时变换样式，各开关与选项重启后保留生效。
- [ ] `windows/scripts/check.ps1` 覆盖图片去重、文件清单、三档规则、验证码、缩图容量上限、清理预览与放回全部逻辑并全绿通过；真机在 `EnCopy Dev` 走查留证据在 `evidence/T-061/`。

## Resolution

- **阶段实现与复验修复提交**：`67f29f9`、`8c80492`、合并 `main`（`e0dbda4`）、`ce522bb`、`3af5f23`、`2aa2b68`（分支 `feat/T-061-windows-files-retention-settings`，待两项复验全绿后合并关单）。
- **测试、初验与修复记录**：
  - 按 `evidence-first-testing` 先在 `FilesImagesAppRulesAndRetentionContract` 建立 `.files` 多文件清单契约红灯测试并记录初始红灯，再补齐复制图片 SHA-256 去重、`.files` 清单与篮子 `Ctrl+↓` 栈展开单挑粘贴及「已移走」灰化、来源应用 Shell 图标与三档规则（预设 `1Password`/`Bitwarden`/`KeePass*` 等不记）、4–8 位验证码 10 分钟直接删除（收藏则清除 `ttl` 永不过期）、自动保留（30 天或超 `OriginalsCapBytes` 缩成 `640px` 缩略图并保留已索引识别词、文字与文件清单留 1 年）、「最近清掉」7 天整批放回、`Ctrl+Shift+Backspace` 手动清理面板及五页跟主题走的设置窗口与首次引导框。
  - 首轮真机走查报告 `evidence/T-061/walk-report.md` 共 14 项：**12 PASS、2 FAIL、0 SKIPPED**（截图 `01-first-launch-onboarding.png` 至 `14-cleanup-put-back-restored.png` 已完整保留）。两项失败均已先补红灯断言再修复转绿：
    1. **打开设置前的前台应用未排「按 App 记录」首行**：右键托盘或顶栏菜单时焦点已切到系统任务栏/外壳或 Dock 进程（`Shell_TrayWnd`、`TopLevelWindowForXamlIsland`、`Dock_64.exe` 等）或 `EnCopy` 自身，现由 `ForegroundAppTracker`（`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` + 顶边轮询双通道）忽略外壳窗口与自身进程、持续保留最近一次外部正常应用，打开设置时稳定置于首行并标「打开设置前正在使用的应用」。
    2. **Windows 11 商店版记事本显示 `Notepad.exe` 而非中文「记事本」**：商店版 `Notepad.exe` 的 PE `FileDescription` 为 `"Notepad.exe"`，此前早于预设本地化名称返回；现改为优先返回 `presetTitle`（中文「记事本」/英文 `"Notepad"`），并对非预设可执行文件的 `FileDescription` 先剥离 `.exe` 后缀再回退 `ProductName`（经 Codex `gpt-6.1-terra` 定向复审确认修复）。
  - 五页设置窗、霓虹主题切换与清理历史面板离屏渲染图已存于 `evidence/T-061/00-offscreen-*.png`；`windows/scripts/check.ps1` 21/21 全绿通过。
- **验收辅助工具与重建方式**：
  - 工具源码位于 `evidence/tools/walk-src/`（已由 `.gitignore` 忽略），本票新增 `move`、`drag`、`clip-set-image`、`clip-set-files`、`clip-info` 及 `F11` 键支持；重建命令：`dotnet publish evidence/tools/walk-src/walk.csproj -c Release -r win-x64 --self-contained false -o evidence/tools`。定向复验提示词见 `evidence/T-061/codex-walk.md`（输出至 `evidence/T-061/walk-recheck.md`）。
