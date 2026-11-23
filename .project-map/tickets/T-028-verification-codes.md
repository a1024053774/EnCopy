---
id: T-028
title: 验证码 10 分钟后清
type: build
status: closed
blocked_by: [T-027]
claimed_by:
supersedes:
---

## What to build

设计方案的保留规则：验证码留 10 分钟，到时直接删，不进「最近清掉」。验证码指 4 到 8 位纯数字，来自「信息」或「邮件」；或者一段不长的文字里有「验证码」「code」之类的字样和一个 4 到 8 位的数字。纸上写「10 分钟后清」。

## Acceptance

- [x] 信息、邮件里复制的 4 到 8 位数字，和写着验证码/code 的短文字，认作验证码；订单号、过短或过长的数字不算。
- [x] 验证码的纸写「10 分钟后清」；9 分钟时还在，11 分钟时绳上、磁盘和索引里都没有了。
- [x] 自动检查通过，原有检查不改。

## Resolution

完成，2026-11-23。`ClipboardWatcher.isCode(_:app:)`，认出的验证码记 600 秒的 `ttl`，沿用 T-027 的到期清理。`scripts/check.sh`：`97 strings`、`215 checks passed`，新增 6 项；不给验证码 10 分钟的变异被抓到。真机走一遍和这一批一起走。
