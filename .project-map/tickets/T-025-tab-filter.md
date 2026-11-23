---
id: T-025
title: ⌥C 后 Tab 按类型筛选
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

绳子拿到键盘时，Tab 按类型筛选绳上（和搜索时篮子里）的东西：全部 → 文字 → 链接 → 图片 → 全部，⇧Tab 反着走。搜索框里显示当前的类型；筛选和打字搜索叠加。收起后回到全部。文件的类型在文件能挂上绳以后加进来。

## Acceptance

- [x] Tab 依次只显示文字、链接、图片，再回到全部；每换一次选中最新的那件。
- [x] ⇧Tab 反着走；输入法组字时 Tab 留给输入法。
- [x] 筛选和搜索一起用时，只显示这一类里匹配的。
- [x] 自动检查通过，原有检查不改。

## Resolution

完成，2026-11-23。`Line.kindFilter`、`Line.kind(of:)`、`Line.cycleKind`；`SearchBar` 认 Tab 和 ⇧Tab，搜索框前面显示类型标签；收起时清掉筛选。`scripts/check.sh`：`196 checks passed`，新增 9 项；没打字时不管筛选的变异被抓到。真机走一遍和这一批一起走。
