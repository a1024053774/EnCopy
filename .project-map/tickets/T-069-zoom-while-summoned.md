---
id: T-069
title: ⌥C 叫出来的绳子上，悬停也放大
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
issue: 108
---

## What to build

开着「悬停时放大」，指针停在绳上的截图上就放大，不管绳子是指针停在菜单栏放下来的，还是按 ⌥C 叫出来的。现在 ⌥C 叫出来时一直不放大：用户第一次从菜单栏放下来时看到了放大，之后用 ⌥C 就再也看不到，以为是只出现一次的 bug（[#108](https://github.com/a1024053774/EnCopy/issues/108)，V2EX 用户反馈）。

原因：放大在「EnCopy 自己有窗口在前面」时关掉，本意是让给快速查看和设置；⌥C 的搜索框也是 EnCopy 的窗口，叫出来的整段时间都拿着键盘，于是一直被当成「有窗口在前面」。

篮子列表展开时不放大：放大的图会盖住列表。

## Acceptance

- [x] 真机：⌥C 叫出绳子（搜索框在），指针停在截图上，放大出现；Esc 收起后再 ⌥C，再停，还是放大。留截图。
- [x] 真机：从菜单栏放下来的绳子，悬停放大照旧。
- [x] 真机：⌥C 后按 ↓ 展开篮子列表，指针停在绳上的截图上，不放大。
- [x] 本地检查通过。

## Resolution

2026-11-25。放大的「有窗口在前面」判断里，把 ⌥C 的搜索框除外；篮子列表展开时搜索框照旧算在前面，不放大。

- 先在没改的开发版上复现：从菜单栏放下来悬停能放大，收回再放下来也能（`evidence/T-069/old-00-revealed.png`、`old-02-second-hover.png`）；⌥C 叫出来再停在截图上不放大（`old-03-summoned-hover.png`）。
- 改后的开发版（虚拟屏上走）：⌥C 叫出来悬停放大，Esc 收起后连着两轮再 ⌥C 悬停都放大（`evidence/T-069/03-summoned-again-hover.png`、`04-third-summon-hover.png`）；⌥C 后按 ↓ 展开篮子列表，悬停不放大（`05-basket-list-hover.png`）；从菜单栏放下来悬停照旧放大（`06-menubar-hover.png`）。放大的图在搜索框下面，不挡搜索框。
- `ENCOPY_CHECKS_SCREEN="EnCopy walk screen" scripts/check.sh`：468 checks passed。放大和 AppDelegate 不在本地检查编译的范围里，这一条只靠真机。

