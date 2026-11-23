---
id: T-026
title: 记下复制时的来源 App
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

复制文字、链接时，记下当时在前台的 App，存进历史；纸上的时间后面写上这个 App 的名字（「今天 15:02 · 访达」）。这是 App 三档规则和按 App 清理的基础。图片的来源 App 等图片进篮子时一起记。

## Acceptance

- [x] 复制时前台的 App 记进历史，重新打开数据库还在。
- [x] 纸上时间后面显示 App 的名字；这台 Mac 上没有的 App 只显示时间。
- [x] 以前的历史文件打开时补上这一列，之后照样记。
- [x] 自动检查通过，原有检查不改。

## Resolution

完成，2026-11-23。`History` 加 `app` 列（旧库打开时 `ALTER TABLE` 补上），`record(_:text:app:)`、`app(of:)`、`appName(_:)`；`ClipboardWatcher` 记下前台 App 的 bundle id；`Note.app`、`Note.caption`，纸上用 caption。`scripts/check.sh`：`202 checks passed`，新增 6 项；不补列的变异让检查报错。真机走一遍和这一批一起走。

真机（2026-11-23，开发版，虚拟屏 + 外接屏；证据在本地 `evidence/walk-v1/`，不进 git）：复制时前台是 Paste Target（走查用的小程序），历史的 app 列记成 `local.encopy.pastetarget`。纸上的时间后面没有写 App 名字：这个小程序直接从可执行文件启动，系统查不到它的名字，所以没写；拿一个系统认识的 App（访达）复制、看纸上是不是写「访达」，这一步没走到。
