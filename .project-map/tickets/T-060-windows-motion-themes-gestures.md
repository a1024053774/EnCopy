---
id: T-060
title: Windows 绳子的弹簧动画、六套主题、顶边路过与拖出
type: build
status: closed
blocked_by: [T-059]
claimed_by:
supersedes:
---

## What to build

补齐 EnCopy 作为桌面物件的手感、多主题外观和鼠标/拖拽交互，对齐 [T-003](T-003-paper-style.md)、[T-006](T-006-motion.md) 与 [设计方案](../../design/encopy-design.html)。

包含内容：

- **弹簧与帧动画**：按屏幕刷新率驱动弹簧循环（静止后自动停转不空转），实现纸的轻重摆动（小票 38/2.4 最轻到文件夹 58/3.4 最重）、`Alt+C` 快速落下与收起、新卡挂上木夹张开夹紧、微风（静默 4s 后每 7–16s 吹过）、单击轻跳、取下下坠淡出、`⏎` 选卡缩小飞向鼠标指针（同时粘贴立即触发不等动画）；监听系统「动画效果」关闭状态，关闭时自动退化为淡入淡出。
- **六套主题与菜单栏托盘动画**：纸、像素、拟物、极简、霓虹、手账，更换纸、夹子、绳子与选中框；托盘小图标画出麻绳与双夹子，收着绳子复制时夹子轻晃。
- **顶边路过与全屏避让**：鼠标停在屏幕顶边（`y <= 2px`）正中 1/3 达 0.25 秒平滑滑下（仅用鼠标、不抢键盘），离开后延时收起；有新项挂上时短暂探出（支持 0.8–4 秒或关闭）；通过 `SHQueryUserNotificationState` 与窗口边界检测避让全屏应用（可开关覆盖）。
- **拖出、悬停放大、拿在手上与共享**：通过 `DoDragDrop` 拖到外部应用复制、拖进文件夹保存/移动、拖到回收站撤销取下（`IFileOperation` + `FOF_ALLOWUNDO` 记回收站路径供 `Ctrl+Z` 放回）；支持悬停放大预览、右键「拿在手上」（置顶半透明无边框浮窗，支持拖移、滚轮缩放、笔记本文本重排、按 `Esc` 飞回绳上）及「共享…」调用 Windows 原生系统共享面板（`IDataTransferManagerInterop`）。

## Acceptance

- [x] 动画遵循「按键在按下那一帧生效、不排队、按轻重摆、静止即停转」，在开启与关闭系统「动画效果」两种设置下表现符合规范。
- [x] 六套主题切换即时生效，绳子与篮子列表条纹外观同步跟主题走；托盘图标随监听开关、挂物数量与新复制轻晃变化。
- [x] 顶边 `y <= 2px` 正中 1/3 路过不干扰浏览器顶部标签页点击；全屏应用（如全屏播放或演示）时默认自动避让，`Alt+C` 仍可强行呼出。
- [x] 拖进微信/记事本等应用能正常粘贴/复制，拖进桌面或文件夹移动保存，右键或「拿在手上」悬浮窗可缩放和 `Esc` 飞回，右键「共享…」能拉起 Windows 共享面板。
- [x] `windows/scripts/check.ps1` 通过；真机在 `EnCopy Dev` 下走查六套主题、顶边路过、全屏避让、拖出保存、拿在手上与减弱动画，留证据在 `evidence/T-060/`。

## Resolution

- **代码提交**：`3c12836`、`b656515`、`8385bff`、`4c6804e`（分支 `feat/T-060-windows-motion-themes-gestures`）。
- **测试与审查记录**：
  - 按 `evidence-first-testing` 先写弹簧动力学与静止自动退订、六套主题与托盘轻晃、顶边路过与全屏避让、拿在手上/悬停放大/拖拽回收站撤销契约检查并复现红灯，再补齐实现。
  - 首轮审查与跨模型针对性复审（Codex `gpt-6.1-terra` 只读沙盒）共识别并修复：动画帧原位更新变换矩阵保持卡片指针状态、新卡探出期与双屏水平越界收起判定、收起动画 `_isTucking` 竞态、`WindowsShareService` 锚点窗口激活顺序与纯文本载荷、以及 `IFileOperation`（`FOF_ALLOWUNDO`）+ `$Recycle.Bin` `$I`/`$R` 元数据对账撤销（拖入桌面回收站与右键移入回收站均支持 `Ctrl+Z` 原位恢复，移入回收站失败绝不静默退化至 `DiscardedFolder`）。
  - `windows/scripts/check.ps1`：20/20 全绿通过（0 警告 0 个错误），离屏渲染六套主题图在 `evidence/T-060/00-offscreen-theme-*.png`。
- **真机验收（由 Codex 在 `EnCopy Dev` 执行并保存至 `evidence/T-060/walk-report.md`）**：
  - **11 项全部 PASS（0 FAIL、0 SKIPPED）**：
    - 首启引导取消「登录时打开」（`01-onboarding-dialog.png`）与 50ms 级 3 轮连拷 + 链接/代码入库无丢失；
    - 六套主题即时重绘（`02-theme-paper.png`、`03-theme-pixel.png`、`04-theme-skeuo.png`、`05-theme-minimal.png`、`06-theme-neon.png`、`07-theme-washi.png`）与新复制探出 1.5s 自动收起（`08-new-item-peek.png`、`08a-tray-copy-80ms.png`、`08b-tray-copy-350ms.png`、`09-peek-auto-retracted.png`）；
    - 顶边左 1/3 `(200,1)` 不误触、正中 1/3 `(1280,1)` 路过滑下且不抢记事本焦点、移离后收起（`10-top-left-no-reveal.png`、`11-top-center-hover-reveal.png`、`12-top-hover-retracted.png`）；
    - 全屏避让抑制路过、`Alt+C` 强行呼出（`13-fullscreen-suppressed.png`、`14-fullscreen-altc-override.png`）；
    - 悬停放大位于搜索框下方、篮子展开隐藏（`15-hover-zoom-below-search.png`、`15b-hover-zoom-hidden-when-basket-open.png`）；
    - 「拿在手上」悬浮窗滚轮缩放/拖移/`Esc` 放回（`16-held-floating.png`、`17-held-zoomed-dragged.png`）与 Windows 原生共享面板（`18-windows-share-sheet.png`）；
    - 外部拖出到记事本（`19-drag-to-notepad.png`）和临时文件夹（`19b-drag-image-to-explorer-slow.png`），以及移入系统回收站 `$Recycle.Bin` 后 `Ctrl+Z` 恢复原位（`20-moved-to-recycle-bin.png`、`21-recycle-bin-restored.png`）。
- **验收辅助工具**：`evidence/tools/walk.exe`（源码 `evidence/tools/walk-src/Program.cs`，重建命令：`dotnet publish evidence/tools/walk-src/walk.csproj -c Release -r win-x64 --self-contained false -o evidence/tools`）。
