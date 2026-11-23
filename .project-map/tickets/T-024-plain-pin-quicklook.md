---
id: T-024
title: ⌥C 后 ⇧⏎ 贴纯文本、⌘P 收藏、⌘Y 快速查看
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

绳子拿到键盘时多三个键，作用在选中的那件上：⇧⏎ 去掉样式只贴字；⌘P 收藏（钉到绳结左边），再按一次取消；⌘Y 用快速查看打开。这些键只在绳子拿到键盘时有效，符合 [T-002](T-002-only-option-c-global.md)。

## Acceptance

- [x] ⏎ 贴带样式的便签时保留样式；⇧⏎ 贴同一段，剪贴板里只有字。
- [x] ⌘P 把选中的那件钉到绳结左边，再按一次回到松的里面。
- [x] ⌘Y 交给快速查看；输入法正在组字时这些键都留给输入法，⌘A 之类留给搜索框。
- [x] 自动检查通过，原有检查不改。

## Resolution

完成，2026-11-23。`SearchBar.takes` 认 ⇧⏎、⌘P、⌘Y；`Paster.pastePicked(plain:)`、`Line.copy(_:plain:)` 去掉 RTF 只留字；`Line.togglePin`。`scripts/check.sh`：`187 checks passed`，新增 11 项；让 ⇧⏎ 照样带样式时检查报错。

留下的事：⌘Y 打开快速查看后，快速查看拿走了键盘，搜索框随之结束，绳子收起。真机走一遍和这一批键盘鼠标功能一起走。

真机（2026-11-23，开发版，虚拟屏 + 外接屏；证据在本地 `evidence/walk-v1/`，不进 git）：没走到：⇧⏎、⌘P、⌘Y。用户这段时间在用鼠标（8 秒里 170 次硬件移动），按键和指针会抢，停在这里；自动检查是这一项现有的证据。
