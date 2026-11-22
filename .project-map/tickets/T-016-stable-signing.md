---
id: T-016
title: 给本机构建一个固定的签名
type: task
status: open
blocked_by: []
claimed_by: devin
supersedes:
---

## Question

本机构建是 ad-hoc 签名。macOS 按签名认 App，所以每重新构建一次，EnCopy 的辅助功能授权就失效：系统设置里的开关还亮着，但 ⏎ 又退回「只复制」，要用户关掉再打开一次。EnCopy 现在是日常在用的，每合并一个功能都要重新装，也就每次都要重新授权。

要做的：在登录钥匙串里放一张自签名的代码签名证书，构建时用它签（`scripts/build-app.sh` 已经认 `SIGN_IDENTITY`）。这样重新构建后签名不变，授权一直有效。

需要用户：建证书会改钥匙串，第一次用它签名时系统会弹框要登录密码。

## Resolution
