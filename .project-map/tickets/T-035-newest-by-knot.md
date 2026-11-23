---
id: T-035
title: 最新的挂在绳结右边第一个
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

设计稿第 502 行：最新的一件挂在绳结右边第一个，越往右越旧；收藏的钉在绳结左边。Tendedero 原来是新的挂在最右端。← → 跟着屏幕方向走：→ 往右，也就是往旧的走；← 往左，从最新的再往左就越过绳结到收藏。号码牌跟 ⌘1–⌘9 一起没要，不做。

## Acceptance

- [x] 从左到右：收藏的（按原顺序）、绳结、最新的、越来越旧的。
- [x] ⌥C 仍然选中最新的；→ 选下一张更旧的，← 往左，两头都不绕回。
- [x] 搜索、篮子里按天翻（↓）时没有绳结，最新的在最左。
- [x] 挂满开了「留在绳上」时，最新的停在绳结右边第一格，旧的从右边滚进来；触控板滑动跟手。
- [x] 自动检查通过；App 能编译。

## Resolution

完成，2026-11-23。`Layout.slot` 把存储顺序（旧的在前，收藏的最前）换成挂的位置（收藏的在前，其余倒过来），`Layout.x`、绳结位置、选中跟随滚动都按位置算；← → 按挂的顺序走（`pickableInPlace`）。新加 `Line.layoutPinned`：搜索和篮子按天翻时为 0，原来篮子翻天时也会画出绳结并按绳上的收藏数排，一起修了；按种类筛选（Tab）时按显示出来的那些算。触控板滑动的方向取反，保持跟手。

需求变了，原有检查跟着改：方向键相关的 7 处 `movePick(±1)` 对调，「Left moves the pick one older」改成「Right moves the pick one older」等，检查的意思没变，只是方向跟着新的布局。新加 `checkNewestByKnot`（12 条）。把 `slot` 改回恒等（旧布局）的变异版本，检查失败在「The newest pick shows the newest end」。渲染：`evidence/T-035/light.png`（重画：`evidence/T-034/run.sh evidence/T-034 <输出.png> [dark]`），收藏的 PDF、绳结、最新的（琥珀色选中）、再往右越来越旧。`scripts/check.sh`：`274 checks passed`；`scripts/build-app.sh` 通过。

真机（2026-11-23，开发版，虚拟屏 + 外接屏；证据在本地 `evidence/walk-v1/`，不进 git）：⌥C 后最新的（刚复制的图片）在最左、描着琥珀色边，往右依次是更早复制的，最右是更早的几条（`02-summon.png`）。之后用户在用鼠标，绳子被点到别处收起，→、⌘P、越过绳结的 ← 都没走到。
