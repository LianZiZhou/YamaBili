# YamaBili 服务端接口 / Backend API

这份文档写给想自建服务端的人。官方服务端不开源，下面只描述插件依赖的接口约定。实现方式不限，满足这些约定即可。
需要自己处理的部分包括：Bilibili 的登录、风控、播放地址获取、HTTPS。

*For people who want to run their own server. The official server is closed source; this is only the contract the plugin relies on.*

所有请求都发到 `ServerRoot`（例如 `https://example.com/`），只通过查询参数区分接口，路径固定为 `/`。

## 1. 槽位池 / Slot pools

世界里烘焙的是编号 URL，服务器负责决定每个编号当前指向什么。

- 每个池包含两个环：`v`（可播放的视频）和 `q`（列表页、封面图集），每个环有 `r × 10000` 个槽。
- 池由 **(r, z)** 唯一确定。所有请求都可以带：
  - `r`：1–100，不能有前导零，默认 1
  - `z`：`[a-z]{1,5}`，不填表示共享默认池
  - `m`：合写形式，匹配 `^([1-9]\d{0,2})?([a-z]{1,5})?$` 且不能为空（`m=10ab` 等于 `r=10&z=ab`）。`m` 与 `r`/`z` 同时出现时视为非法
- 非法值返回 `{"error":"bad_pool"}`。如果请求来自视频播放器，返回 HTTP 400。
- 分配方式：按顺序写入下一个槽，到末尾后回绕，覆盖最旧的条目。带去重 key 的条目（同一视频分P、同一查询页）在没被覆盖之前复用原来的槽。
- 槽表必须持久化，服务重启后同一个编号的含义不能改变。同一实例里的所有玩家依靠同一个编号同步。
- 槽号超出范围或者槽为空时，返回 `{"error":"expired"}`；如果请求来自视频播放器，返回 404。

## 2. 路由 / Routes

| 请求 | 返回 |
|---|---|
| `?search=<关键词或视频链接>` | 列表 JSON，每页 20 条。传入的是视频链接时，只返回这一条 |
| `?mid=<UP 主 id 或空间链接>` | 列表 JSON：该 UP 主的投稿 |
| `?fav=<收藏夹链接或 id>` | 列表 JSON：公开收藏夹 |
| `?q=N` | 重放 q 槽 N：列表 JSON；如果该槽是图集，返回 JPEG |
| `?v=N` | 见下面第 3 节 |
| `?url=<Bilibili 视频链接>` | 和 `?v=N` 相同，只是会先给这个视频分配一个 v 槽（`i` 字段） |

前三个是输入框的前缀。玩家在 `…?search=` 后面输入关键词，VRChat 会原样发出请求，可能是未经编码的 UTF-8，`+` 也要当作字面字符处理。

## 3. 同一个 `?v=N` 对应两种客户端 / One URL, two clients

- **Udon 下载器**（VRCStringDownloader，User-Agent 含 `UnityWebRequest` 或 `UnityPlayer`）：返回视频 JSON。
- **视频播放器**（其他所有客户端）：302 重定向到可播放的 mp4 流地址，或者直接代理这条流（需要支持 Range）。
- 调试时，加上 `&as=json` 可以强制返回 JSON。

## 4. JSON

所有错误都用 HTTP 200 返回 `{"error":"<code>"}`，因为 Udon 读不到错误状态码的响应体。

列表 / List:
```json
{"kind":"search|mid|fav","title":"...","page":1,"pages":50,
 "self":21,"prev":-1,"next":19,"img":20,
 "items":[{"i":0,"bv":"BV...","title":"...","owner":"...","dur":"3:25","up":7}]}
```
`i` 是 v 槽；`self`/`prev`/`next`/`img`/`up` 是 q 槽，`-1` 表示不存在。`up` 槽重放后得到该 UP 主的第 1 页。

视频 / Video:
```json
{"kind":"video","i":0,"bv":"BV...","p":1,"title":"...","part":"","owner":"...",
 "roll":[[12.5,"弹幕"],[13.0,"彩色",16711680]],"top":[...],"bottom":[...],
 "sub":[[1.0,3.2,"字幕"]],
 "pages":[{"i":5,"title":"P1 ...","link":"https://www.bilibili.com/video/BV...?p=1"}],"cur":0}
```
- 弹幕格式是 `[秒, 文本]`；颜色不是白色时再加第三项 `0xRRGGBB`。
- `sub` 是 `[开始秒, 结束秒, 文本]`。
- `pages` 是分P或合集的列表，每项都分配一个 v 槽；`cur` 是当前项在列表中的下标，没有分P或合集时为 `-1`。

## 5. 封面图集 / Cover atlas

`img` 槽返回一张 JPEG：宽 160，高 `90 × 20`，从上往下依次是列表里 20 个条目的封面（每张 160×90，按 cover 方式裁切）。缺少的格子留空。

## 6. 其他 / Notes

- VRChat 要求 HTTPS。
- `/healthz` 可选。
- 只代理公开内容，并遵守 Bilibili 的服务条款。
