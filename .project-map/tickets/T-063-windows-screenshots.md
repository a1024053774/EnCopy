---
id: T-063
title: Windows 接管截图、监视文件夹与登录时打开
type: build
status: open
blocked_by: [T-062]
claimed_by: antigravity
supersedes:
---

## What to build

在 Windows 上实现无需修改系统注册表即可稳定工作的截图接管、自定义文件夹监视与开机自启能力。依据 [T-056](T-056-windows-platform-and-release.md)。

包含内容：

- **截图接管（双通道协同 + 去重）**：
  - 开关开启后（首启弹窗询问），同时监听两个来源：① 通过 `SHGetKnownFolderPath(FOLDERID_Screenshots)` 动态获取系统截图目录（兼容类似 `Pictures\游戏截图` 的文件夹重定向，不硬编码路径）和 `FOLDERID_Captures` 录屏目录，用 `FileSystemWatcher` 监测新图并做图片尾字节完整性检查（PNG `IEND`、JPEG `EOI` 等），防止截到半截文件；② 系统截图工具（`Win+Shift+S`）截完立刻进剪贴板的图片。
  - 来自系统截图目录或剪贴板的截图，默认**复制一份进 `EnCopy\Screenshots\`**，**原文件夹里的截图保留不动**；两边若在短时间内同时收到同一张截图，通过 `pictureHash` 自动合并去重为一件，绝不在绳上挂双份。EnCopy 过期或清理时只删自己 `Screenshots\` 里的副本。
  - 支持「每次截完自动放进剪贴板」与「存储到桌面」功能。
- **额外监视文件夹**：在设置里支持添加和移除最多 3 个自定义监视文件夹（例如第三方截图工具输出目录），新图片落盘完整后自动挂上绳（取下时不删原文件）。
- **登录时打开**：通过读写当前用户注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 实现（日常版键名 `EnCopy`，开发版键名 `EnCopyDev` 或开发版默认不写日常项，避免验收污染）。首次启动引导窗口里默认勾选「登录时打开」，可取消。

## Acceptance

- [x] 动态读取 `FOLDERID_Screenshots`（在截图目录被重定向的机器上也能正确找到目录），并通过尾部完整性校验确保刚写入的大图写完才上绳。
- [x] 开启「接管截图」后，触发 `Win+Shift+S` 或往系统截图目录写入新截图时，文件被复制进 `EnCopy\Screenshots\`、原文件保留在原处不动、绳上只挂出一件（无双份重复）；在 EnCopy 里取下或过期清理只删自己的副本，原文件还在。
- [x] 可在设置添加/移除最多 3 个自定义监视文件夹；取下来自自定义文件夹的图片不删除原文件。
- [x] 「登录时打开」开关正确读写当前用户 `Run` 注册表项且开发版不覆盖日常版配置。
- [x] `windows/scripts/check.ps1` 覆盖截图完整性校验、双通道哈希去重、原文件不删保证、监视文件夹上限与启动项配置逻辑并全绿。
- [ ] 真机走查：使用 `EnCopy Dev`（取消登录项勾选），在启用接管截图后用 `Win+Shift+S` 截屏验证自动上绳、去重、OCR 可搜与原文件完好，留截图与步骤在 `evidence/T-063/`。

## Resolution

- **阶段实现提交**：`e46d0c2`、`ae1342c`（分支 `feat/T-063-windows-screenshots`，基于 `feat/T-062-windows-ocr` 切出，待真机走查全绿后依次合并关单）。
- **测试与审查记录**：
  - 按 `evidence-first-testing` 先在 `ScreenshotsWatchedFoldersLoginAndWindowTraits` 建立契约红灯测试，再实现 `ScreenshotFolders`（`SHGetKnownFolderPath(FOLDERID_Screenshots / FOLDERID_Captures)` 动态解析系统重定向路径）、`ScreenshotWatcher`（PNG `IEND` 12 字节、JPEG `EOI`、GIF `0x3B`、WebP `RIFF` 与 BMP 长度完整性校验，半截写入文件自动延后复检直至完整才上绳）、`ScreenshotCoordinator` + `ClipboardWatcher` 双通道协同与像素级 `PictureHash`（BGRA32 规范像素 SHA-256，跨 WIC/WPF 不同 PNG 压缩流精准合并去重、原文件留在原处不动、取下或清理仅删 `EnCopy\Screenshots\` 私有副本）、设置「截图 → 同时监视文件夹」最多 3 个目录增删（取下绝不删外部原文件）以及 `LaunchAtLogin`（日常版 `EnCopy` 与开发版 `EnCopyDev` 注册表项严格隔离）。
  - 同批完成 `ForegroundAppTracker` 通用窗口特征重构：移除对 `Dock_64.exe` 等特定第三方进程名的硬编码排除，改为基于 Win32 窗口特征（`WS_EX_TOOLWINDOW`、`WS_EX_NOACTIVATE`、隐藏 Owner 且无 `WS_EX_APPWINDOW`、无框纯 `WS_POPUP` 条形浮层以及 DWM Cloak 状态）过滤所有不在任务栏显示的通用 Dock/浮层窗口，并用真实 `CreateWindowExW` 句柄覆盖 4 种通用 Dock 形态回归断言。
  - 针对首轮 Codex `gpt-6.1-terra` 审查指出的 6 项问题先补红灯断言再修复转绿（`ae1342c`）：① `ScreenshotCoordinator` 异步监测线程回到 STA UI 线程执行剪贴板写图与挂绳回调，消除 `CopyEachScreenshot=true` 时 MTA 调 WPF 剪贴板异常；② `History` 增加 `PRAGMA user_version = 2` 自动升级与 `LegacyBytePictureHash` 双哈希兼容查询，防止升级前旧图重复入库；③ 新增 `History.IngestPrivateImage` 在同一把数据库锁内原子完成像素查重、双通道窗口合并与副本写入，消除剪贴板与目录监测并发竞态；④ `ScreenshotWatcher` 改为仅在消费回调成功落盘后才将路径标为已知，短暂独占锁释放后自动重试不丢图；⑤ 启动时尚未创建的 `FOLDERID_Screenshots` / `FOLDERID_Captures` 由父目录监测在系统首次新建时自动挂载并接管首张截图；⑥ 新装首次引导弹窗未确认前暂不启动截图接管。
  - 设置「截图」页离屏渲染图存于 `evidence/T-063/00-offscreen-settings-screenshots.png`；`windows/scripts/check.ps1` 23/23 全绿通过。
- **验收辅助工具与重建方式**：
  - 工具源码位于 `evidence/tools/walk-src/`（已由 `.gitignore` 忽略），本批补充 `known-folder <screenshots|captures>`、`render-text-png` 与 `printscreen` 键支持；重建命令：`dotnet publish evidence/tools/walk-src/walk.csproj -c Release -r win-x64 --self-contained false -o evidence/tools`。
