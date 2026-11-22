---
id: T-011
title: 改名成 EnCopy，原样能编译能跑
type: build
status: open
blocked_by: []
claimed_by: devin
supersedes:
---

## What to build

项目文件夹变成 Tendedero 的 fork，打开就是 EnCopy：名字、bundle id、图标都是 EnCopy 自己的，界面只有简体中文和英文，其余行为和 Tendedero 一模一样。这一步只换身份，不加功能，后面每一步都在它上面改。依据 [T-001](T-001-fork-tendedero.md)、[T-010](T-010-languages.md)、[T-014](T-014-bundle-id.md)。

## Acceptance

- [x] 项目文件夹是 Tendedero 的 fork：保留上游提交历史，另设一个指向原仓库的上游地址，基线是 `3c5b575`；设计方案、项目地图、词汇表一起进仓库。
- [x] 构建出的 App 叫 EnCopy，bundle id 是 `app.encopy.EnCopy`；菜单、提示框、权限说明、访达右键服务里都不再出现 Tendedero 这个名字（说明「基于 Tendedero」的致谢除外）。
- [x] 图标是新的（占位也行），没有用 Tendedero 的图标或它 docs 里的图片。
- [x] 上游的 MIT 版权和许可声明原样保留；README 写明 EnCopy 基于 Tendedero 和它的作者。
- [x] 界面文字只剩简体中文和英文两份，字符串检查只校验这两份并通过。
- [x] 改名后的本地检查脚本全部通过（上游现有 80 项）。
- [x] EnCopy 的设置和文件放在自己的位置，不读写 Tendedero 的；两个 App 同时装着互不影响。
- [ ] 真机：打开 EnCopy，在菜单栏打开记录复制的文字后，截一张图、复制一段文字，都挂上绳，行为和 Tendedero 一样。留截图和步骤。
- [x] 项目地图里词汇表的覆盖范围改成代码目录，`status` 无问题。

## Resolution

进行中：自动检查和构建产物的检查都过了，真机那一项还没走，工单不关。实现提交 `3aca598`，分支 `feat/T-011-rename-to-encopy`。证据文件在本地的 `evidence/T-011/`（不进 git）。

- **fork**：`origin` 是 GitHub 上的公开 fork `a1024053774/EnCopy`（parent 为 `alejandrobujan/tendedero`），`upstream` 指向原仓库；`git merge-base --is-ancestor 3c5b575 HEAD` 成立。设计方案、项目地图、词汇表由 PR #1 进仓库。
- **名字和 bundle id**：`scripts/build-app.sh` 产出 `build/EnCopy.app`。Info.plist 里 `CFBundleIdentifier` 是 `app.encopy.EnCopy`，名字、可执行文件、图标文件都是 `EnCopy`，服务菜单项是 `Hang on EnCopy`，版本 `0.1.0`。`grep -rIil tendedero build/EnCopy.app --exclude=LICENSE` 没有命中，二进制里 `strings | grep -ic tendedero` 是 0（`app-inspect.log`）。
- **图标**：`scripts/make-icon.swift` 重画，一张小票用木夹夹在麻绳上（`icon.png`，人工看过）；上游的 `docs/` 和配图脚本已删。
- **许可**：`git diff 3c5b575 -- LICENSE` 为空；README 写明基于 Tendedero 及其作者；构建时把 LICENSE 放进 App。
- **语言**：只剩 `en.lproj`、`zh-Hans.lproj`。`swift scripts/check-strings.swift` 输出 `86 strings, checked in en, zh-Hans`。多放一个 `fr.lproj`、删掉 zh-Hans 的一条，它都报错退出（`mutants.log`）。
- **本地检查**：`scripts/check.sh` 输出 `80 checks passed`（`check.log`）。改名前的基线是上游 `3c5b575` 用 macOS 26 SDK 跑出的 `80 selection checks passed`（`baseline-checks-3c5b575-sdk26.log`）；默认的 macOS 27 SDK 在这台机器上编译不了 SwiftUI，所以加了 `scripts/swift-sdk.sh`。在源码里放回一处上游名字，`check.sh` 一秒内报错退出。
- **自己的位置**：启动 `build/EnCopy.app` 后，设置写在 `app.encopy.EnCopy`，文件夹建在 `~/Library/Application Support/EnCopy/`；没有出现 `app.tendedero.*` 的设置和 `Application Support/Tendedero`（`launch-smoke.log`）。日志子系统、队列名、剪贴板标记、服务端口名也都改了。这台机器没装 Tendedero，两个 App 同时运行没有实测；两边都打开「接管截图」时会改同一份系统截图设置，这一点和上游一样。
- **地图**：词汇表的覆盖范围改成 `Sources/**`，登记了 README；`status` 无问题。
- **真机（未完成）**：只做到启动。`open build/EnCopy.app` 后进程在跑，首次启动的提示框和绳子面板两个窗口都在，系统截图设置前后一致，没有崩溃报告（`launch-smoke.log`）。点提示框、从菜单栏打开文字记录、截图和复制后看绳上，这几步需要操作界面和看屏幕。这台机器上 lcu 只接受 Claude 宿主的调用；Devin 没有辅助功能、屏幕录制和控制 System Events 的权限，所以还没走。
