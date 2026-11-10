# AI 听书交接（ShoreKeeperAss）

> 2026-11-10。写给换电脑后接手/继续验收的人。
> 本文只写仓库相对路径，不写本机绝对路径、不写 token/密钥。

## 0. 先看这一页

- 代码分支：`ShoreKeeperAss` 的 **`feature/ai-audiobook`**（功能提交 `61f142c`；后续修复/排版/语速见该分支后续提交）。
- 手机入口：`AIClientPhoneView` 的 `main`（`/reader` 反代已合并，提交 `9f11cc7`；早期文档写的 `codex/app-server` 分支已不是唯一来源）。
- **不要直接把本分支合并进当前 GitHub `main`**：GitHub `main` 现在是一条并行分支（目录已改成 `ShoreKeeperHost/`、`ShoreKeeperClient/`，并且不含 `MiniMax/`、`BiliWhisper/` 等文件）。本分支基于“功能完整”的旧 `main`（`AIAssistantHost/`、`AIAssistantClient/`），先对齐目录/恢复文件后再合并，否则 TTS 会丢。
- 本机已验证的部分：构建、Reading 单测、HTTP 单测、真实 TTS + 缓存命中、`127.0.0.1` 监听、局域网 IP 经 PhoneView 打开 `/reader`、真实 mp3 Range、20 分钟连续播放、慢速由 TTS 合成而非变速。
- **还没验的**：4G 真机、iOS 锁屏/后台（需要 HTTPS）、WPF 界面逐按钮手测。换电脑主要就是补这三项。

## 1. 已完成内容

### M1 解析 + 分段 + 缓存（`AIAssistantHost/ShoreKeeper.Host.Wpf/AIAssistant.Host.Wpf/Reading/`）
- `BookParser`：txt（BOM/UTF-8/GB18030 自动判定）+ epub（`System.IO.Compression` 读 container.xml → OPF → spine → xhtml，不引第三方包）。
- `BookSegmenter`：中英文句末标点/换行切段，每段 ≤200 字，超长句按逗号/空格二次切，保留 `chapterIndex / segmentIndex / charOffset`。
- `TtsCache`：`SHA256(bookId|segmentIndex|voiceId|speed|text)`；文件落 `Recordings/TTS/<bookId>/<idx>.mp3`，同目录 `<idx>.key` 作为 sidecar；命中不调 TTS，并发同段只合成一次。

### M2 本机播放（WPF）
- `Reading/Playback/NAudioReadingSink.cs`：`WaveOutEvent` + `AudioFileReader`；1x 直连，1.25/1.5/2.0 加速档用移调+重采样（音高基本不变）。慢速不走播放端变速。
- `Reading/ReadingQueue.cs`：顺序播放、只预取下一段、自动续播、上/下一段、上/下一章、整书跳段、加速档、断点。
- `ReadingWindow.xaml`：书库列表、打开 txt/epub、章节下拉、段文本、整书段进度、段内进度、播放/暂停、加速档。
- 断点：`<宿主工程目录>/books/<bookId>/progress.json`（段序号 + 段内位置 + 倍速）；重启后打开书并从断点 Resume。
- **朗读语速**（2026-11 新增）：主界面音色行右侧的「语速」下拉（0.85–1.10）改的是 `voice_setting.speed`，由 MiniMax 直接读快/读慢，写入 `ReadingService.SpeechSpeed`；语速进 TTS 缓存 key，换档后相关段自动重新合成（旧档 mp3 仍留在 `Recordings/TTS/`，不自动删）。
- 密钥：`MiniMax/MiniMaxTtsService.cs` 只读环境变量 `MINIMAX_API_KEY`，不再读本地 Key 文件。

### M3 手机远程听
- `Remote/ReadingHttpServer.cs`：手写 HTTP/1.1，只绑 `127.0.0.1:47630`（可用 `READING_HTTP_PORT` 覆盖），支持 `/reader` 前缀别名。
- API：`/api/books`、`/api/books/{id}`、`/api/books/{id}/segments`、`/api/progress`（GET/POST）、`/media/{bookId}/{idx}.mp3`。
- 媒体：首次按需 TTS，之后走缓存；`Accept-Ranges: bytes`，支持三种 Range 写法，206/416，HEAD。
- `Remote/ReaderPage.html`：内嵌单页，手机浏览器直接开；进度条、章节、倍速、5 秒节流保存、Media Session。倍速下拉是 0.80/0.85/0.90/0.95/1.00/1.25/1.5/2.0，慢档通过 `/media/<bookId>/<i>.mp3?speed=0.9` 交给服务端合成（TTS 读慢），播放端固定 1.0，不会有变速电音；切档会从头重放本段。
- PhoneView：`/reader` → `http://127.0.0.1:<readerPort>`，复用 token；页面壳不校验 token（让页面能读 localStorage），API/media 继续校验。

### 测试/验收工具
- `AIAssistantHost/ShoreKeeper.Host.Wpf/Reading.Tests/`：121 项，覆盖解析/编码/分段/缓存/队列/断点/NAudio 冒烟。
- `AIAssistantHost/ShoreKeeper.Host.Wpf/Remote.Tests/`：39 项，覆盖 API/Range/HEAD/POST/并发。
- `AIAssistantHost/ShoreKeeper.Host.Wpf/Reading.LongRun/`：20 分钟长跑工具，预置缓存，不消耗 TTS。

## 2. 本机已验收（换电脑可复跑）

- `dotnet build`：0 warning / 0 error。
- `Reading.Tests`：`PASS 134 / FAIL 0`（2026-11 语速改造后）。
- `Remote.Tests`：`PASS 43 / FAIL 0`（含 `/media?speed=` 透传）。
- 真实 MiniMax TTS：首次 `3.07s` 生成 `680,820` 字节 mp3；第二次 `0.018s` 命中缓存，文件 mtime 未变。
- 语速实测（同一段）：`speed=1.0` → 38.16s / 612KB，`speed=0.9` → 44.89s / 720KB，二次命中 `184ms`。
- `netstat`：只有 `127.0.0.1:47630`，无 `0.0.0.0`。
- PhoneView：`/reader` 经 `127.0.0.1`、局域网 IP、Tailscale IP 都 200；真实 mp3 `Range: bytes=1000-1999` → 206，`Content-Range: bytes 1000-1999/680820`。
- 长跑：125 段 × 10 秒 = 21:00 连续播放，段间稳定 10 秒，无卡顿、无缓存 miss。
- 真实书：龙枪 109 章 / 4245 段、冰与火 354 章 / 18867 段、猎魔人 epub 132 章。

## 3. 新电脑上怎么跑

> 前置：.NET 8 SDK / Windows、Node 20+（PhoneView）、同一 Tailscale 账号、`MINIMAX_API_KEY` 由使用者自己设置。

```powershell
# 1) 代码
git clone https://github.com/Observer730/ShoreKeeperAss
cd ShoreKeeperAss
git switch feature/ai-audiobook
# PhoneView 单独 clone；它当前在 codex/app-server 分支
git clone https://github.com/Observer730/AIClientPhoneView

# 2) 密钥（只放环境变量，别写文件）
$env:MINIMAX_API_KEY = '<你的 key>'

# 3) 构建 + 单测
dotnet restore AIAssistantHost/ShoreKeeper.Host.Wpf/AIAssistant.Host.Wpf/AIAssistant.Host.Wpf.csproj
dotnet build   AIAssistantHost/ShoreKeeper.Host.Wpf/AIAssistant.Host.Wpf/AIAssistant.Host.Wpf.csproj -c Debug
dotnet run --project AIAssistantHost/ShoreKeeper.Host.Wpf/Reading.Tests -c Release
dotnet run --project AIAssistantHost/ShoreKeeper.Host.Wpf/Remote.Tests -c Release

# 4) 放书：AIAssistantHost/ShoreKeeper.Host.Wpf/AIAssistant.Host.Wpf/Book/（本地书不入库）

# 5) 启动宿主；听书服务自动起在 127.0.0.1:47630
AIAssistantHost/ShoreKeeper.Host.Wpf/AIAssistant.Host.Wpf/bin/Debug/net8.0-windows/AIAssistant.Host.Wpf.exe

# 6) 重启 PhoneView（server.mjs 改动不热更新）
cd AIClientPhoneView   # 实际目录名以 clone 为准
node server.mjs
# 手机打开 http://<机器名>.<tailnet>.ts.net:8787/reader?t=<PhoneView token>
# 本机也可自测：http://127.0.0.1:8787/reader?t=...
```

## 4. 换电脑后要补的验收

1. **WPF 界面手测**：打开「AI 听书」→ 选书 → 播放/暂停 → 倍速 → 章节跳转 → 关掉重开 → 从断点续。
2. **4G 真机**：手机关 Wi-Fi，走 Tailscale 打开 `.../reader`，播放、拖整书进度、断网重连后回断点。
3. **iOS 锁屏/后台**：先让 tailnet 管理员启用 Serve，然后：
   ```powershell
   tailscale serve --bg --yes --https=443 --set-path=/reader http://127.0.0.1:47630
   ```
   用 `https://<机器名>.<tailnet>.ts.net/reader` 打开；不要用 Funnel。
4. **20 分钟长跑（可选）**：
   ```powershell
   dotnet run --project AIAssistantHost/ShoreKeeper.Host.Wpf/Reading.LongRun -c Release -- `
     --mp3 <10 秒静音 mp3> --segments 125 --minutes 20
   ```
5. **GitHub main 对齐**：`main` 的 `ShoreKeeperHost` 改名分支不含 MiniMax/BiliWhisper；合并本分支前先恢复/迁移这些文件，否则 TTS 不可用。

## 5. 关键位置速查

| 功能 | 相对路径 |
|---|---|
| 入口/服务启动 | `AIAssistantHost/ShoreKeeper.Host.Wpf/AIAssistant.Host.Wpf/MainWindow.xaml.cs` |
| 解析/分段/缓存 | `.../AIAssistant.Host.Wpf/Reading/` |
| NAudio 播放/服务 | `.../Reading/Playback/` |
| HTTP 服务/手机页 | `.../Remote/` |
| 播放窗口 | `.../ReadingWindow.xaml` |
| 测试 | `AIAssistantHost/ShoreKeeper.Host.Wpf/{Reading.Tests,Remote.Tests,Reading.LongRun}/` |
| PhoneView 反代 | `AIClientPhoneView/server.mjs` 的 `/reader` 块 |

## 6. 已知坑 / 限制

- 服务端口固定思路：默认 47630；被占用时设 `READING_HTTP_PORT`，PhoneView 用 `--reader-port` 或 `PV_READER_PORT` 同步。
- PhoneView 改完必须重启；`/reader` 页面无 token 时只显示输入框，API/media 会 401。
- 章节识别（2026-11 已修）：`BookParser.IsTitleLine` 现在也认「`1.第1章 序幕`」和「`正文 第一卷 … 字数:7363`」两类前缀，并剥掉「字数:N」尾。实测龙枪 txt 109 章 / 冰与火 354 章 / 猎魔人 epub 132 章。
- 语速走合成端（`voice_setting.speed`），播放窗口的 1.25/1.5/2.0 仍是播放端移调，只有加速档可能听到轻微损失；慢速档已不再用播放端减速。
- 换 voiceId 或换语速都会因缓存 key 变化重新合成（消耗 TTS 配额）；手机页打开会自动请求当前段音频（不自动播放）。
- 宿主启动即开 HTTP 服务；如果 47630 被占用只写日志，不影响原有 TCP 47621。
- 本分支未改 Unity 客户端和 `TcpMessageServer`。

## 7. 回滚

- 整个功能：切回 `main`（旧完整版）或删除本分支；新增文件都在 `Reading/`、`Remote/`、`ReadingWindow.*`、测试工程，删除即可。
- PhoneView：还原 `server.mjs` 一个文件；`/reader` 块、`--reader-port`、启动横幅三处，回滚后不影响原路由。
