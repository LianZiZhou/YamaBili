# YamaBili

English | [中文](README.zh.md)

Bilibili search, danmaku, parts/series, favorites and uploader pages for [YamaPlayer](https://vpm.kwxxw.net/), added without modifying any file of the YamaPlayer package.
Works out of the box against the hosted API at `https://bili.v.lzz.moe/`.

## Requirements

- Unity 2022.3, VRChat Worlds SDK 3.7.1+
- YamaPlayer 1.5.x (`net.kwxxw.yama-stream`, VPM repo `https://vpm.kwxxw.net/index.json`)

## Quick start

1. Install YamaPlayer through VCC and put a YamaPlayer in your scene.
2. Import `YamaBili-x.y.z.unitypackage` (it goes to `Assets/YamaBili`; you may move the folder).
3. Run **Tools → YamaBili → Build Into YamaPlayer**. It finds every YamaPlayer in the scene and all of its screens (SubScreens included), adds the "video search" button to the left side bar, the page, the link input and the danmaku layer, and bakes the URL pools.
4. Select `YamaPlayer/BiliPlayer`, set **r** and **z** in its inspector (the URL Baker) if you want, and press **Bake URL Pools**.
5. Players need "Allow Untrusted URLs" enabled in VRChat.

## Updating YamaPlayer

YamaBili only adds objects to the copy of YamaPlayer in your scene. To update: **Tools → YamaBili → Remove From YamaPlayer**, update YamaPlayer, then **Build Into YamaPlayer** again.
Settings such as r, z and the server address survive a rebuild. If a new YamaPlayer changed its UI layout, Build lists the missing parts and stops without changing the scene.

## r and z

Udon cannot build URLs at runtime, so the world carries two sets of pre-baked numbered links: `?v=N` (videos) and `?q=N` (list pages, cover images).
The server puts search results into the slots of a ring pool in order and returns the slot numbers. When the ring wraps around, the oldest slots are overwritten.

| Parameter | Values | Effect |
|---|---|---|
| `r` | integer 1–100, default 1 | Capacity in units of 10000: V and Q each get `r × 10000` URLs. Bigger keeps links alive longer but makes the world larger |
| `z` | 1–5 lowercase letters, default: shared pool | Zone: your world gets a pool of its own that other worlds' traffic cannot overwrite |
| `m` | `<r><z>` | Both packed to keep URLs short: `m=10ab` = `r=10&z=ab`, `m=ab` = `z=ab`, `m=10` = `r=10` |

A pool is identified by (r, z) together. Changing either one means re-baking and re-uploading the world. The default pool (r=1, no z) has no `m` in its URLs.
The URL Baker inspector explains the details.

## Self-hosting

The server is not open source. To build your own, follow the contract in [Docs/Backend-API.md](Docs/Backend-API.md), then set `ServerRoot` to your address and re-bake.

## Settings (`BiliUrlPool`)

- `ServerRoot`: API address, with a trailing `/`
- `Capacity` (r), `Zone` (z)
- `SaveKeyPrefix`: prefix of the PlayerData keys that keep search history and danmaku settings (default `YamaBili.`)
- `ShowCredit`: show the plugin credit on the Info page

## License

MIT, see [LICENSE](LICENSE). It covers this plugin only. Only public Bilibili content is served; respect Bilibili's terms of service.
Author: 莲子粥 Lynn ([LianZiZhou](https://github.com/LianZiZhou))
