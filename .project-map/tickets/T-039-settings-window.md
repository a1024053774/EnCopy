---
id: T-039
title: 菜单栏精简，不常用的选项移到设置窗口
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

用户 2026-11-23：菜单栏展开东西太多了，要精简，不常用的操作尽量放到软件的设置界面。

## Acceptance

- [x] 菜单只留每天用的：显示晾衣绳（⌥C）、挂回去、全部取下、挂回全部（有拿在手上的时）、暂停记录、清理历史…、设置…（⌘,）、退出。
- [x] 其余都在「设置」窗口，四个标签页：通用（登录时打开、声音、快捷键、检查更新）；挂上复制的内容（图片、文字、链接、文件，每项一句说明；按 App 记录，打开设置前在前台的 App 排第一）；截图（接管截图、自动复制每张截图、打开截图文件夹、同时监视文件夹：在访达中显示、不再监视、添加）；晾衣绳（放下晾衣绳的三种时机、绳上保留、大小、悬停时放大）。
- [x] 首次打开的两个提示框里「从菜单栏关掉 / 在菜单栏里选」改成「在设置里」。
- [x] 自动检查通过；App 能编译。

## Resolution

完成，2026-11-23。新文件 `SettingsWindow.swift`（`SettingsWindow`、`SettingsModel`、`SettingsView`）；需要 App 配合的动作（换截图监视、改大小、改快捷键……）由 `AppDelegate.showSettings()` 以 `SettingsActions` 传进去，原来的 `appRulesItem()` 删掉。原来菜单项的悬停说明现在写在设置里每个开关下面。四个标签页的渲染：`evidence/T-039/tabs.png`（重画：`evidence/T-039/run.sh evidence/T-039`，不启动 App）。设置窗口和 `AppDelegate` 不在检查程序编译的范围里，这一单没有新的自动检查，证据是渲染图和整个 App 的类型检查。真机点一遍菜单和设置窗口，和下一次真机验收一起走。

真机第二次（2026-11-23 02:10–02:20，用户离开电脑，开发版在虚拟屏上；证据在本地 `evidence/walk-v1b/`）：菜单只有：显示晾衣绳、挂回去、全部取下、暂停记录、清理历史…、设置…、退出 EnCopy（`menu-items.txt`）。设置窗口四个标签页都能切换，开关显示的是真实状态，「按 App 记录」第一个是当时在前台的 PasteTarget，下面是预设不记的「密码」「钥匙串访问」（`11-settings-row.png`）。
