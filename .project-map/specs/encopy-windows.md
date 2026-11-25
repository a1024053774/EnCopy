# Spec: EnCopy Windows 版（2026-11-25）

## Problem

EnCopy 目前只有 macOS 版（`Sources/` 下的 Swift + AppKit/SwiftUI），项目迁到 Windows 后需要在 Windows 11 上提供行为、视觉语言和概念一致的剪贴板工具，同时完全不动 Mac 版的源码、构建脚本与自动更新通路。

## Solution

在仓库独立的 `windows/` 目录下用 C# / .NET 10 + WPF（配合 Win32 与 WinRT 接口）构建 Windows 版 EnCopy，共享根目录 [CONTEXT.md](../../CONTEXT.md) 的领域词汇和 [设计方案](../../design/encopy-design.html) 的产品交互规范：

- **最细端到端起步**：先打通 `Alt+C` 叫出列表 → 剪贴板监听记进 SQLite（FTS5 `trigram` 全文索引）→ `⏎` 贴回原前台窗口并完成真机走查，验证全局热键、非激活窗口焦点切换和 `SendInput` 贴回。
- **按一张票一件事逐步补齐**：
  1. 屏幕顶部透明穿透绳子面板、五种纸卡片与按天按月分组的篮子列表（含改键录制界面与日常版安装脚本 `install.ps1`）；
  2. 弹簧动画（按屏幕刷新率驱动与纸的轻重）、六套主题、顶边路过与全屏避让、`DoDragDrop` 拖出、拿在手上、悬停放大与系统共享；
  3. 复制图片与文件清单（`Files/*.files`）、来源 App 图标与三档规则、10 分钟验证码、30 天/1 GB 保留规则、手动清理与「最近清掉」7 天放回、跟主题走的设置窗口；
  4. `Windows.Media.Ocr` 离线识别图中文字；
  5. 接管截图（默认复制进 `EnCopy\Screenshots\` 且原文件不动）、监视文件夹与登录时打开；
  6. 内置快速查看（`Ctrl+Y`）；
  7. 内置图片标记；
  8. 基于 `Windows.Media.Editing` 的录屏裁剪；
  9. 自签名打包（`EnCopy-windows-x64.zip`）与不干扰 Mac `EnCopy.dmg` 的应用内更新。

## Decisions

Windows 平台选型与策略：

- [T-054 Windows 版的技术栈与目录结构](../tickets/T-054-windows-stack-and-layout.md)
- [T-055 Windows 版的快捷键映射与贴回方式](../tickets/T-055-windows-shortcuts-and-paste.md)
- [T-056 Windows 版的截图、系统能力替代与发布红线](../tickets/T-056-windows-platform-and-release.md)

沿用的产品设计决定：

- [T-002 全局只占 ⌥C 一个键](../tickets/T-002-only-option-c-global.md)（Windows 对应 `Alt+C`）
- [T-003 样式选哪个方向](../tickets/T-003-paper-style.md)
- [T-004 历史留多久、怎么过期](../tickets/T-004-retention-rules.md)
- [T-005 自己能不能清理历史，按什么清](../tickets/T-005-manual-cleanup.md)
- [T-006 动画做到什么程度](../tickets/T-006-motion.md)
- [T-007 双击一张纸做什么](../tickets/T-007-double-click.md)
- [T-008 ⌥C 时绳子出现在哪里](../tickets/T-008-summon-position.md)
- [T-009 复制的内容默认记不记](../tickets/T-009-recording-default.md)
- [T-010 保留哪些界面语言](../tickets/T-010-languages.md)
- [T-017 开发版要不要用单独的数据位置](../tickets/T-017-dev-copy-own-data.md)
- [T-019 绳上的一件贴出去后要不要挪到绳头](../tickets/T-019-paste-moves-to-head.md)
- [T-043 篮子里的东西怎么不靠搜索一件件看](../tickets/T-043-basket-list.md)
- [T-048 访达的文件进篮子、多文件一叠算一件；保留规则搬进设置，设置跟主题走](../tickets/T-048-files-retention-settings.md)

## Test seam

1. **自动检查（`windows/scripts/check.ps1`）**：每次修改后必跑。依次校验静态门禁（无上游名字、全局仅注册一处热键、数据目录集中由一处决定、中英文词条对齐、全项目编译）和隔离集成测试（使用临时目录和模拟剪贴板，不碰真实 `%LOCALAPPDATA%\EnCopy` 与系统剪贴板，不在用户屏幕闪烁弹窗）。
2. **真机走一遍**：每张施工票收尾时退出日常版，用 `EnCopy Dev` 在记事本、微信（文件传输助手，只贴不发，贴完 `Ctrl+Z` 撤掉）和 Windows Terminal 里按工单验收步骤手动走查，截屏证据存进 `evidence/<工单号>/`（不进 git），验收完运行 `windows/scripts/clean-dev.ps1` 清掉开发版数据。

## Out of scope

- 修改 Mac 版的 `Sources/`、`Package.swift`、`scripts/` 或产品页 `docs/index.html`。
- 截图接管时「移走原文件」（当前只复制进 `EnCopy\Screenshots\`，原文件不动）。
- 单独发布占用 `releases/latest` 的 Windows 版 Release。
- [Mac 版 spec](encopy-v1.md) 已明确排除的项（快捷回复、iCloud 同步、Excel 导入导出、从 iCopy 导入历史、简中与英文以外的界面语言）。
