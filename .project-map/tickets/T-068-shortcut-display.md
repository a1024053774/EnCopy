---
id: T-068
title: 改了快捷键，设置里马上显示新的
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
issue: 107
---

## What to build

在设置「通用」页点「更改快捷键…」、按下新的快捷键后，「快捷键」那一行马上显示新的快捷键；按删除键移除时显示「无」；按 Esc 取消或新快捷键被别的 App 占了时，显示的是仍然生效的那个。现在新快捷键已经生效，设置里却还写着旧的，看起来像没改成功（[#107](https://github.com/a1024053774/EnCopy/issues/107)，V2EX 用户反馈）。

原因：录快捷键是异步的，设置窗口点完按钮就不管了，录完没人叫它重画。

## Acceptance

- [x] 真机：开发版设置里把 ⌥C 改成另一个快捷键，录完后「快捷键」一行立刻显示新的，不用关掉设置重开；新快捷键能叫出绳子。留截图。
- [x] 真机：再改回 ⌥C，显示跟着变回来。
- [x] 本地检查通过。

## Resolution

2026-11-25。设置的「更改快捷键」动作改成录完回调一次（不管改成、移除、取消还是被占），设置窗口在回调里重画。

- 先在没改的开发版上复现：设置里录 ⌥V，偏好里已经是 ⌥V，「快捷键」一行仍写 ⌥C（`evidence/T-068/old-03-after-change.png`）。
- 改后的开发版（虚拟屏上走，设置窗口用辅助功能按按钮、按窗口号截图）：打开时显示 ⌥V（上一轮留下的），录 ⌥C 后立刻显示 ⌥C（`new-02-after-option-c-row.png`）；再录 ⌥V 立刻显示 ⌥V（`new-03-row.png`），按 ⌥V 叫出了绳子和搜索框（`new-04-option-v-summons.png`）；再录回 ⌥C，显示 ⌥C（`new-05-row.png`），偏好里是 keyCode 8。
- `ENCOPY_CHECKS_SCREEN="EnCopy walk screen" scripts/check.sh`：468 checks passed。设置窗口和 AppDelegate 不在本地检查编译的范围里，这一条只靠真机。

