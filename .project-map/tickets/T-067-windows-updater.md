---
id: T-067
title: Windows 应用内更新与发布打包脚本
type: build
status: open
blocked_by: [T-066]
claimed_by:
supersedes:
---

## What to build

实现 Windows 版的自签名发布打包脚本与应用内检查更新闭环，同时从架构与代码层确保绝不污染 Mac 版的 `releases/latest/download/EnCopy.dmg` 自动更新通道。依据 [T-056](T-056-windows-platform-and-release.md)。

包含内容：

- **证书与打包脚本（`windows/scripts/`）**：
  - `make-signing-identity.ps1`：在本机用户专属目录 `%LOCALAPPDATA%\EnCopySigning\` 生成固定 10 年期自签名代码签名证书（若已存在则跳过）；
  - `make-package.ps1`：构建日常版 `EnCopy`、用上述固定证书做 Authenticode 签名，并打包输出带版本号的归档和 `windows/build/EnCopy-windows-x64.zip`（与 Mac 版的 `EnCopy.dmg` 资产名完全隔离）。
- **应用内更新器（Windows 版 `Updater`）**：
  - 启动后 10 秒及此后每 24 小时查询一次 GitHub Releases API（设置「通用」页可手动点「检查更新」，「自动更新」开关默认关闭）；开发版（`EnCopy Dev`）不检查更新。
  - **资产检索规则（保护 Mac 更新线）**：遍历 Releases 列表（支持包含 `prerelease`），跳过草稿，寻找**带有 `EnCopy-windows-x64.zip` 资产的最新非草稿 Release**（从而纯 Mac 版发布自动跳过，Windows 预发布版也能被正常检查到）。
  - 发现新版本时托盘图标右下出现小圆点、托盘菜单顶部出现「更新到 x.y.z 并重启…」，弹窗仅展示发布说明第一段「新东西」并请求确认。
  - 确认后下载 `EnCopy-windows-x64.zip` 解压到临时目录，校验目标主程序版本号更新且 Authenticode 签名证书公钥指纹与当前正在运行的日常版一致（任意一项不符、或当前进程未签名时一律拒绝安装，原程序纹丝不动并引导去发布页手动下载；安装目录写权限受限也优雅报错）；校验通过后拉起独立延时替换脚本，主进程正常退出释放 `.exe` 文件锁，替换完成并自动重新启动新版。

## Acceptance

- [ ] `make-signing-identity.ps1` 与 `make-package.ps1` 能产出经过固定自签名证书签名的 `EnCopy-windows-x64.zip`，内置许可证等必要文件；脚本绝不触碰 `docs/index.html` 或执行 GitHub 发布命令。
- [ ] 版本号比较、跳过草稿与不含 `EnCopy-windows-x64.zip` 的纯 Mac Release、识别包含该资产的 `prerelease` 与合并正式版逻辑全部正确；「自动更新」默认关闭；开发版不检查更新。
- [ ] 签名校验严格：未签名、他人的证书签名、签名后文件被篡改、或版本号不高于当前的更新包一律拒绝替换，原安装目录不受任何影响；第一段「新东西」提取逻辑与 Mac 版对齐。
- [ ] `windows/scripts/check.ps1` 覆盖版本比较、Release JSON 解析、签名/防篡改拦截、模拟 ZIP 解包与替换脚本行为并全绿。
- [ ] **本地模拟真机验收（严禁真建 GitHub Release）**：用本地 HTTP 服务模拟 GitHub Releases JSON 响应并挂接真实签名的低版本与高版本 `EnCopy-windows-x64.zip`，验证从托盘提示点更新 → 验签 → 退出替换 → 自动拉起新版且历史数据完好，留截图与日志在 `evidence/T-067/`。

## Resolution
