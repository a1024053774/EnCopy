# X post for the EnCopy intro video

One post, no thread. Attach `EnCopy-intro.mp4` (39 s, 1920×1080, 30 fps, with music and sound effects).

279 / 280 by X's count: the link counts as 23, and ⌥ and ⏎ count as 2 each. Adding even one character goes over the limit.

```text
Your Mac's clipboard remembers one thing.

EnCopy hangs everything you copy on a clothesline at the top of your screen. ⌥C, pick one, ⏎, and it's pasted where you're typing. Type to search, even inside screenshots.

Free, open source, stays on your Mac.
https://a1024053774.github.io/EnCopy/
```

## Regenerate the video

The animation is `docs/intro.html`, the same file the product page plays live.

```sh
npm i --prefix /tmp/encopy-render puppeteer-core@23.11.1
cd promo
NODE_PATH=/tmp/encopy-render/node_modules node render.js video /tmp/encopy-render/silent.mp4
python3 soundtrack.py /tmp/encopy-render/silent.events.json /tmp/encopy-render/track.wav
ffmpeg -y -i /tmp/encopy-render/silent.mp4 -i /tmp/encopy-render/track.wav -c:v copy -c:a aac -b:a 192k -shortest EnCopy-intro.mp4
```

`INTRO_LANG=zh` before `node render.js` renders the Chinese version.
