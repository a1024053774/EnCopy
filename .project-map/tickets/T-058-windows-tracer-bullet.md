---
id: T-058
title: Windows 端到端第一条线：Alt+C 叫出列表、剪贴板记进 SQLite、⏎ 贴回前台窗口
type: build
status: open
blocked_by: []
claimed_by: antigravity
supersedes:
---

## What to build

最细的一条完整端到端路径，用于验证 Windows 版的三大核心风险：全局快捷键（`Alt+C`）、不抢前台窗口焦点却能接键盘输入、恢复焦点后对原前台窗口安全贴回。依据 [T-054](T-054-windows-stack-and-layout.md)、[T-055](T-055-windows-shortcuts-and-paste.md)。

用户侧流程：

- 后台托盘运行，系统剪贴板每次复制文本或链接（通过 `AddClipboardFormatListener` 事件监听，过滤隐私标记及自身写入，200ms 内连续两次复制都能记住），立即写入 SQLite 数据库与 FTS5 `trigram` 全文索引；
- 在任意前台应用里按 `Alt+C`，界面落在鼠标所在显示器的顶部并拿到键盘输入（同时记下原前台窗口）；此时先用简洁列表展示历史条目，支持直接打字全文搜索（含中文输入法组字时不截断方向键）、`↑`/`↓` 挑一件、`Esc` 或再按一次 `Alt+C` 收起；
- 按 `⏎`（或 `⇧⏎` 纯文本贴）：写入剪贴板、收起面板、恢复原前台窗口焦点，待修饰键释放后发送 `Ctrl+V` 贴回光标处（按下带 `Ctrl`、抬起清零修饰键，避免卡键）；目标若是管理员权限窗口（UIPI 拦截发送），只复制到剪贴板并弹出顶部提示「已复制，按 Ctrl+V 粘贴」。
- 同时落地 `windows/` 工程骨架、日常版与开发版数据隔离、「`Alt+C` 被占用」启动提示，以及基础 `windows/scripts/`（`check.ps1`、`build-app.ps1`、`clean-dev.ps1`；改键录制界面与 `install.ps1` 放到 `T-059`）。

## Acceptance

- [x] 全部分新代码与脚本位于 `windows/` 下（脚本在 `windows/scripts/`），Mac 版的 `Sources/`、`Package.swift`、`scripts/` 和 `docs/index.html` 零改动。
- [x] 全局只注册一处快捷键（默认 `Alt+C`）；启动时若 `Alt+C` 被占用会弹窗或气泡提示被占用；静态检查确认不存在第二处全局热键注册或键盘钩子。
- [x] 开发版（`EnCopy Dev`）与日常版（`EnCopy`）数据目录严格隔离在各自 `%LOCALAPPDATA%` 下；剪贴板文本/链接记录进 SQLite + FTS5 `trigram`（含短词 1–2 字与长词搜索），重启后还在，含隐私标记或自身写入不重复记录，200ms 内连续复制两次均入库。
- [x] 按 `Alt+C` 列表落在指针所在显示器顶部，原前台程序保持不变；打字全文搜索和中文输入法组字时方向键归输入法；`Esc` 或再按一次 `Alt+C` 收起后，方向键、`⏎`、`Ctrl+Z` 完全还给前台窗口。
- [x] 按 `⏎`（与 `⇧⏎` 纯文本）能把选中项贴回原前台窗口光标处，且贴完后系统无残留修饰键；面对管理员权限窗口时只复制并弹出顶部提示条。
- [x] 界面文字提供简体中文与英文两套资源，并在检查中校验无遗漏。
- [x] 运行 `windows/scripts/check.ps1` 全绿（静态门禁 + 隔离目录自动化检查，无弹窗闪烁、不碰真实数据与系统剪贴板）。
- [ ] 真机（使用 `EnCopy Dev`）：在记事本、微信（文件传输助手，只贴不发并 `Ctrl+Z` 撤回）和 Windows Terminal 里各走通「复制 → `Alt+C` → 挑选或搜索 → `⏎` 贴回」，主副屏各测一次，留截图与步骤在 `evidence/T-058/`。
- [x] 在 `.project-map/MAP.md` 登记 `windows/**` 对应的活文档覆盖范围，`project_map.py status` 无问题、无过期文档。

## Resolution

阶段完成（已修复首轮走查缺陷，待增量复验后合并）：

- 代码提交：`a53a3d7`、快速连按修复 `3952e94`（分支 `feat/T-058-windows-tracer-bullet`，尚未 push / 合并）。
- 自动化检查：`powershell -ExecutionPolicy Bypass -File windows/scripts/check.ps1` 6 项静态门禁 + 双版本 0 警告编译 + 10 项隔离测试全部通过（`[check] PASS`，含异步突发合并队列、瞬时空读不吞序号与纯 Win32 `HGLOBAL` 读取校验）。
- 失败-修复证据（`evidence-first-testing`）：首轮真机走查（`evidence/walk-report.md`）在 32–69ms 连续写入下 4/4 丢失首条（`History.sqlite` rows 43–46 仅存 `SECOND`）；`ClipboardAndBurstPersistence` 先在队列合并复现上获红灯，再将 `Win32ClipboardBackend` 改为独立后台 STA 监听线程 + 纯 Win32 `OpenClipboard`/`GlobalLock` 即时快照入队后转绿。
- 离屏视觉预览：`evidence/T-058/00-offscreen-panel-preview.png`（由 `EnCopy.Checks --render-preview` 生成）。
- 本地验收辅助工具：`evidence/tools/walk.exe`（位于 git 忽略目录，源码在 `evidence/tools/walk-src/`），重建命令：`dotnet publish evidence/tools/walk-src/walk.csproj -c Release -r win-x64 --self-contained false -o evidence/tools`。
- 已知状态：双屏与 100%/125% 混合缩放摆放及基础粘贴已由 Codex 走查通过（`evidence/walk-report.md`）；微信未安装、普通 Windows Terminal 受插件限制、管理员终端按规则跳过；快速连续复制 `3952e94` 待在真机增量复验后关票。
