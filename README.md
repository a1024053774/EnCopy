# EnCopy

EnCopy 是 macOS 上的剪贴板 App，正在做成这样：复制的东西和截图挂在屏幕顶部的一根绳上，按 ⌥C 把绳子叫下来，用键盘挑一件，贴回正在打字的地方。

它基于 Alejandro Buján 的 [Tendedero](https://github.com/alejandrobujan/tendedero) 修改而来（上游 `3c5b575`，2.0.0，MIT 许可）。EnCopy 是独立的项目，有自己的名字和图标，与 Tendedero 及其作者没有关联，也没有得到其认可。

## 现在到哪了

v1 还在做。当前版本换好了名字、图标和 bundle id（`app.encopy.EnCopy`），界面只留简体中文和英文。按 ⌥C 绳子落下来并拿到键盘，← → 挑一件，⏎ 贴回前台 App；直接贴要「辅助功能」权限，没有的话 ⏎ 只复制。⌥C 是唯一的全局快捷键。篮子、过期和清理按[规格](.project-map/specs/encopy-v1.md)逐步加上；进度看[项目地图](.project-map/MAP.md)，设计细节看[设计方案](design/encopy-design.html)。

## 构建

需要 Swift 工具链，不需要 Xcode。支持 macOS 14 及以上。

```sh
scripts/build-app.sh
open build/EnCopy.app
```

要日常用，装到「应用程序」。脚本会先构建，退出正在运行的 EnCopy，再换上新的并打开：

```sh
scripts/install.sh
```

只装了 macOS 27 的 Command Line Tools 时，它带的 SDK 编译不了 SwiftUI，脚本会自动改用旁边的 macOS 26 SDK。

本地构建是 ad-hoc 签名，每次重新构建后，macOS 会重新询问权限。

## 检查

```sh
scripts/check.sh
```

依次检查三件事：代码和脚本里没有上游的名字；每句界面文字在简体中文和英文里都有；再用真实的模型和视图代码跑一遍本地检查。本地检查不依赖 XCTest，用私有剪贴板和临时文件夹，不碰系统剪贴板，也不看你的截图。

## 许可

MIT，见 [LICENSE](LICENSE)，上游的版权声明原样保留。LICENSE 末尾的说明同样有效：Tendedero 的名字和图标不在许可范围内。
