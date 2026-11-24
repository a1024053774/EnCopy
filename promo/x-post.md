# X post for the EnCopy intro video

Attach `EnCopy-intro.mp4` (43 s, 1920×1080, 30 fps, with music and sound effects). The first two seconds already show ⌥C bringing the line down: X autoplays muted in the feed, and most people decide in that time whether to stop.

## The post

No links in the post itself: X shows posts with outside links to far fewer people. The links go in your own first reply, right after posting.

272 / 280 by X's count (⌥ and ⏎ count as 2 each).

```text
My Mac's clipboard only remembered the last thing I copied, so I built EnCopy.

Everything you copy hangs on a clothesline at the top of your screen. ⌥C, pick one, ⏎ pastes it.

It even searches the text inside screenshots. Free and open source, nothing leaves your Mac.
```

First reply, 100 / 280:

```text
Download (macOS 14+): https://github.com/a1024053774/EnCopy
Try ⌥C right in your browser: https://a1024053774.github.io/EnCopy/
```

## Getting it seen

An account with few followers gets few views whatever it posts; X mostly shows a post to people who already follow, then widens it if they engage in the first hour. What helps:

- Post when US and Europe are both awake: 21:00 to 23:00 Beijing time (9 to 11 am in New York).
- Reply under bigger accounts that post about Mac apps or developer tools, with something useful, not a link.
- Post where people look for Mac apps on purpose: r/macapps and r/MacOS on Reddit, Show HN on Hacker News ("Show HN: EnCopy – a clipboard history for Mac, hung on a clothesline"), Product Hunt, and in Chinese V2EX (分享创造) and 少数派.
- Pin the post to your profile.

## What the video shows

A cold open (⌥C, the line drops in), the problem (the clipboard keeps only the last copy), the name on pegs, then text, code, links, a screenshot and a PDF hanging as they are copied. ⌥C brings the line down, ← → picks, ⏎ pastes into Mail. Typing searches everything, including text in screenshots. ↓ opens the basket as a list; 25 files copied at once are one row, ⌘↓ opens it and ⏎ pastes just one; files are never copied, only their paths are kept. A screenshot is right-clicked and shared to a phone by AirDrop, then held over the code it belongs to, dragged and zoomed. Then the themes, privacy, and the end card with the product page address.

## Regenerate the video

The animation is `docs/intro.html`, the same file the product page plays live. `render.js` writes the sound events and the groove's timing next to the silent video, and `soundtrack.py` reads both.

```sh
npm i --prefix /tmp/encopy-render puppeteer-core@23.11.1
cd promo
NODE_PATH=/tmp/encopy-render/node_modules node render.js video /tmp/encopy-render/silent.mp4
python3 soundtrack.py /tmp/encopy-render/silent.events.json /tmp/encopy-render/track.wav
ffmpeg -y -i /tmp/encopy-render/silent.mp4 -i /tmp/encopy-render/track.wav -c:v copy -c:a aac -b:a 192k -shortest EnCopy-intro.mp4
```

`INTRO_LANG=zh` before `node render.js` renders the Chinese version. The Xiaohongshu cut (3:4, Chinese) and its cover are made from that: see `xhs/post.md`.
