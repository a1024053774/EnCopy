---
id: T-020
title: 半秒内连着复制两次，两次都记下
type: build
status: open
blocked_by: []
claimed_by:
supersedes:
---

## What to build

EnCopy 每半秒看一次系统剪贴板。两次复制落在同一个半秒里时，它只看到后一次，前一次就没记上。[T-013](T-013-text-history-basket.md) 的真机验收里遇到过一次：复制的 30 段里有一段和别的程序写剪贴板撞在同一个半秒里，没记上。连着快速复制两段文字时也会这样。

## Acceptance

- [ ] 先量：隔 100、200、300 毫秒连着复制两段文字，各试 20 次，记下现在各丢几次。
- [ ] 改完后同样的做法，隔 200 毫秒以上的两段都记下。
- [ ] 改完后 EnCopy 空闲时的 CPU 占用和现在比没有看得出的增加，留下数字。
- [ ] 自动检查通过，原有检查不改。

## Resolution
