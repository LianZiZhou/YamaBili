# YamaBili

[English](README.md) | 中文

为 [YamaPlayer](https://vpm.kwxxw.net/) 加上 Bilibili 搜索、弹幕、分P/合集、收藏夹和 UP 主页，不改动 YamaPlayer 包内的任何文件。
开箱即用，默认连接作者托管的 API `https://bili.v.lzz.moe/`。

## 依赖

- Unity 2022.3，VRChat Worlds SDK 3.7.1+
- YamaPlayer 1.5.x（`net.kwxxw.yama-stream`，VPM 源 `https://vpm.kwxxw.net/index.json`）

## 快速开始

1. 通过 VCC 安装 YamaPlayer，并在场景里放一个 YamaPlayer。
2. 导入 `YamaBili-x.y.z.unitypackage`。文件会放在 `Assets/YamaBili`，之后可以移动到别处。
3. 执行菜单 **Tools → YamaBili → Build Into YamaPlayer**。它会找出场景里所有 YamaPlayer 及其全部屏幕（包括 SubScreen），在左侧栏加上“视频搜索”按钮，并放入页面、链接输入框和弹幕层，最后烘焙 URL 池。
4. 选中 `YamaPlayer/BiliPlayer`，按需在 Inspector（URL Baker）里设置 **r** 和 **z**，然后点 **Bake URL Pools**。
5. 玩家需要在 VRChat 设置里开启 “Allow Untrusted URLs”。

## 更新 YamaPlayer

YamaBili 只往场景里的 YamaPlayer 副本上添加对象。更新步骤：先执行 **Tools → YamaBili → Remove From YamaPlayer**，然后升级 YamaPlayer，再执行一次 **Build Into YamaPlayer**。
r、z、服务器地址等设置在重新 Build 后会保留。如果新版 YamaPlayer 改了界面结构，Build 会列出缺少的节点并停止，场景不会被修改。

## r 和 z

Udon 无法在运行时拼接 URL，所以世界里预先烘焙了两组编号链接：`?v=N`（视频）和 `?q=N`（列表页、封面图）。
服务器按顺序把搜索结果放进环形池的槽位，并返回槽号。环转满一圈后，最早的槽会被覆盖。

| 参数 | 取值 | 作用 |
|---|---|---|
| `r` | 1–100 的整数，默认 1 | 容量，单位 1 万：V 和 Q 各 `r × 10000` 条。值越大，链接被覆盖前存活得越久，但世界也越大 |
| `z` | 1–5 位小写字母，默认用共享池 | 分区：给你的世界一个独立的池，其他世界的流量无法覆盖它 |
| `m` | `<r><z>` | r 和 z 合写，用来缩短 URL：`m=10ab` 等于 `r=10&z=ab`，`m=ab` 等于 `z=ab`，`m=10` 等于 `r=10` |

一个池由 (r, z) 共同确定，改动其中任何一个都要重新烘焙并重新上传世界。默认池（r=1 且不填 z）的链接里不带 `m`。
更详细的原理见 URL Baker 的 Inspector。

## 自建服务端

服务端不开源。如果要自己实现，请参照 [Docs/Backend-API.zh.md](Docs/Backend-API.zh.md) 中的接口约定，然后把 `ServerRoot` 改成你的地址并重新烘焙。

## 设置（`BiliUrlPool`）

- `ServerRoot`：API 地址，末尾带 `/`
- `Capacity`（r）、`Zone`（z）
- `SaveKeyPrefix`：保存搜索历史和弹幕设置用的 PlayerData 键前缀（默认 `YamaBili.`）
- `ShowCredit`：是否在 Info 页显示插件作者署名

## 许可

MIT，见 [LICENSE](LICENSE)，仅适用于本插件。只提供 Bilibili 的公开内容，请遵守 Bilibili 的服务条款。
作者：莲子粥 Lynn（[LianZiZhou](https://github.com/LianZiZhou)）
