---
id: T-011
title: 改名成 EnCopy，原样能编译能跑
type: build
status: open
blocked_by: []
claimed_by:
supersedes:
---

## What to build

项目文件夹变成 Tendedero 的 fork，打开就是 EnCopy：名字、bundle id、图标都是 EnCopy 自己的，界面只有简体中文和英文，其余行为和 Tendedero 一模一样。这一步只换身份，不加功能，后面每一步都在它上面改。依据 [T-001](T-001-fork-tendedero.md)、[T-010](T-010-languages.md)、[T-014](T-014-bundle-id.md)。

## Acceptance

- [ ] 项目文件夹是 Tendedero 的 fork：保留上游提交历史，另设一个指向原仓库的上游地址，基线是 `3c5b575`；设计方案、项目地图、词汇表一起进仓库。
- [ ] 构建出的 App 叫 EnCopy，bundle id 是 `app.encopy.EnCopy`；菜单、提示框、权限说明、访达右键服务里都不再出现 Tendedero 这个名字（说明「基于 Tendedero」的致谢除外）。
- [ ] 图标是新的（占位也行），没有用 Tendedero 的图标或它 docs 里的图片。
- [ ] 上游的 MIT 版权和许可声明原样保留；README 写明 EnCopy 基于 Tendedero 和它的作者。
- [ ] 界面文字只剩简体中文和英文两份，字符串检查只校验这两份并通过。
- [ ] 改名后的本地检查脚本全部通过（上游现有 80 项）。
- [ ] EnCopy 的设置和文件放在自己的位置，不读写 Tendedero 的；两个 App 同时装着互不影响。
- [ ] 真机：打开 EnCopy，在菜单栏打开记录复制的文字后，截一张图、复制一段文字，都挂上绳，行为和 Tendedero 一样。留截图和步骤。
- [ ] 项目地图里词汇表的覆盖范围改成代码目录，`status` 无问题。

## Resolution

