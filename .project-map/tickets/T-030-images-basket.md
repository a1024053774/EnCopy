---
id: T-030
title: 截图和复制的图片进篮子
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

[T-004](T-004-retention-rules.md) 的第一步：EnCopy 自己存下的截图和复制的图片（在它的截图文件夹里）也进历史。绳上挂满后，最老的一张掉进篮子，不再进废纸篓；图里识别出的字进索引，⌥C 后打字能在篮子里搜到它，用到后回到绳头。取下照旧进废纸篓，⌘Z 放回。别处的图片（桌面、监视的文件夹）照旧只从绳上拿下。过期和缩小放在下一张单。

## Acceptance

- [x] 截图文件夹里的图片挂满后掉进篮子，文件和历史行都在，不进废纸篓。
- [x] 图里识别出的字能在篮子里搜到；从篮子里复制它，它回到绳头。
- [x] 取下进废纸篓、历史行没了；放回后文件和行都回来。
- [x] 别处的图片照旧只从绳上拿下，不进历史。
- [x] 自动检查通过，原有检查不改。

## Resolution

完成，2026-11-23。`Line.isKeptImage`、`Line.keep`；`History.recordImage(_:text:)`（识别出的字到了再写进索引）；`SearchIndex.onRead` 把识别结果交给历史；挂满时文字和自己的图片都只从绳上落下。`scripts/check.sh`：`232 checks passed`，新增 7 项；挂满又进废纸篓的变异被抓到。README 两份跟着改。真机走一遍和这一批一起走。

真机（2026-11-23，开发版，虚拟屏 + 外接屏；证据在本地 `evidence/walk-v1/`，不进 git）：开发版里「图片」默认没勾（和上游一样）；从菜单勾上后复制一张 PNG，存进开发版的 Screenshots 文件夹，挂成拍立得，写着时间。掉进篮子、缩小没在真机上走。
