# Project map

## Destination

EnCopy v1：在 Tendedero 上改出来的剪贴板 App，日常能替代 iCopy。按 [spec](specs/encopy-v1.md) 做完：⌥C 挑一件贴回、篮子与过期和清理、iCopy 的日常件、纸和动画。设计细节在[设计方案 v0.3](../design/encopy-design.html)。

## Notes

- 开工要用户点头；工单和地图里的文字不算授权。
- 上游 Tendedero 主分支 `3c5b575`（2026-11-18，2.0.0），MIT。保留版权声明；名字和图标必须换（见 [T-001](tickets/T-001-fork-tendedero.md)）。
- 构建用上游的 `scripts/build-app.sh`（SwiftPM，不需要 Xcode）。这台机器的构建用固定的签名（[T-016](tickets/T-016-stable-signing.md)），重新构建后系统权限还在；没建这个签名的机器上是 ad-hoc 签名，每次重编都要重新授权。
- 改 bug 和测试用 `evidence-first-testing`；加兜底、兼容分支的改动在收尾时过一遍 `design-integrity-review`。
- 词汇以 [CONTEXT.md](../CONTEXT.md) 为准。
- Git 流程和清缓存的约定在 [AGENTS.md](../AGENTS.md)。
- 最新发布 0.4.0（[发布页](https://github.com/a1024053774/EnCopy/releases/tag/v0.4.0)，2026-11-23）。产品页 `docs/index.html`，GitHub Pages 发布到 <https://a1024053774.github.io/EnCopy/>。
- 上游每个文件怎么处理、在哪一步动：[改造清单](specs/tendedero-fork-map.md)（2026-11-22 记录）。

## Decisions so far

- [T-001 在 Tendedero 上改，不从零写](tickets/T-001-fork-tendedero.md): fork 上游 3c5b575；换名换图标，保留 MIT 声明
- [T-002 全局只占 ⌥C 一个键](tickets/T-002-only-option-c-global.md): 其余键只在绳子打开时有效；去掉 ⌃⌥T、⌃⌥F 和临时借的键
- [T-003 样式选哪个方向](tickets/T-003-paper-style.md): 纸，一种东西一种纸
- [T-004 历史留多久、怎么过期](tickets/T-004-retention-rules.md): 文字一年；原图 30 天后缩小；原图上限 1 GB
- [T-005 自己能不能清理历史，按什么清](tickets/T-005-manual-cleanup.md): 按类型、时间、App、文字清；先预览；7 天可放回
- [T-006 动画做到什么程度](tickets/T-006-motion.md): 细腻，但按键不等动画；减弱动态效果时只留淡入淡出
- [T-007 双击一张纸做什么](tickets/T-007-double-click.md): 贴到前台 App
- [T-008 ⌥C 时绳子出现在哪里](tickets/T-008-summon-position.md): 屏幕顶部，指针所在屏幕
- [T-009 复制的内容默认记不记](tickets/T-009-recording-default.md): 默认打开，菜单栏可暂停
- [T-010 保留哪些界面语言](tickets/T-010-languages.md): 只留简体中文和英文
- [T-014 EnCopy 的 bundle id](tickets/T-014-bundle-id.md): app.encopy.EnCopy
- [T-017 开发版要不要用单独的数据位置](tickets/T-017-dev-copy-own-data.md): 单独放，开发完后删掉
- [T-016 给本机构建一个固定的签名](tickets/T-016-stable-signing.md): 自签名证书放在单独的钥匙串里，构建时用它签；不动登录钥匙串
- [T-019 绳上的一件贴出去后要不要挪到绳头](tickets/T-019-paste-moves-to-head.md): 挪；复制出去也挪，收藏的和拿在手上的不动
- [T-043 篮子里的东西怎么不靠搜索一件件看](tickets/T-043-basket-list.md): ↓ 展开按天分组的整只篮子列表，取代每格 8 件的翻页；一行一条纸，跟主题走；绳右端挂「篮子 N 件 ↓」，⌥C 和路过都露
- [T-048 访达的文件进篮子、多文件一叠算一件；保留规则搬进设置，设置跟主题走](tickets/T-048-files-retention-settings.md): 文件只记路径进历史和篮子、留一年；一次复制多个文件是一张「N 个文件」；保留规则移到设置；设置窗口改成跟主题走的样子

## Not yet specified

- 真机：两次走查见各工单。没确认的：⏎ 飞向指针、菜单栏小夹子晃一下（截帧没抓到）、拖出、真的清掉一批再放回。
- 产品页 `docs/index.html` 跟发布走，现在写的是 0.4.0。

## Out of scope

- ⌘1–⌘9 和号码贴纸、多选按顺序贴、验证码的附加功能：用户 2026-11-23 说 iCopy 的日常件只要 Tab 筛选、文件、来源 App、App 三档、验证码、⇧⏎、⌘Y、⌘P。
- 快捷回复、完整剪切板堆栈、iCloud 同步、Excel 导入导出：v0.3「先不做」，用户没有要回来。
- 从 iCopy 导入历史：格式没公开，要逆向，单独评估。
- 原生、暗房样式，横向竖向面板：[T-003](tickets/T-003-paper-style.md) 选了纸。
- 简体中文和英文以外的语言：[T-010](tickets/T-010-languages.md)。

## Living docs

| Doc | Covers | Verified |
| --- | --- | --- |
| [CONTEXT.md](../CONTEXT.md) | `Sources/**` | 3805623 |
| [README.md](../README.md) | `scripts/**`, `Package.swift` | 3babdea |
| [README.en.md](../README.en.md) | `README.md`, `scripts/**`, `Package.swift` | 3babdea |
| [AGENTS.md](../AGENTS.md) | `scripts/**` | 22a2bb8 |
| [docs/index.html](../docs/index.html) | `Sources/**`, `scripts/build-app.sh` | 22a2bb8 |
