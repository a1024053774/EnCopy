# EnCopy

[简体中文](README.md) | English

EnCopy is a clipboard app for macOS, being built toward this: what you copy and your screenshots hang on a line at the top of the screen; press ⌥C to bring the line down, pick an item with the keyboard, and paste it back where you were typing. Product page: <https://a1024053774.github.io/EnCopy/>. You can try ⌥C right on the page.

It is a modified version of Alejandro Buján's [Tendedero](https://github.com/alejandrobujan/tendedero) (upstream `3c5b575`, 2.0.0, MIT license). EnCopy is an independent project with its own name and icon. It is not affiliated with or endorsed by Tendedero or its author.

## Download

The latest version is [0.2.0](https://github.com/a1024053774/EnCopy/releases/latest). Download [EnCopy.dmg](https://github.com/a1024053774/EnCopy/releases/latest/download/EnCopy.dmg) and drag EnCopy into Applications. Requires macOS 14 or later; runs on Apple silicon and Intel.

This version is not notarized by Apple, so macOS blocks it the first time you open it: go to System Settings → Privacy & Security and click Open Anyway. To paste directly with ⏎, turn on EnCopy in System Settings → Privacy & Security → Accessibility.

## Where it stands

v1 is still in progress. Since 0.1.0 it has its new name, icon and bundle id (`app.encopy.EnCopy`), and the interface keeps only Simplified Chinese and English. Press ⌥C and the line drops onto the screen the pointer is on and takes the keyboard; ← → picks an item, and ⏎ pastes it back into the front app. Pasting directly needs the Accessibility permission; without it, ⏎ only copies. ⌥C is the only global shortcut.

Text, links and code you copy are recorded out of the box, and you can pause recording from the menu bar. When the line is full, the oldest text item falls into the basket instead of the Trash. After ⌥C, typing searches all the text on the line and in the basket, and ⏎ pastes the picked result. An item you paste or copy out goes back to the head of the line, so the next ⌥C picks it first; copying the same content again adds no new item, and the existing one goes back to the head as well. Everything is still there after you quit and reopen. Screenshots and copied images that EnCopy keeps also fall into the basket when the line is full, and the text read from them finds them.

New in 0.2.0:

- One kind of paper per kind of thing: text is a receipt, code is tractor-feed paper, a link is a kraft tag, an image is a polaroid, a file is a folder; wooden pegs and two-strand twine. Pinned items hang left of the knot, and the newest hangs first right of the knot, older ones further right.
- Six themes: Paper, Pixel, Skeuomorphic, Minimal, Neon and Journal. Papers, pegs and the rope change together; pick one in Settings → The line. Each card shows the icon of the app it was copied from, which you can turn off.
- When something new is copied, the line peeks out for 1.5 seconds; set it from 0.8 to 4 seconds in Settings → The line, or turn it off.
- After ⌥C: Tab shows one kind (text, links, images, files), ↓ walks the basket by day (today, yesterday, this week, earlier), ⇧⏎ pastes words only, ⌘Y opens Quick Look, ⌘P pins. Double-clicking a card pastes it.
- Files copied in Finder hang on the line too, and pictures and videos from Finder are found by their file names. Each app can be set to record as usual, clear after an hour, or not record; Passwords and Keychain Access are not recorded by default. Verification codes clear after 10 minutes, and one thrown away early is not left in the Trash. Copying the same picture again brings the earlier one back to the head instead of saving it twice.
- Retention: text is kept for a year; screenshot originals shrink to thumbnails after 30 days and their text stays searchable; when originals pass 1 GB in total, the oldest shrink first. Expired items go to Recently Cleared first and can be put back in batches for 7 days. Clean Up History… in the menu bar clears a batch by kind, age, app or text.
- Motion follows the design's table: papers swing by weight, ⌥C and sliding down past the top edge each have their own spring, and a breeze passes now and then; with Reduce motion on, only fades remain. A new icon, and a menu bar glyph of a rope with two pegs.

What is left is in the [project map](.project-map/MAP.md) and design details are in the [design proposal](design/encopy-design.html); both are in Chinese.

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

Runs seven checks in order: the upstream name does not appear in the code or scripts; the global shortcut is registered in one place only; the data folder is decided in one place only; new copies, screenshots and files in watched folders all go through one rule that decides whether they hang; every interface string exists in both Simplified Chinese and English; the whole app type-checks; and the local checks run against the real model and view code. The local checks do not depend on XCTest. They use a private pasteboard and a temporary home folder, so they don't touch the system clipboard, don't read or write your EnCopy data, and don't look at your screenshots.

## Contributors

- [LuckyE](https://github.com/a1024053774): maintainer. Sets the direction, makes the decisions, ships the releases.
- [Claude](https://github.com/claude) (an AI by Anthropic): pair programming on code and docs. Worked with the maintainer on ⌥C following the pointer to its screen, used items going back to the head of the line, the 0.1.0 and 0.2.0 releases and the product page, the batch of v1 features listed above, and the fixes from two rounds of review. Earlier commits credit it as `Co-Authored-By: Claude`; from 0.2.0 on, commits Claude writes have Claude as the author and the maintainer as co-author.
- Devin (an AI by Cognition): pair programming on code and docs. Worked with the maintainer on the rename and new icon, ⌥C summoning the line and pasting back into the front app, text going into the basket, the separate development copy and stable signing. Credited as `Co-Authored-By: Devin` in those commits.
- Alejandro Buján and the [upstream contributors](https://github.com/alejandrobujan/tendedero/graphs/contributors) of [Tendedero](https://github.com/alejandrobujan/tendedero): EnCopy is built on their code.

GitHub's Contributors list counts commit authors only; co-authors are shown on each commit instead. So Claude's earlier co-authored commits are not counted there, and the ones it authors from 0.2.0 on are.

## License

MIT, see [LICENSE](LICENSE); the upstream copyright notice is kept as is. The note at the end of LICENSE applies too: the Tendedero name and icon are not covered by the license.
