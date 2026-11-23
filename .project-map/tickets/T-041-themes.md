---
id: T-041
title: 5 套主题：像素、拟物、极简、霓虹、手账
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

用户 2026-11-23：「再设计几套主题，比如像素风，拟物风等等，一共设计 5 套」。在 T-034 的纸之外再做 5 套，设置里切换。主题只换外观：纸的底、图案、边、圆角、字体、文字颜色、阴影，夹子，绳子，选中的描边；纸上的内容、挂的位置、点中的范围、动画都不变。

## Acceptance

- [x] 设置 → 晾衣绳 →「主题」：纸（默认）、像素、拟物、极简、霓虹、手账。换了马上全部重画，下次打开还是它；存了不认识的值回到纸。
- [x] 像素：直角、黑边、硬投影、方块夹子、方格虚线绳、亮黄粗选中边。拟物：真纸渐变和厚阴影、金属长尾夹、粗麻绳带高光。极简：白卡片大圆角发丝边、小圆钉、细灰线、系统强调色选中。霓虹：深色卡片发光描边、发光圆点、发光青线、粉色选中。手账：点阵纸、斜贴的条纹和纸胶带、红白细绳、珊瑚色选中。
- [x] 文字、代码、链接、图片、文件五种纸在每套主题里都有样子；纸主题和原来一模一样。
- [x] 自动检查通过；App 能编译。

## Resolution

完成，2026-11-23。新文件 `Theme.swift`（`Theme`、`ThemeCardBackground`、`ThemePeg`、`ThemeRopeStroke`、`ThemeFileCard`、`FileIcons`）；`Line.theme` 发布当前主题；`NoteSheet`、`PeggedView`、`Rope` 按主题画；设置窗口加「主题」。顺手：卡片的阴影前加 `compositingGroup()`，阴影只投一次（像素主题的硬投影原来会给纸上每段字各投一份）。

六套主题的渲染（同一组纸：PDF 收藏、便签选中、代码、链接、截图，带来源 App 图标）：`evidence/T-041/themes-light.png`，单张 `evidence/T-041/<主题>.png`（重画：`evidence/T-034/run.sh evidence/T-041 <输出.png> <主题>`）。新检查 `checkThemes`（5 条）。`scripts/check.sh`（虚拟屏上）：`146 strings`、`371 checks passed`。真机看手感和深色模式，下一次走查一起。
