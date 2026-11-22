# Tendedero 改造清单（2026-11-22，记录）

把 [spec](encopy-v1.md) 落到上游代码上：每个文件留、改还是删，在哪一步动。基于上游主分支 `3c5b575`（2.0.0），约 6,600 行 Swift。这是一份带日期的记录，不随代码更新；文件拆分、改名以实际提交为准。

步骤：①打通一条线（T-011、T-012）②篮子、过期和清理（T-013 起）③iCopy 的日常件 ④纸和动画。②的后半到④还没切成工单，见地图的 *Not yet specified*。

## 源码

| 文件 | 行数 | 处理 | 哪一步 | 要做的 |
| --- | ---: | --- | --- | --- |
| `AppDelegate.swift` | 1338 | 改 | ① ② ③ | 菜单里的名字和文案（T-011）；只注册 ⌥C，去掉搜索快捷键、指针停在绳上时临时注册的 ⌫/空格/⌘Z/⌘C/Esc，⌥C 走「叫它来」（T-012）；首次启动改成说明默认记录（T-013）；菜单加暂停记录、清理历史、设置（②③） |
| `HotKey.swift` | 215 | 改 | ① | 保留 Carbon 注册；默认快捷键改成 ⌥C；删掉搜索和放回两个快捷键（T-012） |
| `SearchBar.swift` | 106 | 改 | ① ② | 它的面板已经能「不抢前台就拿键盘」，扩成 ⌥C 面板：方向键、⏎、Esc（T-012）；搜索范围扩到篮子（T-013）；Tab、⌘1–9、⌘P、⌘⌫、⌘⇧⌫ 等（③） |
| `Line.swift` | 1050 | 改 | ① ② | 加选中状态和「贴」（T-012）；挂满时从扔废纸篓改成掉进篮子，存储从 UserDefaults 路径列表改成 SQLite，同内容合并（T-013）；收藏沿用 kept |
| `ClipboardWatcher.swift` | 221 | 改 | ② ③ | 文本、链接默认打开（T-013）；收文件（路径和书签）、记来源 App、App 三档规则、认验证码（③） |
| `SearchIndex.swift` | 116 | 改 | ② | JSON 索引换成 SQLite FTS5，覆盖篮子（T-013）；截图的图中文字进同一个索引（②后半） |
| `LineView.swift` | 258 | 改 | ④ | 最新的挨着绳结、号码牌、选中样子、下层按天的绳、麻绳；摆动交给物理循环 |
| `PeggedView.swift` | 270 | 重画 | ④ | 玻璃相框换成五种纸和木夹；swing 参数按纸的轻重（上游 stiffness 46 / damping 2.6 作拍立得那一档） |
| `LinePanel.swift` | 52 | 改 | ④ | 保留不拿键盘、点穿；下层绳出现时面板变高 |
| `GrabArea.swift` | 427 | 改 | ③ | 双击改成贴（T-007）；「在预览里打开」进右键菜单；其余手势不动 |
| `ScreenshotWatcher.swift` `Inbox.swift` `CaptureFlight.swift` | 642 | 改名 | ① ② | 文件夹和日志名改成 EnCopy（T-011）；截图进篮子、30 天后缩小（②后半） |
| `NoteSheet.swift` `Markdown.swift` | 705 | 保留 | ④ | 小票正文复用它的排版和 Markdown |
| `Markup.swift` `Trim.swift` `Held.swift` `Zoom.swift` `Share.swift` `QuickLook.swift` `FullScreen.swift` | 1147 | 保留 | ① | 只改名字和路径（T-011） |
| `main.swift` `Localization.swift` | 20 | 保留 | | |

## 新增

名字按 [CONTEXT.md](../../CONTEXT.md) 的英文名起。

| 新东西 | 哪一步 | 做什么 |
| --- | --- | --- |
| 贴入 | ① T-012 | 收起面板后对前台 App 发 ⌘V；检查辅助功能权限，没权限时只复制并引导授权 |
| 篮子（basket）存储 | ② T-013 | SQLite 历史表加 FTS5；每件记类型、来源、最后一次用到、状态（完整、只剩缩略图、最近清掉） |
| 保留规则（retention） | ② | 每天空闲时跑；先算出要动的清单，写进设置页的「下一次清理」，再执行 |
| 清理历史（cleanup） | ② | 清理面板和「最近清掉」；和保留规则共用「先算后做」 |
| 设置窗口 | ②③ | 快捷键、保留、App 规则、默认纯文本、音效 |
| 物理循环 | ④ | 麻绳下垂、按纸的轻重摆动、微风；静止就停；减弱动态效果时不跑 |

## 脚本和资源

| 文件 | 处理 | 哪一步 | 要做的 |
| --- | --- | --- | --- |
| `scripts/build-app.sh` | 改 | ① | 产物名、可执行文件名、bundle id `app.encopy.EnCopy`、权限说明文案、版本号 |
| `scripts/make-icon.swift` | 换 | ① | 上游图标不在许可里，换成 EnCopy 自己的图标 |
| `scripts/make-readme-art.swift` `docs/` | 删 | ① | 上游宣传图不在许可里 |
| `scripts/make-dmg.sh` `scripts/make-dmg-background.swift` | 改名 | ① | 跟着产物名改 |
| `scripts/check-strings.swift` | 改 | ① | 只校验 en 和 zh-Hans |
| `scripts/test-selection.sh` `scripts/test-selection.swift` | 改名、扩充 | ①起 | 本地检查（现有 80 项）；每个切片往里加检查 |
| `Sources/Tendedero/Resources/*.lproj` | 删 11 个 | ① | 只留 en、zh-Hans |
| 源码目录、模块名、SwiftPM 产品名 | 改名 | ① | Tendedero → EnCopy |
