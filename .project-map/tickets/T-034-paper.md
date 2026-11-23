---
id: T-034
title: 一种东西一种纸，木夹和麻绳
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

[T-003](T-003-paper-style.md) 定的纸：文字是撕边小票，代码是针式打印纸（两侧一排孔、浅绿条纹），链接是牛皮纸吊牌（切角、穿绳孔），图片是拍立得（白边，下边宽，写时间），文件是牛皮纸文件夹（T-029 已做）。夹子换成中间一圈弹簧的木夹，收藏的漆成深灰；绳子换成两股颜色缠在一起的麻绳。选中的那张描琥珀色边，往下沉 4 点、放大到 1.06 倍。深色模式里纸还是浅色的纸。号码贴纸跟 ⌘1–⌘9 一起，用户这一轮没要，不做。

## Acceptance

- [x] 文字、代码、链接、图片、文件各有自己的纸，浅色和深色模式下都是浅色的纸。
- [x] 木夹带弹簧，收藏的夹子深灰；绳子是两股的麻绳。
- [x] 选中的那张琥珀色描边、下沉、放大。
- [x] 纸的大小和原来的卡片一样，布局、飞行和点中范围不变；自动检查通过，原有检查不改；App 能编译。

## Resolution

完成，2026-11-23。`NoteSheet` 改画三种纸，`PaperShape` 管小票撕边和吊牌切角；`PeggedView` 按种类换纸，拍立得写时间，选中改成琥珀色；`Clothespin` 换木夹；`Rope` 换麻绳。用和检查一样的方式编了一个渲染程序，把真的 `LineView` 挂上五种东西各画一张，浅色、深色各一张：`evidence/T-034/light.png`、`dark.png`（重画：`evidence/T-034/run.sh evidence/T-034 <输出.png> [dark]`）。`scripts/check.sh`：`261 checks passed`；`scripts/build-app.sh` 通过。

留下的事：拿在手上（Hold）的便签还是原来的样子；绳结右边放最新的在下一张单。
