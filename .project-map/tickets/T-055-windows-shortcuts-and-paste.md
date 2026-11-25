---
id: T-055
title: Windows 版的快捷键映射与贴回方式
type: decide
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## Question

Mac 版的 `⌥C` 和面板打开后的 `⌘` 快捷键在 Windows 上怎么对应？`Alt+C` 与别的程序冲突怎么办？贴回前台窗口怎么恢复焦点和处理管理员窗口？

## Resolution

来源：用户 2026-11-25 拍板。

- **全局快捷键**：沿用 [T-002](T-002-only-option-c-global.md) 全局只占一个键，默认 **`Alt+C`**（通过 Win32 `RegisterHotKey` 注册），保留设置页自定义改键与清除；启动时若发现 `Alt+C` 已被其他程序占用，弹出提示引导改键。`RegisterHotKey` 在系统层拦截组合键，不会把 `C` 传给前台程序，因此不会误触发传统程序里的 `Alt+C` 助记菜单。
- **面板快捷键映射**：绳子与篮子列表拿到键盘时，`⌘` 键统一对应为 `Ctrl`：
  - `Ctrl+P` 收藏、`Ctrl+Y` 快速查看、`Ctrl+↓` 展开文件一叠、`Ctrl+Backspace`（及 `Delete`）取下、`Ctrl+Z` 放回、`Ctrl+C` 复制、`Ctrl+Shift+Backspace` 打开清理历史；
  - `←` `→` `↑` `↓`、`⏎`（贴）、`⇧⏎`（纯文本贴）、`Tab`/`⇧Tab`（种类筛选）、`Esc` 与 Mac 版一致；
  - 收起面板后，上述按键全部归还当前前台窗口。
- **贴回前台窗口**：
  - 呼出瞬间记下原前台窗口句柄 `GetForegroundWindow()`；
  - 按 `⏎`、`⇧⏎` 或双击卡片时，先将选中内容写入剪贴板、收起面板并将焦点归还原前台窗口，等物理修饰键（如 `Alt`/`Shift`）释放后，通过 `SendInput` 发送 `Ctrl+V`（按键按下带 `Ctrl`，抬起清零修饰键，防止卡键）；
  - 普通窗口无需额外系统权限；若检测到目标窗口以管理员权限运行（UIPI 会拦截普通进程的 `SendInput`），则只复制到剪贴板，并在屏幕顶部弹出提示「已复制，按 Ctrl+V 粘贴」。
