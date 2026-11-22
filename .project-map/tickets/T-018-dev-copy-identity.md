---
id: T-018
title: 开发版有自己的名字、设置和数据
type: build
status: open
blocked_by: []
claimed_by: devin
supersedes:
---

## What to build

`build/` 里的开发版现在和 `/Applications` 里日常用的那份是同一个 App：同一个 bundle id、同一份设置、同一个数据文件夹，真机验收时复制的测试内容会进用户的真实历史。按 [T-017](T-017-dev-copy-own-data.md)，开发版改成一个单独的 App：名字是 EnCopy Dev，bundle id 是 `app.encopy.EnCopy.dev`，设置、数据文件夹和系统权限都记在它自己名下，验收完一条命令删干净。日常版的名字、bundle id 和数据位置不变。

## Acceptance

- [ ] `scripts/build-app.sh` 构建出的是开发版 `build/EnCopy Dev.app`，bundle id 是 `app.encopy.EnCopy.dev`。`scripts/install.sh` 和 `scripts/make-dmg.sh` 构建的是日常版，名字和 bundle id 跟现在一样。
- [ ] 数据文件夹只在一处决定，名字跟着 App 的名字走：日常版还是 `~/Library/Application Support/EnCopy/`，开发版是 `~/Library/Application Support/EnCopy Dev/`。自动检查覆盖：笔记存在这个文件夹里；别处不再自己取 Application Support 目录。
- [ ] `scripts/clean-dev.sh` 退出开发版，删掉它的设置、数据文件夹和权限记录，不碰日常版的。
- [ ] 真机：退出日常版，打开开发版，打开截图接管和文字记录，复制一段文字。这段文字存在开发版的数据文件夹里，系统截图位置指向开发版的文件夹；日常版的设置和数据文件夹跟之前一样。退出开发版后，系统截图设置回到原样。跑 `scripts/clean-dev.sh` 后，开发版的设置和数据都没了。重新打开日常版，绳上还是原来的东西。
- [ ] 本地检查通过，原有检查不改。

## Resolution
