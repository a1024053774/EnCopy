---
id: T-063
title: Windows 接管截图、监视文件夹与登录时打开
type: build
status: open
blocked_by: [T-062]
claimed_by:
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

- [ ] 动态读取 `FOLDERID_Screenshots`（在截图目录被重定向的机器上也能正确找到目录），并通过尾部完整性校验确保刚写入的大图写完才上绳。
- [ ] 开启「接管截图」后，触发 `Win+Shift+S` 或往系统截图目录写入新截图时，文件被复制进 `EnCopy\Screenshots\`、原文件保留在原处不动、绳上只挂出一件（无双份重复）；在 EnCopy 里取下或过期清理只删自己的副本，原文件还在。
- [ ] 可在设置添加/移除最多 3 个自定义监视文件夹；取下来自自定义文件夹的图片不删除原文件。
- [ ] 「登录时打开」开关正确读写当前用户 `Run` 注册表项且开发版不覆盖日常版配置。
- [ ] `windows/scripts/check.ps1` 覆盖截图完整性校验、双通道哈希去重、原文件不删保证、监视文件夹上限与启动项配置逻辑并全绿。
- [ ] 真机走查：使用 `EnCopy Dev`（取消登录项勾选），在启用接管截图后用 `Win+Shift+S` 截屏验证自动上绳、去重、OCR 可搜与原文件完好，留截图与步骤在 `evidence/T-063/`。

## Resolution
