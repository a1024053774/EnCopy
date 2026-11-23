# EnCopy

[简体中文](README.md) | English

EnCopy is a clipboard app for macOS, being built toward this: what you copy and your screenshots hang on a line at the top of the screen; press ⌥C to bring the line down, pick an item with the keyboard, and paste it back where you were typing. Product page: <https://a1024053774.github.io/EnCopy/>. You can try ⌥C right on the page.

It is a modified version of Alejandro Buján's [Tendedero](https://github.com/alejandrobujan/tendedero) (upstream `3c5b575`, 2.0.0, MIT license). EnCopy is an independent project with its own name and icon. It is not affiliated with or endorsed by Tendedero or its author.

## Download

The latest version is [0.1.0](https://github.com/a1024053774/EnCopy/releases/latest). Download [EnCopy.dmg](https://github.com/a1024053774/EnCopy/releases/latest/download/EnCopy.dmg) and drag EnCopy into Applications. Requires macOS 14 or later; runs on Apple silicon and Intel.

This version is not notarized by Apple, so macOS blocks it the first time you open it: go to System Settings → Privacy & Security and click Open Anyway. To paste directly with ⏎, turn on EnCopy in System Settings → Privacy & Security → Accessibility.

## Where it stands

v1 is still in progress. The current version has its new name, icon and bundle id (`app.encopy.EnCopy`), and the interface keeps only Simplified Chinese and English. Press ⌥C and the line drops onto the screen the pointer is on and takes the keyboard; ← → picks an item, and ⏎ pastes it back into the front app. Pasting directly needs the Accessibility permission; without it, ⏎ only copies. ⌥C is the only global shortcut.

Text, links and code you copy are recorded out of the box, and you can pause recording from the menu bar. When the line is full, the oldest text item falls into the basket instead of the Trash. After ⌥C, typing searches all the text on the line and in the basket, and ⏎ pastes the picked result. An item you paste or copy out goes back to the head of the line, so the next ⌥C picks it first; copying the same content again adds no new item, and the existing one goes back to the head as well. Everything is still there after you quit and reopen. Screenshots and copied images that EnCopy keeps also fall into the basket when the line is full, and the text read from them finds them.

Images in the basket, expiry and cleanup will be added step by step according to the [spec](.project-map/specs/encopy-v1.md). Progress is in the [project map](.project-map/MAP.md) and design details are in the [design proposal](design/encopy-design.html); all three are in Chinese.

## Build

Requires the Swift toolchain, not Xcode. Supports macOS 14 and later.

```sh
scripts/build-app.sh
open "build/EnCopy Dev.app"
```

This builds the development copy, EnCopy Dev. It is a separate app with the bundle id `app.encopy.EnCopy.dev`; its settings, data folder (`~/Library/Application Support/EnCopy Dev/`) and system permissions are all its own, so trying it never touches the everyday copy's history. Both claim ⌥C and both take over screenshots, so don't run them at the same time. When you are done, one command quits the development copy and deletes its settings and data folder:

```sh
scripts/clean-dev.sh
```

For everyday use, install it into Applications. The script builds the everyday copy (name EnCopy, bundle id `app.encopy.EnCopy`), quits the running EnCopy, then puts the new one in place and opens it:

```sh
scripts/install.sh
```

If only the macOS 27 Command Line Tools are installed, their SDK cannot compile SwiftUI, and the scripts automatically switch to the macOS 26 SDK installed next to it.

Local builds are ad-hoc signed by default. macOS ties permissions to the signature, so after every rebuild the Accessibility switch still looks on, but you have to turn it off and on again for it to take effect. To keep the permission across rebuilds, run this once:

```sh
scripts/make-signing-identity.sh
```

It puts a self-signed code-signing certificate in a separate keychain (`~/Library/Keychains/encopy-signing.keychain-db`), and from then on `scripts/build-app.sh` signs with it, so the signature stays the same across rebuilds. It does not touch the login keychain or the keychain search list, and asks for no password. The keychain's password is empty so that builds can unlock it on their own; as a result, any program running as you can sign with it, and it only affects the permissions the system grants EnCopy on this Mac.

## Checks

```sh
scripts/check.sh
```

Runs five checks in order: the upstream name does not appear in the code or scripts; the global shortcut is registered in one place only; the data folder is decided in one place only; every interface string exists in both Simplified Chinese and English; and the local checks run against the real model and view code. The local checks do not depend on XCTest. They use a private pasteboard and a temporary home folder, so they don't touch the system clipboard, don't read or write your EnCopy data, and don't look at your screenshots.

## Contributors

- [LuckyE](https://github.com/a1024053774): maintainer. Sets the direction, makes the decisions, ships the releases.
- Claude (an AI by Anthropic): pair programming on code and docs. Worked with the maintainer on ⌥C following the pointer to its screen, used items going back to the head of the line, the 0.1.0 release and the product page. Credited as `Co-Authored-By: Claude` in those commits.
- Devin (an AI by Cognition): pair programming on code and docs. Worked with the maintainer on the rename and new icon, ⌥C summoning the line and pasting back into the front app, text going into the basket, the separate development copy and stable signing. Credited as `Co-Authored-By: Devin` in those commits.
- Alejandro Buján and the [upstream contributors](https://github.com/alejandrobujan/tendedero/graphs/contributors) of [Tendedero](https://github.com/alejandrobujan/tendedero): EnCopy is built on their code.

GitHub's Contributors list counts commit authors only; co-authors are shown on each commit instead.

## License

MIT, see [LICENSE](LICENSE); the upstream copyright notice is kept as is. The note at the end of LICENSE applies too: the Tendedero name and icon are not covered by the license.
