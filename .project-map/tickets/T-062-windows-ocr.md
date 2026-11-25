---
id: T-062
title: Windows 按文字找图片（Windows.Media.Ocr）
type: build
status: open
blocked_by: [T-061]
claimed_by:
supersedes:
---

## What to build

在 Windows 版落地离线图片文字识别能力，让用户按图片和截图里的字进行全文检索，对标 Mac 版 `SearchIndex.swift`（Vision 框架）。依据 [T-056](T-056-windows-platform-and-release.md)。

包含内容：

- 图片入库或变化时在后台低优先级队列异步调用 WinRT `Windows.Media.Ocr.OcrEngine` 识别文字（对文件大小、尺寸与图片修改时间做缓存，避免重复识别），结果折叠规范化后写入 SQLite FTS5 索引（与文本统一参与搜索与 `SearchIndex` 触发刷新）。
- 优先使用 `zh-Hans-CN` 语言引擎（实测可同时识别简体中文与英文数字）；若目标机器未安装中文 OCR 语言包，自动回退已安装的识别语言，并在设置窗口的对应位置给出友好提示引导安装系统中文识别语言包，不静默丢弃功能或报错崩溃。
- 搜索时若凭借图里识别出的字命中图片（即便图片标题或文件名不含搜索词，或原图已被缩成缩略图），卡片或篮子行显示「图中文字」黄底角标；来自资源管理器复制的图片同时保留文件名可搜，EnCopy 自己文件夹的图片/截图只按图中文字搜。

## Acceptance

- [ ] 包含简体中文与英文的测试图片进入历史后，后台异步完成 `Windows.Media.Ocr` 识别并写入索引；在 `Alt+C` 搜索框和篮子列表里输入图里的中文词或英文词均可命中该图片并显示「图中文字」标记。
- [ ] 原图缩成缩略图后，历史索引里仍保留从原图识别出的字，依然能搜中。
- [ ] 在缺少 `zh-Hans-CN` OCR 语言包的环境下能够平稳回退可用语言，且设置页面正确提示缺失中文识别包（通过单元测试/模拟检查验证）。
- [ ] `windows/scripts/check.ps1` 跑通真实含字样本图的 `Windows.Media.Ocr` 端到端识别与搜索断言并全绿。
- [ ] 真机走查：使用 `EnCopy Dev` 复制一张带中英文文字的图片，等识别完成后按图里几个字搜出并贴回前台窗口，留截图在 `evidence/T-062/`。

## Resolution
