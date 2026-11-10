using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIAssistant.Host.Wpf.Reading;
using AIAssistant.Host.Wpf.Remote;

namespace Remote.Tests;

internal static class Program
{
    private const string BookId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private static int _pass;
    private static int _fail;
    private static string _tempRoot = string.Empty;

    public static async Task<int> Main()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "reading-remote-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        try
        {
            var catalog = new FakeCatalog(_tempRoot);
            await using var server = new ReadingHttpServer(catalog, 0);
            await server.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"), Timeout = TimeSpan.FromSeconds(15) };

            await TestBooksAsync(http);
            await TestBookAsync(http);
            await TestSegmentsAsync(http);
            await TestMediaAsync(http, catalog);
            await TestRangeAsync(http);
            await TestHeadAsync(http);
            await TestProgressAsync(http, catalog);
            await TestPageAsync(http);
            await TestNotFoundAsync(http);
            await TestConcurrentRangeAsync(http);
        }
        finally
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        Console.WriteLine($"PASS {_pass} / FAIL {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    private static async Task TestBooksAsync(HttpClient http)
    {
        var res = await http.GetAsync("/api/books");
        Expect(res.StatusCode == HttpStatusCode.OK, "GET /api/books 200");
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Expect(doc.RootElement.GetProperty("books").GetArrayLength() == 2, "书库 2 本");
        var first = doc.RootElement.GetProperty("books")[0];
        Expect(first.GetProperty("id").GetString() == BookId, "书 id 正确");
    }

    private static async Task TestBookAsync(HttpClient http)
    {
        var res = await http.GetAsync($"/api/books/{BookId}");
        Expect(res.StatusCode == HttpStatusCode.OK, "GET /api/books/{id} 200");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var chapters = doc.RootElement.GetProperty("chapters");
        Expect(chapters.GetArrayLength() == 2, "两章");
        Expect(chapters[0].GetProperty("firstSegmentIndex").GetInt32() == 0, "第一章 firstSegmentIndex=0");
        Expect(chapters[1].GetProperty("firstSegmentIndex").GetInt32() == 3, "第二章 firstSegmentIndex=3");
        Expect(doc.RootElement.GetProperty("progress").ValueKind == JsonValueKind.Null, "初始无断点");
    }

    private static async Task TestSegmentsAsync(HttpClient http)
    {
        var res = await http.GetAsync($"/api/books/{BookId}/segments?from=2&count=3");
        Expect(res.StatusCode == HttpStatusCode.OK, "GET segments 200");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var segments = doc.RootElement.GetProperty("segments");
        Expect(segments.GetArrayLength() == 3, "from=2 count=3 返回 3 段");
        Expect(segments[0].GetProperty("i").GetInt32() == 2, "首段 i=2");

        var chRes = await http.GetAsync($"/api/books/{BookId}/segments?chapter=1&count=10");
        using var chDoc = JsonDocument.Parse(await chRes.Content.ReadAsStringAsync());
        var chSegs = chDoc.RootElement.GetProperty("segments");
        Expect(chSegs.GetArrayLength() == 2, "第二章 2 段");
        Expect(chSegs[0].GetProperty("i").GetInt32() == 3, "第二章首段 i=3");
    }

    private static async Task TestMediaAsync(HttpClient http, FakeCatalog catalog)
    {
        var res = await http.GetAsync($"/media/{BookId}/0.mp3");
        Expect(res.StatusCode == HttpStatusCode.OK, "GET media 200");
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Expect(bytes.Length == 1000, "media 1000 字节");
        Expect(res.Headers.AcceptRanges.Contains("bytes"), "Accept-Ranges: bytes");
        Expect(catalog.AudioCalls >= 1, "EnsureSegmentAudio 被调用");

        // 手机端 ?speed= 要透传成合成语速（TTS 读慢），不能退化成播放端变速
        var slow = await http.GetAsync($"/media/{BookId}/0.mp3?speed=0.9");
        Expect(slow.StatusCode == HttpStatusCode.OK, "GET media?speed 200");
        Expect(catalog.LastSpeechSpeed.HasValue && Math.Abs(catalog.LastSpeechSpeed.Value - 0.9) < 1e-9,
            "speed 查询参数透传");

        var noSpeed = await http.GetAsync($"/media/{BookId}/0.mp3");
        Expect(noSpeed.StatusCode == HttpStatusCode.OK, "GET media 无 speed 200");
        Expect(!catalog.LastSpeechSpeed.HasValue, "无 speed 时用服务默认语速");
    }

    private static async Task TestRangeAsync(HttpClient http)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/media/{BookId}/0.mp3");
        request.Headers.Range = new RangeHeaderValue(10, 19);
        var res = await http.SendAsync(request);
        Expect(res.StatusCode == HttpStatusCode.PartialContent, "Range 206");
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Expect(bytes.Length == 10, "Range 10 字节");
        Expect(res.Content.Headers.ContentRange?.ToString() == "bytes 10-19/1000", "Content-Range 10-19/1000");

        using var suffixReq = new HttpRequestMessage(HttpMethod.Get, $"/media/{BookId}/0.mp3");
        suffixReq.Headers.TryAddWithoutValidation("Range", "bytes=-20");
        var suffixRes = await http.SendAsync(suffixReq);
        var suffixBytes = await suffixRes.Content.ReadAsByteArrayAsync();
        Expect(suffixRes.StatusCode == HttpStatusCode.PartialContent && suffixBytes.Length == 20, "suffix Range 20 字节");

        using var openReq = new HttpRequestMessage(HttpMethod.Get, $"/media/{BookId}/0.mp3");
        openReq.Headers.TryAddWithoutValidation("Range", "bytes=990-");
        var openRes = await http.SendAsync(openReq);
        Expect((await openRes.Content.ReadAsByteArrayAsync()).Length == 10, "open Range 990- 10 字节");

        using var badReq = new HttpRequestMessage(HttpMethod.Get, $"/media/{BookId}/0.mp3");
        badReq.Headers.TryAddWithoutValidation("Range", "bytes=2000-");
        var badRes = await http.SendAsync(badReq);
        Expect(badRes.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable, "越界 Range 416");
        Expect(badRes.Content.Headers.ContentRange?.ToString() == "bytes */1000", "416 Content-Range");
    }

    private static async Task TestHeadAsync(HttpClient http)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, $"/media/{BookId}/0.mp3");
        var res = await http.SendAsync(request);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Expect(res.StatusCode == HttpStatusCode.OK, "HEAD media 200");
        Expect(bytes.Length == 0, "HEAD 无 body");
        Expect(res.Content.Headers.ContentLength == 1000, "HEAD Content-Length=1000");
    }

    private static async Task TestProgressAsync(HttpClient http, FakeCatalog catalog)
    {
        var body = JsonSerializer.Serialize(new
        {
            bookId = BookId,
            segmentIndex = 2,
            chapterIndex = 0,
            positionSeconds = 12.5,
            speed = 1.5
        });
        var res = await http.PostAsync("/api/progress", new StringContent(body, Encoding.UTF8, "application/json"));
        Expect(res.StatusCode == HttpStatusCode.OK, "POST /api/progress 200");
        var saved = catalog.Progress;
        Expect(saved is not null && saved.SegmentIndex == 2, "断点 segmentIndex=2");
        Expect(saved is not null && Math.Abs(saved.PositionSeconds - 12.5) < 0.001, "断点 position=12.5");
        Expect(saved is not null && Math.Abs(saved.Speed - 1.5) < 0.001, "断点 speed=1.5");

        var getRes = await http.GetAsync($"/api/progress?bookId={BookId}");
        using var doc = JsonDocument.Parse(await getRes.Content.ReadAsStringAsync());
        Expect(doc.RootElement.GetProperty("progress").GetProperty("segmentIndex").GetInt32() == 2, "GET progress 返回断点");

        var badBody = JsonSerializer.Serialize(new { bookId = BookId, segmentIndex = 99, chapterIndex = 0, positionSeconds = 0, speed = 1 });
        var badRes = await http.PostAsync("/api/progress", new StringContent(badBody, Encoding.UTF8, "application/json"));
        Expect(badRes.StatusCode == HttpStatusCode.BadRequest, "越界 segment 400");
    }

    private static async Task TestPageAsync(HttpClient http)
    {
        var res = await http.GetAsync("/");
        var html = await res.Content.ReadAsStringAsync();
        Expect(res.StatusCode == HttpStatusCode.OK && html.Contains("<audio", StringComparison.Ordinal), "首页含 audio");
        Expect(html.Contains("/media/", StringComparison.Ordinal), "首页含 /media/");
        var alias = await http.GetAsync("/reader");
        Expect(alias.StatusCode == HttpStatusCode.OK && (await alias.Content.ReadAsStringAsync()).Contains("<audio", StringComparison.Ordinal), "/reader 别名");
    }

    private static async Task TestNotFoundAsync(HttpClient http)
    {
        var res = await http.GetAsync("/nope");
        Expect(res.StatusCode == HttpStatusCode.NotFound, "未知路径 404");
        var badBook = await http.GetAsync("/api/books/zzzz");
        Expect(badBook.StatusCode == HttpStatusCode.NotFound, "非法 bookId 404");
    }

    private static async Task TestConcurrentRangeAsync(HttpClient http)
    {
        var tasks = new List<Task<HttpResponseMessage>>();
        for (var i = 0; i < 8; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/media/{BookId}/0.mp3");
            request.Headers.Range = new RangeHeaderValue(i * 100, i * 100 + 9);
            tasks.Add(http.SendAsync(request));
        }

        var responses = await Task.WhenAll(tasks);
        Expect(responses.All(response => response.StatusCode == HttpStatusCode.PartialContent), "8 路并发 Range 全部 206");
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private static void Expect(bool condition, string name)
    {
        if (condition)
        {
            _pass++;
            Console.WriteLine("PASS " + name);
            return;
        }

        _fail++;
        Console.WriteLine("FAIL " + name);
    }

    private sealed class FakeCatalog : IReadingCatalog
    {
        private readonly string _audioDirectory;
        private readonly Book _book;
        private readonly List<Segment> _segments;

        public FakeCatalog(string tempRoot)
        {
            _audioDirectory = Path.Combine(tempRoot, "audio");
            Directory.CreateDirectory(_audioDirectory);
            var chapters = new List<Chapter>
            {
                new() { Index = 0, Title = "第一章", Text = "第一章正文" },
                new() { Index = 1, Title = "第二章", Text = "第二章正文" }
            };
            _segments = new List<Segment>();
            for (var i = 0; i < 5; i++)
            {
                _segments.Add(new Segment
                {
                    ChapterIndex = i < 3 ? 0 : 1,
                    SegmentIndex = i,
                    CharOffset = i * 10,
                    Text = "第 " + i + " 段测试文本",
                    ChapterTitle = i < 3 ? "第一章" : "第二章"
                });
            }

            _book = new Book
            {
                Id = BookId,
                Title = "测试书",
                SourcePath = "C:\\book.txt",
                Format = "txt",
                SourceLength = 12345,
                Chapters = chapters,
                SegmentCount = _segments.Count
            };
        }

        public ReadingProgress? Progress { get; private set; }

        public int AudioCalls { get; private set; }

        public double? LastSpeechSpeed { get; private set; }

        public IReadOnlyList<BookInfo> ListBooks() => new[]
        {
            new BookInfo
            {
                Id = BookId,
                Title = "测试书",
                Format = "txt",
                SourcePath = "C:\\book.txt",
                SourceLength = 12345,
                ChapterCount = 2,
                SegmentCount = 5
            },
            new BookInfo
            {
                Id = "bbbbbbbbbbbbbbbbbbbbbbbb",
                Title = "第二本",
                Format = "epub",
                SourcePath = "C:\\book2.epub",
                SourceLength = 222,
                ChapterCount = 1,
                SegmentCount = 1
            }
        };

        public Task<Book> GetBookAsync(string bookId, CancellationToken cancellationToken = default)
        {
            if (bookId == BookId)
            {
                return Task.FromResult(_book);
            }

            throw new FileNotFoundException("book not found", bookId);
        }

        public IReadOnlyList<Segment> GetSegments(string bookId)
        {
            if (bookId != BookId)
            {
                throw new FileNotFoundException("book not found", bookId);
            }

            return _segments;
        }

        public ReadingProgress? GetProgress(string bookId) => bookId == BookId ? Progress : null;

        public void SaveProgress(ReadingProgress progress)
        {
            if (progress.BookId != BookId)
            {
                throw new InvalidOperationException("unexpected book");
            }

            Progress = progress;
        }

        public Task<string> EnsureSegmentAudioAsync(
            string bookId,
            int segmentIndex,
            double? speechSpeed = null,
            CancellationToken cancellationToken = default)
        {
            LastSpeechSpeed = speechSpeed;
            if (bookId != BookId || segmentIndex < 0 || segmentIndex >= _segments.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentIndex));
            }

            AudioCalls++;
            var path = Path.Combine(_audioDirectory, $"{BookId}-{segmentIndex}.mp3");
            if (!File.Exists(path))
            {
                var bytes = new byte[1000];
                for (var i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = (byte)(i % 251);
                }

                File.WriteAllBytes(path, bytes);
            }

            return Task.FromResult(path);
        }
    }
}
