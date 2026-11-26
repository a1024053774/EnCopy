---
id: T-073
title: 纸以外的主题里，访达复制的文件卡片写文件名，不写「Copied 时间戳.files」
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

在纸以外的主题（像素、拟物、极简、霓虹、手账、圣诞季）里，从访达复制一个文件，绳上的卡片写的是 EnCopy 自己那份清单的名字「Copied 1795659438790.files」，底下写「Files」，图标也是清单的。纸主题写的是文件名和它所在的文件夹。2026-11-25 拍圣诞主题图时发现，从 T-048 起就是这样。

卡片应当和纸主题一样：一个文件时写文件名、底下写它所在的文件夹，图标是这个文件的；一次复制好几个时写「N 个文件」、底下写前三个名字；文件挪走了的，用清单里记的名字。

## Acceptance

- [x] 本地检查：一份只有一个文件的清单，卡片写那个文件的名字和文件夹，图标用那个文件；三个文件的清单写「3 files」和前几个名字；不是清单的文件照旧写自己的名字。检查在旧写法上失败。
- [x] 开发版里：像素或圣诞季主题下，从访达复制一个文件和一次复制三个文件，卡片分别写文件名和「3 个文件」。留截图。
- [x] 本地检查通过。

## Resolution

2026-11-26。原因：纸主题的文件卡（`Line.swift` 的 `FileCard`）认得访达复制留下的文件清单，其余主题的 `ThemeFileCard` 不认得，直接写清单自己的文件名和所在的 `Files` 文件夹。改成两种卡都从 `FileList.card(_:)` 拿标题、底下那行和图标。

- 本地检查新加 3 条（一个文件：名字、文件夹、图标是那个文件；三个文件：「3 files」和三个名字；不是清单的文件写自己的名字）。`ENCOPY_CHECKS_SCREEN="EnCopy walk screen" scripts/check.sh`：477 checks passed。让 `FileList.card` 照旧写法返回清单名字的版本，在「The card shows the file's name, its folder and its icon, not the list's」上失败。
- 开发版（虚拟屏）：先复制一个 PDF、再一次复制三个文件。圣诞季下两张卡写「Budget plan 2027.pdf / Quarterly Docs」和「3 个文件 / Budget plan 2027.pdf, Minutes…」，图标是 PDF（`evidence/T-073/01-圣诞季-文件卡.png`）；像素主题一样（`02-像素-文件卡.png`）。

