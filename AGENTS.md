# EnCopy 协作约定

EnCopy 是 [Tendedero](https://github.com/alejandrobujan/tendedero) 的 fork。计划、决定和工单在 `.project-map/`，词汇以 `CONTEXT.md` 为准，设计细节在 `design/encopy-design.html`。

开工先跑项目地图的状态检查，再读 `.project-map/MAP.md`：

```sh
python3 ~/.agents/skills/project-map/scripts/project_map.py status --root .
```

Windows 下：

```powershell
python "$env:USERPROFILE\.gemini\config\skills\project-map\scripts\project_map.py" status --root .
```

## 命令

| 做什么 | 命令 |
| --- | --- |
| 本地检查，每次改完都跑 | `scripts/check.sh` |
| 构建开发版，产物是 `build/EnCopy Dev.app` | `scripts/build-app.sh` |
| 构建日常版并装到 `/Applications/EnCopy.app` | `scripts/install.sh` |
| 退出开发版，删掉它的设置和数据文件夹 | `scripts/clean-dev.sh` |
| 给本机构建一个固定的签名，每台机器跑一次 | `scripts/make-signing-identity.sh` |
| 打磁盘映像 | `scripts/make-dmg.sh` |

本地检查会在屏幕上开一个窗口挂绳子。环境变量 `ENCOPY_CHECKS_SCREEN` 设成一块屏的名字，窗口就开在那块屏上。用户在用电脑时，检查和渲染都放到虚拟屏上做：用 `evidence/tools/vdisplay` 开一块虚拟屏，它叫 `EnCopy walk screen`，然后 `ENCOPY_CHECKS_SCREEN="EnCopy walk screen" scripts/check.sh`。

本地检查在一个临时的 home 目录里跑，不读写真实的 `~/Library/Application Support/EnCopy`；绕过 `scripts/check.sh` 直接运行检查程序会报错退出。

构建和检查都不需要 Xcode。只有 macOS 27 的 Command Line Tools 时，`scripts/swift-sdk.sh` 会让两个脚本改用 macOS 26 的 SDK。

这台机器的 Xcode 27.2 在外置硬盘上。`/Applications/Xcode.app` 只是启动器，`xcode-select` 指的仍是 Command Line Tools。硬盘插着时可以让两个脚本用它，直接用 macOS 27 的 SDK 编译：

```sh
DEVELOPER_DIR=/Volumes/WD-Elements/Applications/Xcode.app/Contents/Developer scripts/check.sh
```

Windows 版全部源码、测试、构建产物和脚本放在 `windows/` 下，Mac 版的 `Sources/`、`Package.swift` 和 `scripts/` 一律不动。

| 做什么（Windows） | 命令 |
| --- | --- |
| 本地检查，每次改完都跑 | `powershell -ExecutionPolicy Bypass -File windows/scripts/check.ps1` |
| 构建开发版，产物是 `windows/build/EnCopy Dev/` | `powershell -ExecutionPolicy Bypass -File windows/scripts/build-app.ps1` |
| 构建日常版并装到 `%LOCALAPPDATA%\Programs\EnCopy\` | `powershell -ExecutionPolicy Bypass -File windows/scripts/install.ps1` |
| 退出开发版，删掉它的设置和数据文件夹 | `powershell -ExecutionPolicy Bypass -File windows/scripts/clean-dev.ps1` |
| 给本机构建一个固定的自签名证书，每台机器跑一次 | `powershell -ExecutionPolicy Bypass -File windows/scripts/make-signing-identity.ps1` |
| 打发布包 `windows/build/EnCopy-windows-x64.zip` | `powershell -ExecutionPolicy Bypass -File windows/scripts/make-package.ps1` |

Windows 本地检查在临时目录与模拟剪贴板中离屏运行，不在屏幕弹窗闪烁，不读写 `%LOCALAPPDATA%\EnCopy` 和系统剪贴板；绕过 `windows/scripts/check.ps1` 直接跑检查程序会报错退出。构建和检查使用 `.NET 10 SDK`（`Microsoft.DotNet.SDK.10`）。

## Git

- 远端：`origin` 是 `a1024053774/EnCopy`，地址走 `ssh.github.com:443`（这台机器连不上 22 端口）；`upstream` 是原仓库 `alejandrobujan/tendedero`，只拉不推。
- 代码只通过 PR 进 `main`。一张工单一个分支，例如 `feat/T-012-summon-and-paste`；不挂工单的改动用 `fix/…`、`chore/…`、`docs/…`。
- 提交信息写成 `类型(工单号): 做了什么`，类型用 feat、fix、refactor、test、docs、chore，例如 `feat(T-012): ⌥C 叫它来并贴回前台 App`。一个提交只做一件事，正文写为什么。
- 一个功能的收尾顺序：本地检查通过 → 真机走一遍 → 提交 → push 分支 → 开 PR → 合并 → 在 `main` 上跑 `scripts/install.sh` → 删分支 → 清缓存。检查没过不提交；不用 `--no-verify`，不 force push `main`。
- 开 PR 一律带 `--repo a1024053774/EnCopy --base main`。这是 fork，`gh` 默认会把 PR 开到上游仓库。
- PR 用 merge commit 合并：`gh pr merge --merge --delete-branch`。分支上的提交号合并后不变，工单里记的提交号一直有效。
- 日常用的 EnCopy 在 `/Applications/EnCopy.app`，bundle id 是 `app.encopy.EnCopy`。`build/` 里的开发版是另一个 App：名字 EnCopy Dev，bundle id `app.encopy.EnCopy.dev`，设置、数据文件夹（`~/Library/Application Support/EnCopy Dev/`）和系统权限都记在它自己名下，真机验收写不进日常那份的历史（[T-017](.project-map/tickets/T-017-dev-copy-own-data.md)）。两份都要占 ⌥C、都会接管截图，不能同时开：真机验收前先退出日常那份（菜单栏图标 → 退出 EnCopy），验收完跑 `scripts/clean-dev.sh`，再打开日常那份。
- 真机验收的截图和录屏放 `evidence/<工单号>/`，这个目录不进 git：仓库是公开的，截图可能带到屏幕上别的内容。工单的 Resolution 写步骤、结果和文件名。
- Windows 版全部代码与脚本放在 `windows/`，一张工单一个分支（如 `feat/T-058-windows-tracer-bullet`），各自开 PR 合进 `main`（同样带 `--repo a1024053774/EnCopy --base main`，用 `gh pr merge --merge --delete-branch`）。提交用本机默认 git 身份，不加 `--author`。Windows 功能每张票按收尾顺序自己走完：本地检查通过 → 真机走一遍 → 提交 → push 分支 → 开 PR → 合并 → 在 `main` 上跑 `windows/scripts/install.ps1`（`T-059` 落地后）→ 删分支 → 清缓存，整批做完再汇报一次。
- Windows 日常用的 EnCopy 在 `%LOCALAPPDATA%\Programs\EnCopy\EnCopy.exe`，数据文件夹在 `%LOCALAPPDATA%\EnCopy\`。`windows/build/EnCopy Dev/` 里的开发版名字是 `EnCopy Dev`，单实例互斥名、注册表登录项和数据文件夹（`%LOCALAPPDATA%\EnCopy Dev\`）都记在自己名下，真机验收写不进日常那份的历史。两份都要占 `Alt+C`、都会接管截图，不能同时开：真机验收前先退出日常那份（托盘图标 → 退出 EnCopy），验收完跑 `windows/scripts/clean-dev.ps1`，再打开日常那份。

## 发布和产品页

- 发一个版本：先把 `scripts/build-app.sh` 里的 `VERSION` 改好，合并进 `main`；在 `main` 上跑 `scripts/make-dmg.sh`，再 `gh release create v<版本> build/EnCopy-<版本>.dmg build/EnCopy.dmg --repo a1024053774/EnCopy --target <main 的提交>`。不带版本号的 `EnCopy.dmg` 每次都要传，产品页和 README 的下载链接 `releases/latest/download/EnCopy.dmg` 靠它。
- 发版前在 `Sources/EnCopy/Updater.swift` 的 `WhatsNew.entries` 里给新版本写一条：用户更新后第一次打开会弹一个小窗说这些（[T-074](.project-map/tickets/T-074-whats-new.md)），有新主题时填 `theme` 就会配图。这一版没什么可说的就不写，不弹。
- 这台 Mac 没有 Developer ID，DMG 没有经过 Apple 公证；发布说明里要写第一次打开怎么点「仍要打开」。
- 从 0.4.1 之后的第一个发布起，EnCopy 会自己从最新发布的 `EnCopy.dmg` 更新（[T-053](.project-map/tickets/T-053-in-app-update.md)）。所以发布的 App 必须用这台机器的固定签名构建（`scripts/make-signing-identity.sh` 建的那个）：签名换了，所有人的更新都会被拒，只能去发布页手动下载。发布说明的第一段（第一个标题下、到下一个标题或 `---` 为止）就是更新确认框里显示的「新东西」，安装步骤写在它后面。
- 日常那份要装成发布出去的那一份时，从 DMG 里拷，不要重新构建：照 `scripts/install.sh` 的做法先退出正在运行的 EnCopy，再 `ditto` 进 `/Applications`。
- 产品页是 `docs/index.html`，一个文件，GitHub Pages 从 `main` 的 `docs/` 发布到 <https://a1024053774.github.io/EnCopy/>。页上写的功能、版本号、DMG 大小都要和最新发布的一致；App 的行为变了，合并前看一眼它是不是还对。
- Windows 安装包是 `windows/build/EnCopy-windows-x64.zip`，必须用 `windows/scripts/make-signing-identity.ps1` 建的本机固定自签名证书做 Authenticode 签名（保证 Windows 应用内更新验签通过）。**绝不能单独建一个非预发布的 Windows Release 占住 `releases/latest`**：Mac 版的自动更新和产品页都靠 `releases/latest/download/EnCopy.dmg`，单发 Windows 正式版会让所有 Mac 用户的更新失败。Windows 的包要么跟 Mac 版放进同一个 Release，要么发成 `prerelease`；怎么发、什么时候发都要先问用户，不要擅自建 GitHub Release，不要改 `docs/index.html`。
- 这台 Windows 机器没有商业代码签名证书，首次运行会被 SmartScreen 拦截；发布说明里要写第一次打开怎么点「更多信息 → 仍要运行」。

## 真机走一遍

用 `screencapture` 看屏幕，`cliclick` 动鼠标，再加 `evidence/tools/` 里几个小程序读辅助功能树、发按键（本地，不进 git）。走之前给 Devin 开「辅助功能」和「屏幕录制」。

- 合成按键只在按下时带修饰键，抬起不带。抬起也带的话，系统会以为修饰键一直按着，之后的点击和打字全变成快捷键。`evidence/tools/mods` 能查看，`mods clear` 能清掉。
- 点击用 `evidence/tools/click <x> <y>`：按住 120 毫秒，按下和抬起的点击数都是 1。没有按住时间的点击，有些界面不认；`cliclick dd:… du:…` 抬起时点击数是 0，绳上的卡片不把它当成点击，不会复制。`cliclick` 只用来移动指针。
- 往剪贴板写中文用 `LANG=en_US.UTF-8 pbcopy`。这里的 shell 没设 UTF-8，`pbcopy` 会写进空字符串。
- 内建屏的菜单栏图标太多时，EnCopy 的图标被刘海挡住，辅助功能也找不到它的菜单。接了外接屏时，在外接屏的菜单栏里点得到。
- 两块屏的验收优先用真的外接屏。没有外接屏时，`evidence/tools/vdisplay` 能临时建一块虚拟屏，结束进程它就没了；但它一出来就成了主屏、内建屏去镜像它，要马上跑 `evidence/tools/unmirror <它的编号>` 把镜像关掉，摆到内建屏右边。建之前先问用户。
- 验收期间日常那份是退出的，用户这段时间复制的东西只记进开发版，`scripts/clean-dev.sh` 会一起删掉。验收拖得久时，删之前告诉用户。
- 用 Python 脚本调 `scripts/check.sh`（比如跑改坏的版本）时，从环境里去掉 `SDKROOT`：`/usr/bin/python3` 的外壳会设上它，`scripts/swift-sdk.sh` 就不换 SDK，检查编译失败，看起来像检查报错。
- 按键发给的是前台 App。发之前先确认前台是谁；用户正在用电脑时不发按键，改用辅助功能直接操作目标 App，或者等一等。
- 真机验收要独占系统剪贴板和前台。开始前先问用户，这台 Mac 上有没有别的自动化在跑（比如另一个 computer use 会话）：它粘贴时也写系统剪贴板，两边的内容会串到对方那里；它看到提示框挡在前面，也会去点。有的话等它停了再走。
- 用辅助功能按标题找按钮来点时，同一条命令里先确认提示框还在。提示框已经关掉的话，按标题会找到菜单里同名的那一项：「登录时打开」在提示框里是勾选框，在菜单里是开关。
- 辅助功能的授权认的是签名。这台机器的构建用 `scripts/make-signing-identity.sh` 建的固定签名（[T-016](.project-map/tickets/T-016-stable-signing.md)），重新构建、重装后授权还在。EnCopy 和 EnCopy Dev 在系统设置里是两条，各授权一次。没建这个签名的机器上是 ad-hoc 签名，每次重新构建后都要用户把开关关掉再打开。
- 开发版的设置是空的时，打开它会依次出现两个框：「让 EnCopy 接管你的截图？」和「EnCopy 会记下你复制的文字」。第一个框里的「登录时打开」默认勾着，先取消勾选再点按钮，开发版不进登录项。日常那份的登录项不受影响。系统第一次还会问开发版能不能访问「桌面」文件夹，点「不允许」（验收用不到），之后不再问。
- 开发版开着提示框时退不掉，`scripts/clean-dev.sh` 会停下并提示；关掉提示框再跑。
- 在微信里只用文件传输助手，只贴不发。输入框里可能有用户的草稿：贴完用 ⌘Z 撤掉，不要全选删除。
- Windows 真机验收用的辅助小程序和脚本（屏幕截图、`UIAutomation` 读控件树、`SendInput` 发按键和点击、修饰键查看与清零）跟 Mac 一样放在 `evidence/tools/` 下（`evidence/` 已被 git 忽略，公开仓库不进验收工具）；每张工单的 `Resolution` 里写清用了哪些工具、怎么重建。
- 合成按键只在按下时带修饰键，抬起一律清零修饰键，发完检查修饰键状态防止卡键；合成鼠标点击保持 120 毫秒按住时间。
- 真机验收第一次开始前问用户一次有没有别的自动化在跑（之后不用再问），每次开始前确认前台窗口和输入空闲，不抢正在使用的鼠标键盘、不串改系统剪贴板。双屏验收优先用真实外接屏；只有单屏时说明单屏验收范围。
- 开发版首次打开出现引导框时，先取消勾选「登录时打开」，不让开发版进注册表启动项。在记事本、微信（只用文件传输助手，只贴不发，贴完用 `Ctrl+Z` 撤掉，不要全选删除）和 Windows Terminal 走查，截图存进 `evidence/<工单号>/`。

## 清缓存

每个功能合并后清一次，磁盘紧张时随时清：

```sh
rm -rf .build build
scripts/clean-dev.sh
```

- `.build/` 是 SwiftPM 的编译缓存，`build/` 是构建出的 App 和磁盘映像，都能重新生成。
- 检查脚本和构建脚本用的临时目录、检查程序的偏好设置文件，在正常结束时自己删；中途打断留下的 `encopy-*` 临时目录和 `~/Library/Preferences/local.encopy.checks.plist` 一并删掉。
- `scripts/clean-dev.sh` 删的是开发版的设置和数据文件夹，真机验收时挂到开发版绳上的测试件跟着一起没了。日常那份的设置和数据它不碰。系统给开发版的权限（辅助功能、要不要访问「桌面」文件夹）它也不碰，下次构建接着用；要一起清掉，跑 `tccutil reset All app.encopy.EnCopy.dev`。
- 真机验收时放到桌面的测试截图，验收完删掉。`/Applications/EnCopy.app` 是日常用的那份，不删。
- `evidence/` 是验收证据，不是缓存，不清。

Windows 下清缓存：

```powershell
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue windows/build
Get-ChildItem -Path windows -Include bin,obj -Recurse -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
powershell -ExecutionPolicy Bypass -File windows/scripts/clean-dev.ps1
```

- `windows/build/` 与 `bin/`、`obj/` 是编译缓存，都能重新生成；`clean-dev.ps1` 只清开发版的 `%LOCALAPPDATA%\EnCopy Dev\` 与开发版登录项，不碰日常版的 `%LOCALAPPDATA%\Programs\EnCopy\`、`%LOCALAPPDATA%\EnCopy\`、`%LOCALAPPDATA%\EnCopySigning\` 固定签名证书和 `evidence/`。
