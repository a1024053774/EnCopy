---
id: T-017
title: 开发版要不要用单独的数据位置
type: decide
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## Question

`build/` 里的开发版和 `/Applications/EnCopy.app` 是同一个 bundle id，共用设置和数据。篮子落盘后，真机验收时复制的测试内容会写进用户的真实历史。开发版要不要用单独的数据位置？

## Resolution

单独放，开发完后删掉。来源：用户 2026-11-22 选择。落实在 [T-018](T-018-dev-copy-identity.md)。
