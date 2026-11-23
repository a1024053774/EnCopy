---
id: T-036
title: 动画表：纸按轻重摆，拉下和滑下各用各的弹簧，减弱动态效果
type: build
status: closed
blocked_by: []
claimed_by:
supersedes:
---

## What to build

[T-006](T-006-motion.md) 定的「细腻」动画，数值照设计方案「动画」一节的表。这一张做表里和按键、挂上、风有关的部分，余下的在 [T-038](T-038-motion-rest.md)。

## Acceptance

- [x] 所有数值集中在 `Motion` 一处。纸的轻重（摆动弹簧 stiffness / damping）：小票 38 / 2.4，吊牌 42 / 2.6，拍立得 46 / 2.8，打孔纸 50 / 3.0，文件夹 58 / 3.4。挂上、轻晃、微风都按这张纸的重量摆。
- [x] ⌥C 拉下用 spring(0.28, 0.86)，每张纸轻晃 1.5–3°；路过滑下用 spring(0.42, 0.82)，从左到右依次晃 5–9°，最右一张晚 0.26 秒；收起改成 0.16 秒 ease-out cubic-bezier(.23, 1, .32, 1)，不回弹。
- [x] 选中 0.11 秒 ease-out；为跟住选中而滚动用 spring(0.24, 1)；搜索、Tab、↓ 让纸滑拢用 spring(0.24, 1)，挂上新纸让位还是 spring(0.55, 0.78)。
- [x] 单击复制：纸向上跳约 7 点，spring(0.42, 0.72) 落回。
- [x] 微风：最后一次按键（打字、挑、Tab、↓）后 4 秒内不吹；⌥C 拉着时幅度减半。
- [x] 减弱动态效果：不晃、不吹风、不跳、不飞、不掉；面板只做 0.12 秒淡入淡出，选中只换描边，挂上只淡入。
- [x] 自动检查通过；App 能编译。

## Resolution

完成，2026-11-23。新文件 `Motion.swift` 放整张表；`PeggedView` 按纸的种类取摆动弹簧，接 `line.landing` 做拉下时的轻晃和滑下时的一阵风，单击复制改成跳一下；`LineView` 的面板动画由 `Motion.panel(revealed:bySummon:)` 选，搜索类变化用无回弹的滑拢；`Line` 记 `lastKeyAt`，风只在键盘安静 4 秒后吹，跟住选中的滚动带 spring(0.24, 1)；`AppDelegate` 的 ⌥C 路径告诉 Line 这次是 ⌥C 拉下来的，原来自己读系统「减弱动态效果」的地方改读 `Motion.reduced`。`NoteSheet.paper(of:)` 公开出来给摆动用。

新加 `checkMotion`（17 条），数值以设计方案的表为准。三个变异版本都被抓到：风的安静时间改成 0（「No breeze until 4 s after the last key」）、⌥C 用滑下的弹簧（「⌥C drops the panel on the quick spring」）、挑选不算按键（「Picking counts as a key: the breeze waits」）。`scripts/check.sh`：`291 checks passed`；`scripts/build-app.sh` 通过。手感要真机看，跟这一批一起走。

真机（2026-11-23，开发版，虚拟屏 + 外接屏；证据在本地 `evidence/walk-v1/`，不进 git）：没走到：动画手感。用户这段时间在用鼠标（8 秒里 170 次硬件移动），按键和指针会抢，停在这里；自动检查是这一项现有的证据。
