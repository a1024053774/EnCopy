---
id: T-018
title: 开发版有自己的名字、设置和数据
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

`build/` 里的开发版现在和 `/Applications` 里日常用的那份是同一个 App：同一个 bundle id、同一份设置、同一个数据文件夹，真机验收时复制的测试内容会进用户的真实历史。按 [T-017](T-017-dev-copy-own-data.md)，开发版改成一个单独的 App：名字是 EnCopy Dev，bundle id 是 `app.encopy.EnCopy.dev`，设置、数据文件夹和系统权限都记在它自己名下，验收完一条命令删干净。日常版的名字、bundle id 和数据位置不变。

## Acceptance

- [x] `scripts/build-app.sh` 构建出的是开发版 `build/EnCopy Dev.app`，bundle id 是 `app.encopy.EnCopy.dev`。`scripts/install.sh` 和 `scripts/make-dmg.sh` 构建的是日常版，名字和 bundle id 跟现在一样。
- [x] 数据文件夹只在一处决定，名字跟着 App 的名字走：日常版还是 `~/Library/Application Support/EnCopy/`，开发版是 `~/Library/Application Support/EnCopy Dev/`。自动检查覆盖：笔记存在这个文件夹里；别处不再自己取 Application Support 目录。
- [x] `scripts/clean-dev.sh` 退出开发版，删掉它的设置、数据文件夹和权限记录，不碰日常版的。
- [x] 真机：退出日常版，打开开发版，打开截图接管和文字记录，复制一段文字。这段文字存在开发版的数据文件夹里，系统截图位置指向开发版的文件夹；日常版的设置和数据文件夹跟之前一样。退出开发版后，系统截图设置回到原样。跑 `scripts/clean-dev.sh` 后，开发版的设置和数据都没了。重新打开日常版，绳上还是原来的东西。
- [x] 本地检查通过，原有检查不改。

## Resolution

完成，2026-11-22。实现提交 `3f1f42c`，分支 `chore/T-018-dev-copy-identity`。证据在本地的 `evidence/T-018/`（不进 git）。

- **两个版本**：`scripts/build-app.sh` 按环境变量 `EDITION` 构建，默认 `dev`。开发版是 `build/EnCopy Dev.app`，名字 EnCopy Dev，bundle id `app.encopy.EnCopy.dev`；`EDITION=daily` 是日常版，生成的 Info.plist 和 `/Applications/EnCopy.app` 里那份逐行相同；其他值报错退出，不构建（`build.log`）。两个版本的可执行文件是同一个，区别只在 Info.plist 里的名字和 bundle id。`scripts/install.sh` 和 `scripts/make-dmg.sh` 用 `EDITION=daily` 调它；`install.sh` 装完后删掉 `build/EnCopy.app`，不在 `build/` 里留一份带日常版 bundle id 的 App。这两个脚本本身没有重跑：重装会让日常版的辅助功能授权失效（[T-016](T-016-stable-signing.md)），留到下一个功能合并时跑。
- **数据文件夹**：新增 `Sources/EnCopy/DataFolder.swift`，文件夹名取 App 的 `CFBundleName`；笔记、截图、搜索索引三处都改成从它取。不从 App 里启动（没有名字）时直接报错退出，不会落到日常版的文件夹里。
- **自动检查**：`scripts/check.sh` 输出 `88 strings, checked in en, zh-Hans` 和 `110 checks passed`，原有 108 项没有改动（`check-final.log`）。新增第 3 步：只有 `DataFolder.swift` 可以提到 Application Support；本地检查新增 2 项：数据文件夹以 App 的名字命名、笔记在数据文件夹里。4 个变异都被抓到（`mutants.log`）。
- **`scripts/clean-dev.sh`**：开发版开着时跑，它退出开发版，系统截图设置回到用户自己的，随后开发版的设置、数据文件夹都没了，6 秒后再查也没有（`clean-dev-3.log`）；再打开开发版，系统重新问「桌面」文件夹的权限，说明权限记录也清了。开发版开着提示框时跑，它停下并提示，什么都没删（`clean-dev-dialog-open.log`）。
- **真机**：记下日常版的设置和数据文件夹，退出日常版，打开开发版。它的设置写在 `app.encopy.EnCopy.dev`，数据文件夹建在 `~/Library/Application Support/EnCopy Dev/`；打开截图接管后，系统截图位置指向 `EnCopy Dev/Screenshots`；复制的一段测试文字存进 `EnCopy Dev/Notes/` 并挂上开发版的绳（`walk-04-dev-text-hung.png`）。期间日常版的数据文件夹里没有任何变化。清掉开发版后重新打开日常版：绳上还是原来的 9 件，设置和之前逐项相同，「登录时打开」还勾着，系统截图位置回到日常版的文件夹（`daily-defaults-*.txt`、`daily-folder-*.txt`）。

真机上发现并改掉的一处：`clean-dev.sh` 第一版跑完后留下一个空的设置文件。`defaults delete` 清掉内容后文件还在，脚本现在等一秒再清，然后把这个文件删掉。

留下的事：

- 开发版的设置是空的时，每次打开都会走一遍首次启动的三个提示框，其中系统问「桌面」文件夹权限的那个验收时点了「不允许」。做法记在 [AGENTS.md](../../AGENTS.md) 的「真机走一遍」。
- 两个版本同时开没有拦：都会去注册 ⌥C、都会接管截图。现在靠「验收前先退出日常那份」这条约定。
- 没有过 design-integrity-review：这次没有新增兜底、重试、兼容分支或并行接口。
