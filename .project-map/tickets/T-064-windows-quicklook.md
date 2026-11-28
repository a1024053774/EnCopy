---
id: T-064
title: Windows 快速查看（Ctrl+Y 内置预览窗）
type: build
status: closed
blocked_by: [T-063]
claimed_by: devin
supersedes:
---

## What to build

Windows 没有系统级的 `QLPreviewPanel`，因此在 EnCopy 内内置一个轻量无边框毛玻璃预览窗，完整对齐 Mac 版 [QuickLook.swift](../../Sources/EnCopy/QuickLook.swift) 的体验与焦点流转。依据 [T-056](T-056-windows-platform-and-release.md)。

包含内容：

- 支持在绳上按下 `Ctrl+Y`、篮子列表按下 `Ctrl+Y` 或卡片右键选「快速查看」打开预览窗。
- 预览支持：图片高清大图自适应展示、长文本/代码/Markdown 完整滚动与可选复制、录屏视频内置播放与进度条、文件清单展示列表及原路径/文件大小状态。
- 支持用 `←` `→` 在当前绳上或当前篮子列表的相邻条目间连续切换预览（按屏幕顺序从左到右，与 [QuickLook.swift](../../Sources/EnCopy/QuickLook.swift) 一致）。
- 焦点借还保护（借鉴 Mac 踩过的坑）：打开预览窗时搜索框不因此失焦收起；按 `Ctrl+Y`、`Esc` 或 `⏎` 关闭预览窗时仅关闭预览窗，并将键盘焦点无缝交还给搜索框和绳子（`⏎` 不会穿透误触发粘贴）。

## Acceptance

- [x] 图片、长文本/代码/Markdown、录屏、单/多文件均可从绳上及篮子列表用 `Ctrl+Y` 或右键正常预览。
- [x] 预览窗打开时按 `←`/`→` 可沿当前绳或篮子列表顺序前后切换项目。
- [x] 按 `Ctrl+Y`、`Esc` 或 `⏎` 关闭预览窗后，绳子与搜索框仍保持打开且键盘焦点直接回到搜索框，连续按 `⏎` 不会在关闭瞬间误贴出。
- [x] `windows/scripts/check.ps1` 覆盖预览项列表生成、顺序索引计算与焦点借还状态机检查并全绿。
- [x] 真机走查：在 `EnCopy Dev` 上对绳上和篮子里的图片、代码与文件分别按 `Ctrl+Y` 预览、左右切换、关闭后继续挑并 `⏎` 贴出，留截图在 `evidence/T-064/`。

## Resolution

实现并验收完成：

- 新增 `QuickLookSession` 与 `QuickLookContentFactory`：统一计算绳子/篮子预览列表、当前索引、左右边界吞键、关闭键吞键和焦点借还状态；图片、文本/代码/Markdown、视频、单/多文件都生成独立预览内容。
- 新增无边框半透明 `QuickLookWindow`：图片大图、完整滚动文本、视频播放/暂停/进度拖动、文件路径/大小/「已移走」状态；绳子和篮子都支持 `Ctrl+Y`，绳子卡片和篮子行右键都支持「快速查看」。
- 复审后修正两个顺序/焦点边界：篮子打开时对绳子卡片右键预览仍按绳子顺序；从非交互悬停状态打开预览时先提升为交互 summon，避免焦点和 dismiss 状态错误。
- 真机走查还发现并修复视频手动模式不加载的问题：WPF `MediaElement` 在 `LoadedBehavior=Manual` 下只设置 `Source` 不会开始播放，现在创建后立即 `Play()`，同时保留用户暂停状态并启用进度拖动。
- `windows/scripts/check.ps1` 最终 24/24 PASS，日志 `evidence/T-064/check-final.log`；独立设计完整性复审 PASS，日志 `evidence/T-064/design-rereview.log`。
- 真机走查 13/13 PASS、0 FAIL、0 SKIP，报告 `evidence/T-064/walk-report.md`；关键证据包括图片/文件/代码/篮子/右键预览截图、视频播放暂停进度截图、长文本滚动复制证据、单文件移走状态，以及 `Ctrl+Y`/`Esc`/`Enter` 三种关闭后的 `QueryBox` 焦点与不穿透证据。
