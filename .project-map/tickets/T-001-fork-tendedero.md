---
id: T-001
title: 在 Tendedero 上改，不从零写
type: decide
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## Question

EnCopy 从零写，还是 fork Tendedero 改？

## Resolution

Fork Tendedero 改。来源：用户 2026-11-22「计划在 tendedero 的架子上魔改，不从 0 写」。

依据：Tendedero 是 MIT 许可的 Swift/AppKit/SwiftUI 代码，约 6,600 行，已经有绳子和卡片的绘制与摆动、手势、截图接管、剪贴板监听、不抢前台就能拿键盘的面板（`SearchBar.swift` 里的 `SearchPanel`），以及不依赖 XCTest 的本地检查脚本 `scripts/test-selection.sh`。

上游基线：github.com/alejandrobujan/tendedero 主分支 `3c5b575`（2026-11-18，版本 2.0.0）。

许可约束（上游 LICENSE）：保留 MIT 版权和许可声明；名字和图标不在许可里，改过的版本必须用别的名字和别的图标，不能暗示自己是 Tendedero 或得到作者认可。可以写明「基于 Tendedero」。
