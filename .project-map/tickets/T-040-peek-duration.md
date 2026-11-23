---
id: T-040
title: 新内容弹出：停留时间可调，默认短一点
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

用户 2026-11-23：复制后绳子弹出来的时间有点长，影响工作；要能短一点或者不弹，加开关和调整时间。

## Acceptance

- [x] 开关：「设置 → 晾衣绳 → 有新内容挂上时」，关掉就不弹（原来就有）。
- [x] 停留时间：0.8、1.5、2.5、4 秒四档，默认 1.5 秒（原来固定 2.5 秒）；开关关着时这一项变灰。存了别的值回到默认。
- [x] 「挂回去」时露出来的时间也用它。
- [x] 自动检查通过；App 能编译。

## Resolution

完成，2026-11-23。`Line.peekChoices`、`Line.peekSeconds`（UserDefaults `peekSeconds`）；`AppDelegate` 两处原来写死的 2.5 秒改用它；设置窗口「晾衣绳」页加「停留时间」。渲染：`evidence/T-040/tab3.png`。新检查 `checkPeekDuration`（4 条），旧代码上编译不过。`scripts/check.sh`（虚拟屏上）：`138 strings`、`360 checks passed`。
