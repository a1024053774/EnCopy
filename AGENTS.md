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
| 构建 App，产物是 `build/EnCopy.app` | `scripts/build-app.sh` |
| 构建并装到 `/Applications/EnCopy.app`，日常用的那份 | `scripts/install.sh` |
| 打磁盘映像 | `scripts/make-dmg.sh` |

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
- 日常用的 EnCopy 在 `/Applications/EnCopy.app`，和 `build/` 里的开发版是同一个 bundle id，共用设置和数据，不能同时开。真机验收前先退出日常那份（菜单栏图标 → 退出 EnCopy），验收完再打开。
- 真机验收的截图和录屏放 `evidence/<工单号>/`，这个目录不进 git：仓库是公开的，截图可能带到屏幕上别的内容。工单的 Resolution 写步骤、结果和文件名。

## 清缓存

每个功能合并后清一次，磁盘紧张时随时清：

```sh
rm -rf .build build
```

- `.build/` 是 SwiftPM 的编译缓存，`build/` 是构建出的 App 和磁盘映像，都能重新生成。
- 检查脚本和构建脚本用的临时目录在正常结束时自己删；中途打断留下的 `encopy-*` 临时目录一并删掉。
- 真机验收时放到桌面的测试截图、挂到绳上的测试件，验收完删掉。`/Applications/EnCopy.app` 是日常用的那份，不删。
- `evidence/` 是验收证据，不是缓存，不清。
