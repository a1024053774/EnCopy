# EnCopy

EnCopy 是 macOS 上的剪贴板 App，正在做成这样：复制的东西和截图挂在屏幕顶部的一根绳上，按 ⌥C 把绳子叫下来，用键盘挑一件，贴回正在打字的地方。

它基于 Alejandro Buján 的 [Tendedero](https://github.com/alejandrobujan/tendedero) 修改而来（上游 `3c5b575`，2.0.0，MIT 许可）。EnCopy 是独立的项目，有自己的名字和图标，与 Tendedero 及其作者没有关联，也没有得到其认可。

## 现在到哪了

v1 还在做。当前版本换好了名字、图标和 bundle id（`app.encopy.EnCopy`），界面只留简体中文和英文。按 ⌥C 绳子落下来并拿到键盘，← → 挑一件，⏎ 贴回前台 App；直接贴要「辅助功能」权限，没有的话 ⏎ 只复制。⌥C 是唯一的全局快捷键。

复制的文字、链接和代码装好就记，菜单栏里可以暂停。绳上挂满后，最老的一件文字掉进篮子，不再进废纸篓；⌥C 后直接打字，搜的是绳上和篮子里的全部文字，⏎ 贴出选中的那条。同样的内容再复制一遍不会多出一件，原来那件回到绳头。退出重开后都还在。图片现在还不进篮子，挂满后照旧进废纸篓。

图片进篮子、过期和清理按[规格](.project-map/specs/encopy-v1.md)逐步加上；进度看[项目地图](.project-map/MAP.md)，设计细节看[设计方案](design/encopy-design.html)。

## 构建

需要 Swift 工具链，不需要 Xcode。支持 macOS 14 及以上。

```sh
scripts/build-app.sh
open "build/EnCopy Dev.app"
```

这样构建出的是开发版 EnCopy Dev。它是另一个 App，bundle id 是 `app.encopy.EnCopy.dev`，设置、数据文件夹（`~/Library/Application Support/EnCopy Dev/`）和系统权限都记在它自己名下，试用时碰不到日常那份的历史。两份都要占 ⌥C、都会接管截图，不要同时开。试完后，一条命令退出开发版并删掉它的设置和数据文件夹：

```sh
scripts/clean-dev.sh
```

要日常用，装到「应用程序」。脚本会构建日常版（名字 EnCopy，bundle id `app.encopy.EnCopy`），退出正在运行的 EnCopy，再换上新的并打开：

```sh
scripts/install.sh
```

只装了 macOS 27 的 Command Line Tools 时，它带的 SDK 编译不了 SwiftUI，脚本会自动改用旁边的 macOS 26 SDK。

本地构建默认是 ad-hoc 签名。macOS 的权限跟着签名走，所以每次重新构建后，「辅助功能」的开关虽然还亮着，也要关掉再打开才生效。想让授权一直有效，先跑一次：

```sh
scripts/make-signing-identity.sh
```

它在一个单独的钥匙串（`~/Library/Keychains/encopy-signing.keychain-db`）里放一张自签名的代码签名证书，之后 `scripts/build-app.sh` 都用它签名，签名不随重新构建变化。它不动登录钥匙串和钥匙串搜索列表，也不要密码。这个钥匙串的密码是空的，构建才能自己解锁它；因此以你的身份运行的程序都能用它签名，而它只关系到这台 Mac 上系统授给 EnCopy 的权限。

## 检查

```sh
scripts/check.sh
```

依次检查五件事：代码和脚本里没有上游的名字；全局快捷键只在一处注册；数据文件夹只在一处决定；每句界面文字在简体中文和英文里都有；再用真实的模型和视图代码跑一遍本地检查。本地检查不依赖 XCTest，用私有剪贴板和一个临时的 home 目录，不碰系统剪贴板，不读写你的 EnCopy 数据，也不看你的截图。

## 许可

MIT，见 [LICENSE](LICENSE)，上游的版权声明原样保留。LICENSE 末尾的说明同样有效：Tendedero 的名字和图标不在许可范围内。
