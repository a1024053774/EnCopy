---
id: T-054
title: Windows 版的技术栈与目录结构
type: decide
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## Question

Windows 版用什么技术栈？源码、脚本和构建产物放在哪里？Mac 版现有文件动不动？

## Resolution

选 C# / .NET 10（`net10.0-windows10.0.22621.0`）+ WPF，配合 Win32 P/Invoke 与 WinRT 接口；SQLite 使用内置 FTS5（含 `trigram` 分词器）的 `Microsoft.Data.Sqlite`。来源：用户 2026-11-25 拍板（.NET 8 已于 2026-11-10 停止支持，本机已有 .NET 10 Desktop Runtime）。

依据：EnCopy 需要屏幕顶栏透明置顶且空白像素自动穿透鼠标、非激活窗口焦点控制（`WS_EX_NOACTIVATE`）、多个轻量悬浮窗、原生 `DoDragDrop` 多格式拖出以及按屏幕刷新率运行的弹簧动画。WPF 原生支持逐像素透明命中测试、单进程低内存开销与 OLE 拖拽语义，没有 WinUI 3 透明穿透限制或 WebView2 多进程、空白区吞点击的问题。

目录与隔离约定：

- Windows 相关的源码、测试、脚本（`windows/scripts/`）和构建产物全部放在仓库根目录 `windows/` 下；Mac 版的 `Sources/`、`Package.swift` 和 `scripts/` 一律不动。
- 日常版与开发版分开，沿用 [T-017](T-017-dev-copy-own-data.md)：
  - **日常版**：名字 `EnCopy`，装在 `%LOCALAPPDATA%\Programs\EnCopy\`，数据文件夹在 `%LOCALAPPDATA%\EnCopy\`。
  - **开发版**：名字 `EnCopy Dev`，构建在 `windows/build/EnCopy Dev/`，数据文件夹在 `%LOCALAPPDATA%\EnCopy Dev\`，真机验收与本地测试绝不污染日常版历史。
