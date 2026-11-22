# EnCopy 协作约定

EnCopy 是 [Tendedero](https://github.com/alejandrobujan/tendedero) 的 fork。计划、决定和工单在 `.project-map/`，词汇以 `CONTEXT.md` 为准，设计细节在 `design/encopy-design.html`。

开工先跑项目地图的状态检查，再读 `.project-map/MAP.md`：

```sh
python3 ~/.agents/skills/project-map/scripts/project_map.py status --root .
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

本地检查在一个临时的 home 目录里跑，不读写真实的 `~/Library/Application Support/EnCopy`；绕过 `scripts/check.sh` 直接运行检查程序会报错退出。

构建和检查都不需要 Xcode。只有 macOS 27 的 Command Line Tools 时，`scripts/swift-sdk.sh` 会让两个脚本改用 macOS 26 的 SDK。

这台机器的 Xcode 27.2 在外置硬盘上。`/Applications/Xcode.app` 只是启动器，`xcode-select` 指的仍是 Command Line Tools。硬盘插着时可以让两个脚本用它，直接用 macOS 27 的 SDK 编译：

```sh
DEVELOPER_DIR=/Volumes/WD-Elements/Applications/Xcode.app/Contents/Developer scripts/check.sh
```

## Git

- 远端：`origin` 是 `a1024053774/EnCopy`，地址走 `ssh.github.com:443`（这台机器连不上 22 端口）；`upstream` 是原仓库 `alejandrobujan/tendedero`，只拉不推。
- 代码只通过 PR 进 `main`。一张工单一个分支，例如 `feat/T-012-summon-and-paste`；不挂工单的改动用 `fix/…`、`chore/…`、`docs/…`。
- 提交信息写成 `类型(工单号): 做了什么`，类型用 feat、fix、refactor、test、docs、chore，例如 `feat(T-012): ⌥C 叫它来并贴回前台 App`。一个提交只做一件事，正文写为什么。
- 一个功能的收尾顺序：本地检查通过 → 真机走一遍 → 提交 → push 分支 → 开 PR → 合并 → 在 `main` 上跑 `scripts/install.sh` → 删分支 → 清缓存。检查没过不提交；不用 `--no-verify`，不 force push `main`。
- 开 PR 一律带 `--repo a1024053774/EnCopy --base main`。这是 fork，`gh` 默认会把 PR 开到上游仓库。
- PR 用 merge commit 合并：`gh pr merge --merge --delete-branch`。分支上的提交号合并后不变，工单里记的提交号一直有效。
- 日常用的 EnCopy 在 `/Applications/EnCopy.app`，bundle id 是 `app.encopy.EnCopy`。`build/` 里的开发版是另一个 App：名字 EnCopy Dev，bundle id `app.encopy.EnCopy.dev`，设置、数据文件夹（`~/Library/Application Support/EnCopy Dev/`）和系统权限都记在它自己名下，真机验收写不进日常那份的历史（[T-017](.project-map/tickets/T-017-dev-copy-own-data.md)）。两份都要占 ⌥C、都会接管截图，不能同时开：真机验收前先退出日常那份（菜单栏图标 → 退出 EnCopy），验收完跑 `scripts/clean-dev.sh`，再打开日常那份。
- 真机验收的截图和录屏放 `evidence/<工单号>/`，这个目录不进 git：仓库是公开的，截图可能带到屏幕上别的内容。工单的 Resolution 写步骤、结果和文件名。

## 真机走一遍

用 `screencapture` 看屏幕，`cliclick` 动鼠标，再加 `evidence/tools/` 里几个小程序读辅助功能树、发按键（本地，不进 git）。走之前给 Devin 开「辅助功能」和「屏幕录制」。

- 合成按键只在按下时带修饰键，抬起不带。抬起也带的话，系统会以为修饰键一直按着，之后的点击和打字全变成快捷键。`evidence/tools/mods` 能查看，`mods clear` 能清掉。
- 点击要按住 120 毫秒左右（`cliclick dd:… w:120 du:…`）；没有按住时间的点击，有些界面不认。
- 按键发给的是前台 App。发之前先确认前台是谁；用户正在用电脑时不发按键，改用辅助功能直接操作目标 App，或者等一等。
- 辅助功能的授权认的是签名。这台机器的构建用 `scripts/make-signing-identity.sh` 建的固定签名（[T-016](.project-map/tickets/T-016-stable-signing.md)），重新构建、重装后授权还在。EnCopy 和 EnCopy Dev 在系统设置里是两条，各授权一次。没建这个签名的机器上是 ad-hoc 签名，每次重新构建后都要用户把开关关掉再打开。
- 开发版的设置是空的时，打开它会依次出现三个框：系统问能不能访问「桌面」文件夹（点「不允许」，验收用不到）、「让 EnCopy 接管你的截图？」、「也挂上你复制的内容？」。第二个框里的「登录时打开」默认勾着，先取消勾选再点按钮，开发版不进登录项。日常那份的登录项不受影响。
- 开发版开着提示框时退不掉，`scripts/clean-dev.sh` 会停下并提示；关掉提示框再跑。
- 在微信里只用文件传输助手，只贴不发。输入框里可能有用户的草稿：贴完用 ⌘Z 撤掉，不要全选删除。

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
