# YamaBili Backend API

English | [中文](Backend-API.zh.md)

This document is for people who want to run their own server. The official server is closed source; this page only describes the contract the plugin relies on. Any implementation that follows it works.
You have to handle Bilibili login, risk control, stream URL resolution and HTTPS yourself.

All requests go to `ServerRoot` (for example `https://example.com/`). The path is always `/`; endpoints are told apart by query parameters only.

## 1. Slot pools

The world carries numbered URLs; the server decides what each number points to.

- Each pool has two rings: `v` (playable videos) and `q` (list pages, cover atlases), each with `r × 10000` slots.
- A pool is identified by **(r, z)**. Every request may carry:
  - `r`: 1–100, no leading zero, default 1
  - `z`: `[a-z]{1,5}`; absent means the shared default pool
  - `m`: both packed, matching `^([1-9]\d{0,2})?([a-z]{1,5})?$` and not empty (`m=10ab` is `r=10&z=ab`). `m` together with `r`/`z` is invalid
- Invalid values answer `{"error":"bad_pool"}`; a video player gets HTTP 400 instead.
- Allocation: write into the next slot in order and wrap around at the end, overwriting the oldest entry. Entries with a dedupe key (same video part, same query page) reuse their slot until it is overwritten.
- Slot tables must be persistent: a number must keep its meaning across restarts. Everyone in an instance stays in sync through the same number.
- A slot number out of range, or an empty slot, answers `{"error":"expired"}`; a video player gets 404.

## 2. Routes

| Request | Answer |
|---|---|
| `?search=<keyword or video link>` | List JSON, 20 per page. For a video link, just that one item |
| `?mid=<uploader id or space link>` | List JSON: the uploader's videos |
| `?fav=<favorites link or id>` | List JSON: a public favorites folder |
| `?q=N` | Replays q slot N: list JSON, or a JPEG if the slot holds an atlas |
| `?v=N` | See section 3 |
| `?url=<Bilibili video link>` | Same as `?v=N`, after giving the video a v slot (the `i` field) |

The first three are input-field prefixes. Players type after `…?search=` and VRChat sends the request as typed; it may be unencoded UTF-8, and `+` must be treated as a literal character.

## 3. One `?v=N`, two clients

- **Udon downloader** (VRCStringDownloader; User-Agent contains `UnityWebRequest` or `UnityPlayer`): answer the video JSON.
- **Video player** (every other client): 302 to a playable mp4 stream, or proxy the stream (with Range support).
- When debugging, `&as=json` forces the JSON answer.

## 4. JSON

Every error is `{"error":"<code>"}` with HTTP 200, because Udon cannot read the body of an error status.

List:
```json
{"kind":"search|mid|fav","title":"...","page":1,"pages":50,
 "self":21,"prev":-1,"next":19,"img":20,
 "items":[{"i":0,"bv":"BV...","title":"...","owner":"...","dur":"3:25","up":7}]}
```
`i` is a v slot; `self`/`prev`/`next`/`img`/`up` are q slots, `-1` when absent. Replaying an `up` slot gives page 1 of that uploader.

Video:
```json
{"kind":"video","i":0,"bv":"BV...","p":1,"title":"...","part":"","owner":"...",
 "roll":[[12.5,"comment"],[13.0,"coloured",16711680]],"top":[...],"bottom":[...],
 "sub":[[1.0,3.2,"subtitle"]],
 "pages":[{"i":5,"title":"P1 ...","link":"https://www.bilibili.com/video/BV...?p=1"}],"cur":0}
```
- Danmaku entries are `[seconds, text]`, with a third item `0xRRGGBB` when the colour is not white.
- `sub` entries are `[start seconds, end seconds, text]`.
- `pages` lists the parts or series episodes, each with its own v slot; `cur` is the index of the current one, `-1` when there are none.

## 5. Cover atlas

An `img` slot answers one JPEG, 160 wide and `90 × 20` tall: the covers of the list's 20 items from top to bottom, each 160×90, cropped to cover. Missing cells stay blank.

## 6. Notes

- VRChat requires HTTPS.
- `/healthz` is optional.
- Serve only public content and respect Bilibili's terms of service.
