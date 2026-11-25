---
id: T-056
title: Windows 版的截图、系统能力替代与发布红线
type: decide
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## Question

Windows 上截图接管、图中文字识别、快速查看、标记、录屏裁剪、登录时打开、签名和 GitHub 发布与 Mac 不同，分别怎么做？

## Resolution

来源：用户 2026-11-25 拍板。

- **接管截图**：不改系统截图目录注册表。开启「接管截图」后，来自系统截图目录（通过 `SHGetKnownFolderPath(FOLDERID_Screenshots)` 获取，兼容系统文件夹重定向）、自定义监视文件夹，或系统截图工具（`Win+Shift+S`）写入剪贴板的新截图，默认复制一份进 `EnCopy\Screenshots\` 并按图片哈希去重，原文件留在原处不动；EnCopy 过期或清理时只删自己文件夹里的那份。「移走原文件」以后再说。
- **图中文字识别**：使用系统自带离线 `Windows.Media.Ocr`，优先选用 `zh-Hans-CN` 引擎同时识别简体中文与英文；系统未安装中文 OCR 语言包时回退现有语言并在设置页提示安装，不静默丢弃文字识别能力。
- **系统能力对等实现（不砍功能）**：
  - 快速查看（`Ctrl+Y`）：内置轻量预览窗（支持图片、长文本/Markdown/代码、录屏、单/多文件，`←`/`→` 切换，关闭后焦点还回搜索框）；
  - 图片标记：内置轻量标注窗（画笔、方框、箭头、荧光笔、马赛克、文字、裁剪、撤销），仅对 EnCopy 自己目录内的图片按原格式写回并刷新缩略图与索引；
  - 录屏裁剪：内置首尾裁剪窗，调用 WinRT `Windows.Media.Editing.MediaComposition` 导出临时文件后原子替换原文件；
  - 右键共享：通过 `IDataTransferManagerInterop` 调起 Windows 原生系统共享面板；
  - 登录时打开：读写当前用户注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`（日常版与开发版互相隔离）。
- **代码签名与 GitHub 发布红线**：
  - 本机无商业代码签名证书，通过 `windows/scripts/make-signing-identity.ps1` 生成本机固定自签名证书做 Authenticode 签名，用于应用内更新验签闭环；发布说明里写明首次打开若遇 SmartScreen 拦截，点「更多信息 → 仍要运行」。
  - **绝不单独发布占住 `releases/latest` 的 Windows Release**：Mac 版自动更新固定拉取 `releases/latest/download/EnCopy.dmg`，单发正式版会导致 Mac 用户更新失败。Windows 安装包（`EnCopy-windows-x64.zip`）要么与 Mac 版放进同一个 Release，要么发成 `prerelease`；怎么发、什么时候发每次必须先问用户，绝不擅自建 Release 或修改 `docs/index.html`。
- **Git 流程约定**：提交使用本机默认 git 身份，不加 `--author`；每张票按收尾顺序自己走完（本地检查 → 真机走一遍 → 提交 → push → 开 PR → 合并 → install → 删分支 → 清缓存），整批做完再汇报一次；开 PR 一律带 `--repo a1024053774/EnCopy --base main`。
