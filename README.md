# YamaBili

Bilibili 搜索、弹幕、分P/合集、收藏夹和 UP 主页，以插件形式加进 [YamaPlayer](https://vpm.kwxxw.net/)。YamaPlayer 包内的文件一个都不改。
开箱即用：默认连接作者托管的 API `https://bili.v.lzz.moe/`。

*Bilibili search, danmaku, parts/series, favorites and uploader pages for YamaPlayer, added without modifying the YamaPlayer package. Works out of the box against the hosted API at `https://bili.v.lzz.moe/`.*

## 依赖 / Requirements

- Unity 2022.3, VRChat Worlds SDK 3.7.1+
- YamaPlayer 1.5.x (`net.kwxxw.yama-stream`, VPM repo `https://vpm.kwxxw.net/index.json`)

## 快速开始 / Quick start

1. 通过 VCC 安装 YamaPlayer，并把 YamaPlayer 放进场景。
2. 导入 `YamaBili-x.y.z.unitypackage`（内容会放在 `Assets/YamaBili`，可以移动到其他位置）。
3. 菜单 **Tools → YamaBili → Build Into YamaPlayer**。它会找出场景里所有 YamaPlayer 及其所有屏幕（包括 SubScreen），在左侧栏加上“视频搜索”按钮，放入页面、链接输入框和弹幕层，并烘焙 URL 池。
4. 选中 `YamaPlayer/BiliPlayer`，在 Inspector（URL Baker）里按需设置 **r** 和 **z**，然后点 **Bake URL Pools**。
5. 玩家需要在 VRChat 设置里开启 “Allow Untrusted URLs”。

*Install YamaPlayer, import the unitypackage, run **Tools → YamaBili → Build Into YamaPlayer**, then pick r/z in the
`YamaPlayer/BiliPlayer` inspector and press **Bake URL Pools**. Players need "Allow Untrusted URLs".*

## 更新 YamaPlayer / Updating YamaPlayer

YamaBili 只在场景里的副本上加东西。升级流程：**Tools → YamaBili → Remove From YamaPlayer**，升级 YamaPlayer，再执行 **Build Into YamaPlayer**。
r、z、服务器地址等设置在重新 Build 时会保留。如果新版 YamaPlayer 改了界面结构，Build 会列出缺少的节点并停止，不会修改场景。

*Remove From YamaPlayer → update → Build Into YamaPlayer. Settings survive a rebuild; if a new YamaPlayer changed its UI,
Build lists the missing parts and stops without touching the scene.*

## r 和 z

VRChat 不允许 Udon 在运行时拼接 URL，所以世界里预先烘焙了两组编号链接：`?v=N`（视频）和 `?q=N`（列表页、封面图）。
服务器把搜索结果按顺序放进环形池的槽位，然后返回槽号。池转满一圈后，最早的槽会被覆盖。

| 参数 | 取值 | 作用 |
|---|---|---|
| `r` | 1–100 的整数，不填为 1 | 容量，单位 1 万：V 和 Q 各 `r × 10000` 条。越大链接存活越久，但世界越大 |
| `z` | 1–5 位小写字母，不填为共享池 | 分区：世界独占一个池，不被其他世界的流量冲掉 |
| `m` | `<r><z>` | 二者合写来缩短 URL：`m=10ab` 等于 `r=10&z=ab`，`m=ab` 等于 `z=ab`，`m=10` 等于 `r=10` |

池由 (r, z) 共同确定。修改任何一个都必须重新烘焙并重新上传世界。默认池（r=1 且不填 z）的链接不带 `m`。
详细原理见 URL Baker 的 Inspector。

*A pool is (r, z): r sets the size (×10000 per ring), z gives your world a pool of its own, m packs both. Changing either needs a re-bake and re-upload.*

## 自建服务端 / Self-hosting

服务端不开源。如果要自己实现，可以参考 [Docs/Backend-API.md](Docs/Backend-API.md) 中的接口约定，然后把 `ServerRoot` 改成你的地址并重新烘焙。

*The server is not open source; [Docs/Backend-API.md](Docs/Backend-API.md) describes the contract a compatible server must follow.*

## 设置 / Settings (`BiliUrlPool`)

- `ServerRoot`：API 地址，末尾带 `/`
- `Capacity` (r)、`Zone` (z)
- `SaveKeyPrefix`：PlayerData 键前缀，用于保存搜索历史和弹幕设置（默认 `YamaBili.`）
- `ShowCredit`：是否在 Info 页显示插件作者署名

## License

MIT, see [LICENSE](LICENSE). Covers this plugin only. Only public Bilibili content is served; respect Bilibili's terms of service.
作者 / Author: 莲子粥 Lynn ([LianZiZhou](https://github.com/LianZiZhou))
