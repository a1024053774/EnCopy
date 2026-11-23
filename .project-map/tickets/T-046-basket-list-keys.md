---
id: T-046
title: 篮子列表里打字筛、跳组和其余按键；路过时点把手；去掉按天翻页
type: build
status: closed
blocked_by: [T-045]
claimed_by:
supersedes:
---

## What to build

[T-043](T-043-basket-list.md) 的第三刀：列表开着时，直接打字在整只篮子里筛，Tab / ⇧Tab 按种类筛，← → 跳到上一组 / 下一组，⇧⏎ 只贴字，⌘Y 快速查看，⌘P 收藏回绳上。路过（指针停在菜单栏正中、绳子滑下来）时也露出篮子把手，点它打开列表、拿到键盘。原来 ↓ 把绳上换成「今天 / 昨天 / 本周 / 更早」一格的翻页（[T-033](T-033-basket-shelves.md)）去掉，绳上不再换内容。

## Acceptance

- [x] 列表里打字只留匹配的（含图里的字和文件名），删掉字回到全部；Tab 按种类筛，组标题跟着筛。
- [x] ← → 跳组；⇧⏎、⌘Y、⌘P 在列表里照绳上的意思做。
- [x] 路过时把手露出，点它打开列表并能用键盘。
- [x] ↓ 不再把绳上换成一格；T-033 的检查改成新的行为，其余检查不改。

## Resolution

完成，2026-11-23。列表开着时打字和 Tab 交给列表（`Line.narrowsLine`、`loadBasket` 走 `history.search`），← → 走 `Line.horizontalArrow` / `jumpBasketGroup`，⌘P、⌘Y 走 `Line.pinPick` / `quickLookPick`，⇧⏎ 走 `Paster.pastePicked(plain:)`。把手由 `Line.handleShown` 决定：⌥C 和路过时露，探出来时不露。去掉了 T-033 的 `Shelf`、`moveShelf`、`span(of:)`、`loadShelf`、搜索框里的格子名和 `History.search` 的 `used` 参数。提交 `ed7392f`，词汇表和 README `6da9bc1`。

检查：`checkBasketKeys` 9 项；T-033 的检查改成列表行为（`checkBasketListSearch`、Tab 筛、千条之后的旧件、↓ 打开列表时绳子弹一下）。改成「列表开着也沿绳挑」时「→ 跳到昨天第一条」那项报错。`scripts/check.sh`：428 项通过。

真机（2026-11-23 09:03–09:11，开发版，MacBook 内建屏，纸主题和霓虹主题；证据在本地 `evidence/T-044/`，不进 git）：打字 swift 只留一条（`12-filtered.png`）；Tab 只看文字、再 Tab 只看链接，组标题跟着变（`21-tab-text.png`、`22-tab-links.png`）；⌘P 把 figma 那条收藏到绳结左边，它离开列表（`23-pinned.png`）；指针停在菜单栏正中，绳子滑下来、把手露出（`25-hover.png`）；点把手打开列表（`26-handle-click.png`），接着 ↓ 能走（`27-handle-keys.png`）。没走：⌘Y、⇧⏎（检查覆盖）。
