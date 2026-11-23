---
id: T-037
title: 新图标：绳上一张拍立得、一张 ⌥C 小票；菜单栏换成绳和两个夹子
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

[T-011](T-011-rename-to-encopy.md) 换上的是占位图标（灰墙上一张小票）。正式图标照 [T-034](T-034-paper.md) 的纸：夜色墨蓝的墙，两股麻绳，一张拍立得和一张写着 ⌥C 的撕边小票，各夹一个带弹簧的木夹；小票描着琥珀色边，就是 ⌥C 后被选中的样子。菜单栏还是上游的 T 恤符号，换成设计方案里的那个：一根下垂的绳，上面两个夹子（设计稿第 380 行）。

## Acceptance

- [x] App 图标是新画的，在 macOS 图标网格（824 点、四周留 100 点）里，32 像素时也认得出两张纸。
- [x] 菜单栏图标是绳和两个夹子，模板图（跟着菜单栏深浅走），18×12 点；在记复制内容时夹子是实心的，暂停时是空心的。
- [x] 自动检查通过；App 能编译。

## Resolution

完成，2026-11-23。`scripts/make-icon.swift` 重画，`scripts/build-app.sh` 不用改。新文件 `MenuBarIcon.swift` 画菜单栏的绳和夹子，`AppDelegate.updateStatusIcon` 改用它，原来的辅助功能说明照旧。构建出的图标：`evidence/T-037/app-icon.png`（从 `build/EnCopy Dev.app` 里的 `EnCopy.icns` 导出）；菜单栏两种状态放大 12 倍：`evidence/T-037/menubar.png`。

新加 `checkMenuBarIcon`（3 条）。把夹子改成总是实心的变异版本，检查失败在「The pegs are filled while watching, hollow when not」。`scripts/check.sh`：`294 checks passed`；`scripts/build-app.sh` 通过。

真机（2026-11-23，开发版，虚拟屏 + 外接屏；证据在本地 `evidence/walk-v1/`，不进 git）：没走到：菜单栏图标（内建屏和外接屏的菜单栏都太挤，图标没露出来）。用户这段时间在用鼠标（8 秒里 170 次硬件移动），按键和指针会抢，停在这里；自动检查是这一项现有的证据。

真机第二次（2026-11-23 02:10–02:20，用户离开电脑，开发版在虚拟屏上；证据在本地 `evidence/walk-v1b/`）：外接屏菜单栏里看到新的菜单栏图标：一根绳两个实心夹子（`13-menubar-sway.png`）。
