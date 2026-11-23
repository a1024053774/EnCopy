---
id: T-023
title: 双击一张纸贴到前台 App
type: build
status: closed
blocked_by: [T-007]
claimed_by:
supersedes:
---

## What to build

[T-007](T-007-double-click.md) 定了：双击一张纸，贴到前台 App，和 ⏎ 走同一条路（先复制、绳子收起、再发 ⌘V；没有辅助功能权限时只复制并提示）。打开挪进右键菜单（菜单里本来就有「打开」）。篮子里的纸也一样。

## Acceptance

- [x] 双击绳上的一张纸：收起绳子，发一次 ⌘V，剪贴板里是这张纸的字，这张纸回到绳头。
- [x] 右键菜单里还有「打开」。
- [x] 自动检查通过，原有检查不改。

## Resolution

完成，2026-11-23。`GrabView` 的双击改调 `Line.paste`，App 把它接到 `Paster.paste`，⏎ 也改走这个函数。`scripts/check.sh`：`176 checks passed`，新增 3 项走真的卡片双击；把双击改回「打开」后检查报错。真机走一遍放到这一批键盘鼠标功能做完后一起走，结果记在最后那张单里。

真机（2026-11-23，开发版，虚拟屏 + 外接屏；证据在本地 `evidence/walk-v1/`，不进 git）：没走到：双击贴。用户这段时间在用鼠标（8 秒里 170 次硬件移动），按键和指针会抢，停在这里；自动检查是这一项现有的证据。
