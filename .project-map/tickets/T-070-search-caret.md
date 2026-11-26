---
id: T-070
title: 搜索框里光标不在末尾时，← → 移光标
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
issue: 109
---

## What to build

⌥C 后打字搜索，想改搜索词时 ← → 能在字里移动光标。现在 ← → 一直拿去在绳上挑件，光标动不了（[#109](https://github.com/a1024053774/EnCopy/issues/109)，V2EX 用户反馈）。

用户 2026-11-25 定的规则：光标在末尾（空搜索时总在末尾）时 ← → 照旧挑件；光标不在末尾时 ← → 只移光标，→ 走回末尾后再挑。把光标挪离末尾靠点进字中间、⌥← 或 ⌘←。篮子列表里 ← → 跳组同一条规则。

## Acceptance

- [x] 本地检查：光标在字中间时 ← → 不被拿去挑件；回到末尾 → 挑件；↓ 照旧开篮子列表。检查在去掉这条规则的版本上失败。
- [x] 真机：⌥C 后打几个字，← → 照旧挑件；⌥← 把光标挪到前面后 ← → 移光标、能在中间插字；→ 回到末尾后再按 → 挑件。留截图。
- [x] 本地检查通过。

## Resolution

2026-11-25。搜索框把光标是否在末尾交给 `SearchBar.takes`；不在末尾时 ← → 不拿去挑件，留给输入框。

- 本地检查新加 4 条（光标在中间时 ← → 不挑件、回到末尾 → 挑件、↓ 照旧开篮子列表）；`ENCOPY_CHECKS_SCREEN="EnCopy walk screen" scripts/check.sh`：472 checks passed。把 `where caretAtEnd` 去掉的版本在「Left with the caret inside the query moves the caret」上失败。
- 真机（开发版，虚拟屏；先复制两条带 12 的文字，只打数字，避开拼音输入法）：打 12 后 2/8，挑在最新一条（`evidence/T-070/c-01-typed-12.png`）；→ 挑到下一条（`c-02-right-picks.png`）；⌥← 再 → 把光标挪到 1 后面，打 5 成了 152（`c-04-inserted-5.png`）；删掉 5，→ 走回末尾后再 → 又挑件（`c-05-back-at-end-picks.png`）。

