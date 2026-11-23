---
id: T-042
title: 每张纸显示来源 App 的图标
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

用户 2026-11-23：「显示来源 App 这个也要做上」。T-026 只在文字类的纸上、时间后面写了 App 名字，字很小；图片和文件没有。每张纸右下角放来源 App 的小图标，设置里能关。

## Acceptance

- [x] 文字、链接、代码、复制的图片：右下角 16 点的来源 App 图标；历史里没记 App 的不显示；不在这台 Mac 上的 App 不显示。
- [x] 设置 → 晾衣绳 →「显示来源 App」，默认开。
- [x] 重绘时不查库、不读盘：App 按路径缓存，图标按 App 缓存。
- [x] 自动检查通过；App 能编译。

## Resolution

完成，2026-11-23。`Line.showsSourceApp`、`Line.sourceApp(of:)`、`AppIcons.icon(for:)`；`PeggedView` 右下角画图标；设置窗口加开关。访达里复制的文件不进历史，本来就是文件夹纸，没有图标。渲染（微信、终端、Chrome、访达四个来源）：`evidence/T-042/light.png`（重画：`evidence/T-034/run.sh evidence/T-042 <输出.png>`）。新检查 `checkSourceAppBadge`（6 条），旧代码上编译不过。`scripts/check.sh`（虚拟屏上）：`139 strings`、`366 checks passed`。
