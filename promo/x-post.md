# X post for the EnCopy intro video

One post, no thread. Attach `EnCopy-intro.mp4` (40 s, 1920×1080, 30 fps, with music and sound effects).

279 / 280 by X's count: each link counts as 23, and ⌥ and ⏎ count as 2 each. Adding even two characters goes over the limit.

```text
Your Mac's clipboard holds one thing.

EnCopy hangs everything you copy on a line at the top of your screen. ⌥C, pick one, ⏎ pastes. Searches text in screenshots too. 25 files? One card, no copies.

Free, open source, local only.
https://a1024053774.github.io/EnCopy/
https://github.com/a1024053774/EnCopy
```

Chinese version, 253 / 280 (each Chinese character counts as 2):

```text
Mac 的剪贴板只记得最后一次。

EnCopy 把你复制的东西挂在屏幕顶上的一根绳上：⌥C 叫下来，挑一件，⏎ 贴回去。打字就搜，截图里的字也搜得到；一次复制 25 个文件只算一张，不多存一份。

免费开源，只在你的 Mac 上。
https://a1024053774.github.io/EnCopy/
https://github.com/a1024053774/EnCopy
```

## What the video shows

The problem (the clipboard keeps only the last copy), the name on pegs, then text, code, links, a screenshot and a PDF hanging as they are copied. ⌥C brings the line down, ← → picks, ⏎ pastes into Mail. Typing searches everything, including text in screenshots. ↓ opens the basket as a list; 25 files copied at once are one row, ⌘↓ opens it and ⏎ pastes just one; files are never copied, only their paths are kept. Then the six themes, privacy, and the end card with the product page address.

## Regenerate the video

The animation is `docs/intro.html`, the same file the product page plays live. `render.js` writes the sound events and the groove's timing next to the silent video, and `soundtrack.py` reads both.

```sh
npm i --prefix /tmp/encopy-render puppeteer-core@23.11.1
cd promo
NODE_PATH=/tmp/encopy-render/node_modules node render.js video /tmp/encopy-render/silent.mp4
python3 soundtrack.py /tmp/encopy-render/silent.events.json /tmp/encopy-render/track.wav
ffmpeg -y -i /tmp/encopy-render/silent.mp4 -i /tmp/encopy-render/track.wav -c:v copy -c:a aac -b:a 192k -shortest EnCopy-intro.mp4
```

`INTRO_LANG=zh` before `node render.js` renders the Chinese version.
