---
id: T-016
title: 给本机构建一个固定的签名
type: task
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## Question

本机构建是 ad-hoc 签名。macOS 按签名认 App，所以每重新构建一次，EnCopy 的辅助功能授权就失效：系统设置里的开关还亮着，但 ⏎ 又退回「只复制」，要用户关掉再打开一次。EnCopy 现在是日常在用的，每合并一个功能都要重新装，也就每次都要重新授权。

要做的：在登录钥匙串里放一张自签名的代码签名证书，构建时用它签（`scripts/build-app.sh` 已经认 `SIGN_IDENTITY`）。这样重新构建后签名不变，授权一直有效。

需要用户：建证书会改钥匙串，第一次用它签名时系统会弹框要登录密码。

## Resolution

做了，2026-11-22。实现提交 `65fdb77`，分支 `chore/T-016-stable-signing`。证据在本地的 `evidence/T-016/`（不进 git）。

和上面写的做法有一处不同：用户选了「单独的钥匙串」，证书没有放进登录钥匙串。

- **怎么做的**：`scripts/make-signing-identity.sh` 建一个钥匙串 `~/Library/Keychains/encopy-signing.keychain-db`，里面放一张自签名的代码签名证书（名字 EnCopy Local Signing，10 年）。`scripts/build-app.sh` 发现这个钥匙串就解锁它并用这张证书签名；没有它时和以前一样是 ad-hoc 签名；有 Developer ID 时还是优先用 Developer ID。登录钥匙串和钥匙串搜索列表都没动，整个过程没有要密码（`make-identity.log`）。脚本跑第二遍只说已经有了，不重建。
- **签名不随构建变**：从 `main` 和从 T-013 分支各构建一份开发版，代码不同（CDHash 不同），系统用来认 App 的那条要求相同：`identifier "app.encopy.EnCopy.dev" and certificate leaf = H"8f9c…"`（`two-builds.txt`）。
- **真机**：打开第一份构建，⏎ 只复制并出现「去授权…」（`walk-02-build1-untrusted-hint.png`）；用户在系统设置里打开 EnCopy Dev 的开关后，⏎ 直接贴进备忘录（`walk-04-build1-pasted.png`）。退出，换上第二份构建，没有再授权，⏎ 直接贴进备忘录（`evidence/T-013/walk-07-pasted-in-notes.png`）。
- **`scripts/clean-dev.sh`** 不再清开发版的权限记录，否则每清一次数据又要重新授权。清完数据再打开开发版，系统没有再问「桌面」文件夹的权限。
- **自动检查**：`scripts/check.sh` 通过，110 项（`check.log`）；这次没有改源码和检查。

要知道的：

- 这个钥匙串的密码是空的，这样构建才能自己解锁它。因此以用户身份运行的程序都能用这张证书签名，签出来的东西如果用 EnCopy 的 bundle id，就能用上系统授给 EnCopy 的权限。把证书放登录钥匙串并对 codesign 点了「始终允许」，结果是一样的。
- 日常版（`/Applications/EnCopy.app`）要等下一次 `scripts/install.sh` 才换成新签名。换了之后用户要给 EnCopy 授权一次，之后重装不用再授权。
- 这台机器的登录钥匙串里还有一张 Apple Development 证书，这次没有用它：它一年到期，而且和 Apple ID 绑在一起。
- 撤回办法：`security delete-keychain ~/Library/Keychains/encopy-signing.keychain-db`，之后的构建回到 ad-hoc 签名。
