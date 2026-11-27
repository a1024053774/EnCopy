---
id: T-062
title: Windows 按文字找图片（Windows.Media.Ocr）
type: build
status: open
blocked_by: [T-061]
claimed_by: antigravity
supersedes:
---

## What to build

在 Windows 版落地离线图片文字识别能力，让用户按图片和截图里的字进行全文检索，对标 Mac 版 `SearchIndex.swift`（Vision 框架）。依据 [T-056](T-056-windows-platform-and-release.md)。

包含内容：

- 图片入库或变化时在后台低优先级队列异步调用 WinRT `Windows.Media.Ocr.OcrEngine` 识别文字（对文件大小、尺寸与图片修改时间做缓存，避免重复识别），结果折叠规范化后写入 SQLite FTS5 索引（与文本统一参与搜索与 `SearchIndex` 触发刷新）。
- 优先使用 `zh-Hans-CN` 语言引擎（实测可同时识别简体中文与英文数字）；若目标机器未安装中文 OCR 语言包，自动回退已安装的识别语言，并在设置窗口的对应位置给出友好提示引导安装系统中文识别语言包，不静默丢弃功能或报错崩溃。
- 搜索时若凭借图里识别出的字命中图片（即便图片标题或文件名不含搜索词，或原图已被缩成缩略图），卡片或篮子行显示「图中文字」黄底角标；来自资源管理器复制的图片同时保留文件名可搜，EnCopy 自己文件夹的图片/截图只按图中文字搜。

## Acceptance

- [x] 包含简体中文与英文的测试图片进入历史后，后台异步完成 `Windows.Media.Ocr` 识别并写入索引；在 `Alt+C` 搜索框和篮子列表里输入图里的中文词或英文词均可命中该图片并显示「图中文字」标记。
- [x] 原图缩成缩略图后，历史索引里仍保留从原图识别出的字，依然能搜中。
- [x] 在缺少 `zh-Hans-CN` OCR 语言包的环境下能够平稳回退可用语言，且设置页面正确提示缺失中文识别包（通过单元测试/模拟检查验证）。
- [x] `windows/scripts/check.ps1` 跑通真实含字样本图的 `Windows.Media.Ocr` 端到端识别与搜索断言并全绿。
- [ ] 真机走查：使用 `EnCopy Dev` 复制一张带中英文文字的图片，等识别完成后按图里几个字搜出并贴回前台窗口，留截图在 `evidence/T-062/`。

## Resolution

- **阶段实现提交**：`10b26a8`、`7814471`（分支 `feat/T-062-windows-ocr`，待真机走查全绿后合并关单）。
- **测试与审查记录**：
  - 按 `evidence-first-testing` 先在 `WindowsOcrAndImageTextBadge` 建立契约红灯检查，再实现 `SearchIndex`（低优先级后台队列、含识别语言签名的 `SearchIndex.json` 持久化缓存、`WinRtOcrBackend` 流式有界 `<= 2400px` 缩放与中英文双模型联合识字及字符字距拼接）与 `History.ocr_text` 索引规则（EnCopy 自有图片仅按图中文字可搜，资源管理器外部图片同时保留文件名可搜；原图缩成缩略图后不覆盖原图识别词；搜索命中图中文字时在拍立得卡右上角与篮子行右侧展示黄底「图中文字」角标；缺少简体中文识别包时平稳回退已安装语言并在设置「挂上复制的内容」页给出友好提示）。
  - 针对首轮 Codex `gpt-6.1-terra` 审查指出的 5 项边界缺陷先补红灯断言再修复转绿（`7814471`）：缓存键加入语言组合签名使后续补装中文包自动重跑；失败返回 `null` 与空文本 `""` 严格区分且补充英文轮次异常不丢失主语言结果；改为流式有界解码移除 64 MiB 整块读上限；重复复制（`onAgain`）与识别进行中再次变更均进入指纹复检；保留规则在 `ocr_text IS NULL` 时暂缓缩图，确保先识字再缩图。
  - 离屏渲染核验图已存于 `evidence/T-062/00-offscreen-rope-ocr-hit.png`、`00-offscreen-basket-ocr-hit.png`、`00-offscreen-settings-ocr-missing-zh.png`；`windows/scripts/check.ps1` 22/22 全绿通过。
