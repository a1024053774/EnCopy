---
id: T-022
title: 刚授权辅助功能后的第一次 ⏎ 要多久
type: research
status: open
blocked_by: []
claimed_by:
supersedes:
---

## Question

[T-012](T-012-summon-and-paste.md) 的真机验收里，刚授权辅助功能后的第一次 ⏎，文字没有在 1.6 秒内出现，之后每次都在 0.3 秒内。[T-015](T-015-summon-moves-to-pointer-screen.md) 量了开发版打开后的第一次 ⏎：221 毫秒，第二次 116 毫秒；那时授权早就在（固定签名，[T-016](T-016-stable-signing.md)），不是「刚授权后」。

要量「刚授权后」，得请用户在系统设置里把 EnCopy Dev 的辅助功能关掉再打开，然后用 `evidence/tools/pastetime` 量第一次 ⏎。慢的话查原因；不慢就关单。固定签名之后授权很少重来，这一单不急。

## Resolution
