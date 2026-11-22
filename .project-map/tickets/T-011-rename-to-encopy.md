---
id: T-011
title: 改名成 EnCopy，原样能编译能跑
type: build
status: closed
blocked_by: []
claimed_by:
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
- [x] 真机：打开 EnCopy，在菜单栏打开记录复制的文字后，截一张图、复制一段文字，都挂上绳，行为和 Tendedero 一样。留截图和步骤。
- [x] 项目地图里词汇表的覆盖范围改成代码目录，`status` 无问题。

## Resolution

完成。实现提交 `3aca598`，分支 `feat/T-011-rename-to-encopy`，PR #2。证据文件在本地的 `evidence/T-011/`（不进 git）。

- **fork**：`origin` 是 GitHub 上的公开 fork `a1024053774/EnCopy`（parent 为 `alejandrobujan/tendedero`），`upstream` 指向原仓库；`git merge-base --is-ancestor 3c5b575 HEAD` 成立。设计方案、项目地图、词汇表由 PR #1 进仓库。
- **名字和 bundle id**：`scripts/build-app.sh` 产出 `build/EnCopy.app`。Info.plist 里 `CFBundleIdentifier` 是 `app.encopy.EnCopy`，名字、可执行文件、图标文件都是 `EnCopy`，服务菜单项是 `Hang on EnCopy`，版本 `0.1.0`。`grep -rIil tendedero build/EnCopy.app --exclude=LICENSE` 没有命中，二进制里 `strings | grep -ic tendedero` 是 0（`app-inspect.log`）。
- **图标**：`scripts/make-icon.swift` 重画，一张小票用木夹夹在麻绳上（`icon.png`，人工看过）；上游的 `docs/` 和配图脚本已删。
- **许可**：`git diff 3c5b575 -- LICENSE` 为空；README 写明基于 Tendedero 及其作者；构建时把 LICENSE 放进 App。
- **语言**：只剩 `en.lproj`、`zh-Hans.lproj`。`swift scripts/check-strings.swift` 输出 `86 strings, checked in en, zh-Hans`。多放一个 `fr.lproj`、删掉 zh-Hans 的一条，它都报错退出（`mutants.log`）。
- **本地检查**：`scripts/check.sh` 输出 `80 checks passed`（`check.log`）。改名前的基线是上游 `3c5b575` 用 macOS 26 SDK 跑出的 `80 selection checks passed`（`baseline-checks-3c5b575-sdk26.log`）；默认的 macOS 27 SDK 在这台机器上编译不了 SwiftUI，所以加了 `scripts/swift-sdk.sh`。在源码里放回一处上游名字，`check.sh` 一秒内报错退出。
- **自己的位置**：启动 `build/EnCopy.app` 后，设置写在 `app.encopy.EnCopy`，文件夹建在 `~/Library/Application Support/EnCopy/`；没有出现 `app.tendedero.*` 的设置和 `Application Support/Tendedero`（`launch-smoke.log`）。日志子系统、队列名、剪贴板标记、服务端口名也都改了。这台机器没装 Tendedero，两个 App 同时运行没有实测；用户 2026-11-22 决定不测，只用 EnCopy。
- **地图**：词汇表的覆盖范围改成 `Sources/**`，登记了 README；`status` 无问题。
- **真机**（2026-11-22，macOS 27.2，两块屏）：用 `screencapture` 看屏幕，`cliclick` 点和拖，⌘⇧4 用 CGEvent 发。
  1. `open build/EnCopy.app`，这时 EnCopy 的设置和文件夹都是空的。出现「让 EnCopy 接管你的截图？」，图标是 EnCopy 的，带「登录时打开」勾选框，空绳出现在屏幕顶部（`walk-01-first-launch.png`、`walk-01a-alert-crop.png`）。这个提示框和后面的「把复制的内容也挂上」由用户点了「开启」，第二个没有截到图。
  2. 点菜单栏的 EnCopy 图标，菜单是简体中文，「接管截图」「图片」「文字」「链接」「登录时打开」都勾着（`walk-03-menu.png`）。点「文字」，`clipboardTextEnabled` 变 0，菜单里的勾消失（`walk-04-menu-text-off.png`）；再点一次变回 1，勾回来（`walk-05-menu-text-on.png`）。
  3. ⌘⇧4 框一块屏幕。文件落在 `~/Library/Application Support/EnCopy/Screenshots/`，绳子放下来，截图夹在绳上（`walk-06-screenshot-hung.png`）。
  4. 复制两行文字，再复制一个网址。绳上先后多出一张便签和一张链接卡，菜单栏图标的说明从「晾衣绳上有 1 张」变到「3 张」，两段文字存进 `…/EnCopy/Notes/`（`walk-07-text-hung.png`、`walk-08-link-hung.png`）。指针停在菜单栏中段，三件一起挂在绳上（`walk-09-hover-all-three.png`）。
  5. 菜单里选「全部取下」，绳空了，两个文件夹也空了（`walk-12-after-cleanup.png`）。全程没有崩溃报告。
  - 没分清的一点：合成的点击有几次没反应，菜单栏图标和菜单项都出现过，最后一次「全部取下」改用辅助功能的 AXPress 才按下去。是合成点击的时序问题还是 App 的问题没有分清；T-012 改「叫它来」时用手点菜单栏图标确认。
- **Xcode**：外置硬盘上的 Xcode 27.2 能用。`DEVELOPER_DIR=/Volumes/WD-Elements/Applications/Xcode.app/Contents/Developer scripts/check.sh` 用 macOS 27 的 SDK 直接编译，不走 `scripts/swift-sdk.sh` 的替换，同样是 `80 checks passed`（`check-xcode27.log`）。
