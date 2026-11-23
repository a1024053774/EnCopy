---
id: T-032
title: 清理历史面板和「最近清掉」
type: build
status: closed
blocked_by: [T-031]
claimed_by:
supersedes:
---

## What to build

[T-005](T-005-manual-cleanup.md) 定的手动清理。菜单栏「清理历史…」打开一个窗口：按类型、最后用到的时间、来源 App、包含的文字挑一批，先看会清掉几件和前几件是什么，再确认清掉；清掉的进「最近清掉」，7 天内整批放回。挂在绳上的（包括收藏的）不会被挑中。同一个窗口里能设原图最多占用多少，看下一次自动清理会过期几件、缩小几张。

## Acceptance

- [x] 不加条件时挑中除绳上以外的全部；按时间、类型、App、文字各自只挑中符合的。
- [x] 清掉后文件和历史行进「最近清掉」成一批；放回后连来源 App 一起回来。
- [x] 原图上限能设，不设时是 1 GB。
- [x] 自动检查通过，原有检查不改；整个 App 能编译。

## Resolution

完成，2026-11-23。`CleanupFilter`（在 `Clearing.swift`），`Line.cleanupCandidates`、`cleanUp`、`putBack(batch:)`、`originalsCap`；`Sources/EnCopy/CleanupPanel.swift` 是 SwiftUI 窗口；菜单「清理历史…」；`History.record` 在内容没变时也补上新知道的来源 App。`scripts/check.sh`：`127 strings`、`251 checks passed`，新增 10 项；不避开绳上东西的变异被抓到。

做这一单时发现：T-030 的 `isKeptImage` 只在检查程序里能编译（检查用的 `Inbox` 是替身），App 本身从 T-030 起编译不过。这里修好，并给 `scripts/check.sh` 加了一步对整个 App 做类型检查；把那个错放回去，这一步会报错。真机走一遍和这一批一起走。

真机（2026-11-23，开发版，虚拟屏 + 外接屏；证据在本地 `evidence/walk-v1/`，不进 git）：菜单「清理历史…」打开面板：类型、最后用到、来源 App、包含的文字四个条件，「最近清掉」和「保留」两段都在（`07-cleanup-panel.png`、`cleanup-panel-ax.txt`）。真的清掉一批、放回没走。
