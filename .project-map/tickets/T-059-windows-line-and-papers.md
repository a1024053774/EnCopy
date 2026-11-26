---
id: T-059
title: Windows 屏幕顶部的绳子与五种纸，接上篮子列表
type: build
status: closed
blocked_by: [T-058]
claimed_by: antigravity
supersedes:
---

## What to build

把 [T-058](T-058-windows-tracer-bullet.md) 验证过的列表界面升级为 EnCopy 标志性的屏幕顶部绳子、五种纸卡片与按天按月分组的篮子列表，让日常界面与交互完整对齐 [设计方案](../../design/encopy-design.html) 与 [T-043](T-043-basket-list.md)。

包含内容：

- 屏幕顶部透明置顶面板（空白像素鼠标穿透，卡片和把手处可点可悬停），画出抛物线麻绳、绳结、木夹与号码贴纸；收藏卡片固定在绳结左边，其余最新在绳结右边第一个、越往右越旧；支持绳子容量设置（「能挂几件挂几件」与 25/50/100 滚动）。
- 五种纸卡片（小票文本、打孔纸代码、牛皮纸吊牌链接、拍立得图片占位/预览、文件夹占位）与左上角种类标签（六色区分），文本支持标题/正文切分与 Markdown 预览，暗黑模式下纸面保持浅色、绳与搜索栏随系统深浅切换。
- 绳子交互完整落地：`←` `→` 挑（及 `Shift+←/→` 多选按顺序贴）、`Tab`/`⇧Tab` 种类筛选、`Ctrl+P` 收藏、`Ctrl+Backspace`/`Delete` 取下、`Ctrl+Z` 放回（含退回原位）、单击复制、双击贴回；用到或再次复制同一内容时回到绳头。
- 挂满后最老非收藏条目落入篮子，绳子右下角挂「篮子 N 件 ↓」把手；按 `↓` 或点把手在搜索框下方展开篮子列表（今天、昨天、本周、按月分组，带种类筛选条与计数），支持 `↑`/`↓` 挑、←/→ 跳组、`⏎`/`⇧⏎`/`Ctrl+P`，在最上面一条按 `↑` 回到绳上。
- 补齐全局快捷键录制界面（支持自定义改键与清除，冲突或非法单键即时提示）以及日常版安装脚本 `windows/scripts/install.ps1`（先退出正在运行的日常版 `EnCopy`，再构建日常版安装到 `%LOCALAPPDATA%\Programs\EnCopy\`）。

## Acceptance

- [x] 绳子面板在主屏、副屏与不同 DPI 缩放下位置贴合顶栏，空白透明区点击与滚轮完全穿透到底下应用，卡片与把手响应点击。
- [x] 收藏卡在绳结左、普通卡最新贴近绳结右；双击贴、单击复制或重复复制同内容会把非收藏项挪回绳头；绳满后最老项掉进篮子且不丢失历史记录。
- [x] 文本、代码、链接正确渲染对应纸张形状、种类标签与暗黑/浅色配色；键盘全部绑定（`←`/`→`/`Shift+←/→`/`Tab`/`Ctrl+P`/`Ctrl+Backspace`/`Ctrl+Z`/`↓`/`↑`/`⏎`/`⇧⏎`/`Esc`）与点击交互符合词汇表与设计方案。
- [x] 支持在快捷键录制界面修改与清除全局唤起快捷键并重启保留；运行 `windows/scripts/install.ps1` 能把日常版安装到 `%LOCALAPPDATA%\Programs\EnCopy\`，与开发版 `EnCopy Dev` 数据和进程互不干扰。
- [x] `windows/scripts/check.ps1` 新增针对纸张分类、绳槽顺序、挪回绳头、篮子分组与撤销步的测试并全绿通过。
- [x] 真机走查：使用 `EnCopy Dev` 挂满一屏以上内容并验证掉进篮子、展开篮子列表、搜索筛选、收藏/取下/撤销、单双屏与深浅色外观，留截图与步骤在 `evidence/T-059/`。

## Resolution

已完成真机走查与增量复验，全部验收条件达成：

- 代码提交：`b99ce01`、篮子撤销补卡与监听生命周期修复 `06f8c23`、同步 `T-058` 收尾提交 `44749f4`（分支 `feat/T-059-windows-line-and-papers`）。
- 自动化检查：`powershell -ExecutionPolicy Bypass -File windows/scripts/check.ps1` 全绿通过（6 项静态门禁 + 双版本 0 警告编译 + 16 项隔离测试 `[check] PASS`，含 5 种纸与 Markdown 渲染、`CardClickCoordinator` 防双击竞态、多选取下与 `Ctrl+Z` 原顺序放回、`IOException` 两阶段回滚、模态篮子空筛选防穿孔、滚动偏移夹紧、满绳篮子 `Ctrl+P` 再 `Ctrl+Z` 按序补回（含 `[Pin -> AddNew -> Undo]` / `[Pin1 -> Pin2 -> Undo -> Undo]` 与 `KeepOnLine=25` 未挤卡零回填约束）、`canUndoQuery` 放行判定、离屏 `alpha == 0` / `HitTest == null` 点击穿透校验以及 `install.ps1` 隔离安装校验）。
- 失败-修复证据（`evidence-first-testing`）：针对首轮真机走查报告 `evidence/walk-report.md` 发现的「绳满时从篮子 `Ctrl+P` 收藏再 `Ctrl+Z` 撤销导致绳上少一张」以及复审指出的「`KeepOnLine=25` 未挤下卡时误回填无关篮子卡」「`[Pin -> AddNew -> Undo]` 时序倒挂」均先立红灯测试再修正 `TrimToCapacity` + `PutBack()` 补回上界与最近使用优先级后转绿。
- 独立代码审查：`design-integrity-review`（`same-model review`）两轮审查识别的 `[P1]` 暂停态监听空转/滞留与 `[P2]` 无界回填已全部修复、加单测并重跑全绿。
- 真机走查与复验报告：`evidence/walk-report.md`、`evidence/walk-recheck.md`（由 Codex 按走查要求在真实双屏桌面执行）。
  - 挂满溢出进篮子、展开篮子列表、搜索与 `Tab` 种类筛选：`evidence/T-059/line-full-light.png`、`basket-open.png`、`basket-search-settled.png`、`filter-tab-settled.png`、`secondary-basket.png`、`secondary-basket-filter-link.png`。
  - 满绳篮子首件 `Ctrl+P` 收藏挤下末位卡再 `Ctrl+Z` 恢复 10 张原始顺序：`evidence/T-059/recheck-basket-open.png`、`recheck-basket-pin.png`、`recheck-basket-pin-undo.png`。
  - 绳上 `Ctrl+Backspace` 取下与 `Ctrl+Z` 原位放回、单击复制回绳头与双击贴回：`evidence/T-059/remove-before.png`、`removed.png`、`remove-undo.png`、`click-before.png`、`click-single-settled.png`、`click-double.png`。
  - 小票/链接吊牌/代码打孔纸外观与深浅色外壳对比（暗色下纸面保持暖浅色）：`evidence/T-059/paper-types.png`、`code-paper.png`、`line-dark.png`。
  - 主副屏透明空白区点击穿透与面板保持打开态的滚轮穿透（`walk.exe wheel -370 270 120` 使底层记事本上滚 3 行且面板不关）：`evidence/T-059/transparent-click.png`、`secondary-transparent-click.png`、`recheck-notepad-lines.png`、`recheck-wheel-before.png`、`recheck-wheel-after.png`、`recheck-wheel-compare.png`。
  - 主屏 100% + 副屏 125% 混合 DPI 摆放贴合顶栏：`evidence/T-059/mixed-dpi-secondary-placement.png`。
  - 离屏视觉预览：`evidence/T-059/00-offscreen-line-light.png`、`00-offscreen-basket-dark.png`、`00-offscreen-shortcut-recorder.png`。
- 本地验收辅助工具：`evidence/tools/walk.exe`（位于 git 忽略目录，源码在 `evidence/tools/walk-src/`，含 `tray`、`right-click`、`wheel` 子命令），重建命令：`dotnet publish evidence/tools/walk-src/walk.csproj -c Release -r win-x64 --self-contained false -o evidence/tools`。
