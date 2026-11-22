---
id: T-006
title: 动画做到什么程度
type: decide
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## Question

动画要做到什么程度，怎么和高频键盘操作相处？

## Resolution

细腻：纸按轻重摆动，挂上、取下、贴出去都有来去的路线，麻绳下垂回弹，偶尔有微风。三条规矩：按键在按下那一帧生效，不等动画、不排队；越常用越克制；全部用弹簧。系统「减弱动态效果」打开时只留淡入淡出。逐项参数见[设计方案 v0.3「动画」](../../design/encopy-design.html#motion)，标「沿用 Tendedero」的数值取自上游 `PeggedView.swift`、`LineView.swift`、`Line.swift`。

来源：用户 2026-11-22「软件的动画也要做好，动画要细腻」；「按键不等动画」等三条规矩写在 v0.3 方案里，用户看过方案后让继续做完。
