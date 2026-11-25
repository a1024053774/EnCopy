---
id: T-066
title: Windows 录屏裁剪（Windows.Media.Editing）
type: build
status: open
blocked_by: [T-065]
claimed_by:
supersedes:
---

## What to build

Windows 下无需引入体积庞大的 `ffmpeg`，直接使用 WinRT 原生 `Windows.Media.Editing.MediaComposition` 实现录屏卡片的首尾裁剪，与 Mac 版 [Trim.swift](../../Sources/EnCopy/Trim.swift) 行为对齐。依据 [T-056](T-056-windows-platform-and-release.md)。

包含内容：

- 录屏文件（如来自系统 `FOLDERID_Captures` 或 EnCopy 文件夹的 `.mp4`）挂上绳时抽取首帧缩略图并叠加播放标志；来自外部普通文件夹的视频仅作只读展示与快速查看，不改写原文件。
- 对 EnCopy 自己文件夹内的录屏长按或右键打开内置录屏裁剪窗：支持实时播放预览、首尾双滑块选定保留区间、时间精确微调。
- 确认保存后调用 `Windows.Media.Editing.MediaComposition` 将所选时间段导出到同一目录下的隐藏临时文件（避免被监视器误判为新文件），完成后原子替换原文件并刷新绳上首帧缩略图。

## Acceptance

- [ ] 录屏卡片展示真实视频首帧与播放图标，外部普通视频只打开快速查看而不开放写回裁剪。
- [ ] 内置裁剪窗能加载视频播放、拖动起止滑块选择片段；点保存导出有效的新视频文件（时长符合裁剪范围，画面音轨正常），且导出过程中临时文件不会误触发二次上绳。
- [ ] 替换原文件后绳上缩略图同步更新，取消不改原视频。
- [ ] `windows/scripts/check.ps1` 覆盖可裁剪边界判断与真实短视频样本通过 `MediaComposition` 裁剪后的时长验证并全绿。
- [ ] 真机走查：使用 `EnCopy Dev` 放入一段短 `.mp4` 录屏并完成首尾裁剪，对比裁剪前后时长与播放效果，留截图在 `evidence/T-066/`。

## Resolution
