---
id: T-027
title: 按 App 记录：不记、一小时后清、正常
type: build
status: closed
blocked_by: [T-026]
claimed_by:
supersedes:
---

## What to build

设计方案的 App 三档规则（用户说的「App 黑白名单」）：每个 App 可以设成正常记录、一小时后清、不记。「密码」和「钥匙串访问」预设为不记。不记的 App 里复制的东西，文字和图片都不记；一小时后清的，从最后一次用到算起一小时后直接删掉（文件、历史和索引里都没有，不进废纸篓）。菜单栏「挂上复制的内容」下面多一项「按 App 记录」，前台 App 排第一个。

## Acceptance

- [x] 「密码」「钥匙串访问」预设为不记；在不记的 App 里复制，绳上、历史和索引里都查不到。
- [x] 一小时后清的 App 里复制的，纸上写「一小时后清」；不到一小时还在，过了一小时纸、文件和历史行都没了。
- [x] 把预设的 App 改成正常记录后，照常记下且不过期。
- [x] 自动检查通过，原有检查不改。

## Resolution

完成，2026-11-23。`ClipboardWatcher.AppRule`、`appRules`、`rule(for:)`，检查剪贴板时先看前台 App 的规则；`History` 加 `ttl` 列，`ttl(of:)`、`expired(at:)`；`Line.clearExpired(now:)`，App 启动时和每分钟清一次（绳子拿着键盘时不清）；菜单「按 App 记录」。`scripts/check.sh`：`96 strings`、`209 checks passed`，新增 7 项；不记规则失效的变异被抓到。

留下的事：图片还不进历史，一小时后清对复制的图片不起作用，等图片进篮子时一起做。真机走一遍和这一批一起走。
