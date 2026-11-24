# 小红书笔记

视频笔记。视频用 `EnCopy-xhs.mp4`（3:4，1080×1440，约 43 秒，中文，带音乐）；封面用 `cover.png`。两个文件都从下面「重新生成」的命令做出来。

小红书正文里的外链点不开，还可能被限流，所以正文只写「GitHub 搜 EnCopy」，链接放到评论区或个人简介。

## 标题（20 字以内）

```text
Mac 剪贴板终于记得我复制过什么了
```

备选：

- `复制过的东西，全挂在一根绳上`
- `自己做的 Mac 剪贴板小工具，免费开源`

## 正文

```text
Mac 的剪贴板只记得最后一次，复制了新的，旧的就没了。于是我做了 EnCopy 👇

🧵 复制什么都挂到屏幕顶上的一根绳上：文字、代码、链接、截图、访达里的文件
⌨️ ⌥C 叫出来，← → 挑一件，⏎ 直接贴回正在打字的地方
🔍 打字就搜，连截图里的字都搜得到
🧺 绳挂满了掉进篮子，按 ↓ 展开成列表，按种类筛
📁 一次复制 25 个文件只算一张，文件只记路径，不多占一点空间
📤 截图右键就能隔空投送到手机、发信息、发邮件
🖼 「拿在手上」：把截图浮在你正在做的事前面，照着做设计、抄数字都方便
🎨 六套主题：纸、像素、拟物、极简、霓虹、手账

不用账号，不联网，密码不记，验证码 10 分钟自己清掉。
免费开源，macOS 14 以上，Apple 芯片和 Intel 都能用。

GitHub 搜 EnCopy 就能下载 ⭐️ 有想要的功能评论区告诉我～

#Mac #Mac软件 #效率工具 #剪贴板 #开源 #macOS #独立开发 #生产力工具
```

评论区第一条（自己发）：

```text
下载地址：github.com/a1024053774/EnCopy
网页上能直接按 ⌥C 试玩：a1024053774.github.io/EnCopy
第一次打开会被系统拦一下（没有付费公证），到「系统设置 → 隐私与安全性」点「仍要打开」就好
```

## 重新生成

```sh
cd promo
INTRO_LANG=zh NODE_PATH=/tmp/encopy-render/node_modules node render.js video /tmp/encopy-render/zh.mp4
python3 soundtrack.py /tmp/encopy-render/zh.events.json /tmp/encopy-render/zh.wav
ffmpeg -y -i /tmp/encopy-render/zh.mp4 -i /tmp/encopy-render/zh.wav -c:v copy -c:a aac -b:a 192k -shortest /tmp/encopy-render/zh-sound.mp4
NODE_PATH=/tmp/encopy-render/node_modules node xhs/shoot.js /tmp/encopy-render   # frame.png and cover.png
ffmpeg -y -f lavfi -i color=c=0x1f45d8:s=1080x1440:r=30 -i /tmp/encopy-render/zh-sound.mp4 -i /tmp/encopy-render/frame.png \
  -filter_complex "[1:v]scale=1080:608[v];[0:v][v]overlay=0:416:shortest=1[b];[b][2:v]overlay=0:0" \
  -map 1:a -c:v libx264 -crf 18 -pix_fmt yuv420p -c:a copy -movflags +faststart xhs/EnCopy-xhs.mp4
```

`xhs/still.png` 是封面中间那张图：中文片子 15.6 秒处的一帧，缩到 1080×608。
