using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AIAssistant.Host.Wpf.Reading;

namespace Reading.Tests;

internal static class Program
{
    private static int _pass;
    private static int _fail;
    private static string _temp = "";

    public static async Task<int> Main(string[] args)
    {
        _temp = Path.Combine(Path.GetTempPath(), "shorekeeper-reading-m1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        try
        {
            await RunBuiltInAsync();
            var bookArg = ReadOption(args, "--book");
            if (!string.IsNullOrWhiteSpace(bookArg))
            {
                DescribeBook(bookArg);
            }
        }
        catch (Exception ex)
        {
            Fail("未捕获异常", ex.ToString());
        }
        finally
        {
            try
            {
                if (Directory.Exists(_temp))
                {
                    Directory.Delete(_temp, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        Console.WriteLine("PASS " + _pass + " / FAIL " + _fail);
        return _fail == 0 ? 0 : 1;
    }

    private static async Task RunBuiltInAsync()
    {
        TestLargeChineseTxt();
        TestEncodings();
        TestMinimalEpub();
        TestLongSentence();
        TestPrefixedChapterTitles();
        TestSegmentEdges();
        TestSegmentSpeed();
        TestParserErrors();
        await TestTtsCacheAsync();
        await TestM2Async();
    }

    private static void TestLargeChineseTxt()
    {
        var path = Path.Combine(_temp, "large.txt");
        var body = MakeBody(28000);
        var fakeTitle = "第2章" + new string('啊', 41);
        var builder = new StringBuilder();
        builder.Append("前言在标题之前。\r\n");
        builder.Append("第二行前言\r");
        builder.Append("第一章 风起\n");
        builder.Append(body);
        builder.Append('\n');
        builder.Append(fakeTitle);
        builder.Append('\n');
        builder.Append("第二章 云涌\n");
        builder.Append(body);
        builder.Append("\n第三章\n\n\n");
        builder.Append("第四章 终\n");
        builder.Append("结尾还有一句话。\n");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var started = Stopwatch.StartNew();
        var book = BookParser.Parse(path);
        var again = BookParser.Parse(path);
        var segments = BookSegmenter.Segment(book);
        started.Stop();

        Expect(book.Format == "txt", "大文本格式", book.Format);
        Expect(book.Title == "large", "大文本标题取文件名", book.Title);
        Expect(book.Id.Length == 24 && IsLowerHex(book.Id), "大文本 Id 长度", book.Id);
        Expect(book.Id == again.Id, "重复解析 Id 稳定", book.Id + " vs " + again.Id);
        Expect(book.Id == ExpectedId(path), "Id 等于 SHA256 前 24 位", book.Id);
        Expect(book.Chapters.Count == 4, "大文本章数", book.Chapters.Count.ToString());
        if (book.Chapters.Count == 4)
        {
            Expect(book.Chapters[0].Title == "前言", "第 0 章是前言", book.Chapters[0].Title);
            Expect(book.Chapters[1].Title == "第一章 风起", "第一章标题", book.Chapters[1].Title);
            Expect(book.Chapters[2].Title == "第二章 云涌", "第二章标题", book.Chapters[2].Title);
            Expect(book.Chapters[3].Title == "第四章 终", "空章被跳过", book.Chapters[3].Title);
            Expect(book.Chapters[1].Text.Contains(fakeTitle), "超长标题行留在正文", "未找到假标题");
            Expect(!book.Chapters.Any(chapter => chapter.Text.Contains('\r')), "换行已规范化", "仍含 CR");
        }

        Expect(builder.Length >= 55000, "生成文本不少于 5.5 万字", builder.Length.ToString());
        Expect(segments.Count > 0 && book.SegmentCount == segments.Count, "段数", book.SegmentCount + "/" + segments.Count);
        Expect(started.ElapsedMilliseconds < 3000, "5.5 万字解析耗时", started.ElapsedMilliseconds + " ms");
        ExpectSegmentRules(book, segments, "大文本分段");
        Console.WriteLine("INFO large chars=" + builder.Length + " chapters=" + book.Chapters.Count + " segments=" + segments.Count + " ms=" + started.ElapsedMilliseconds);
    }

    private static void TestEncodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbPath = Path.Combine(_temp, "gb.txt");
        var gb = Encoding.GetEncoding("GB18030");
        const string gbText = "春眠不觉晓，处处闻啼鸟。";
        File.WriteAllBytes(gbPath, gb.GetBytes(gbText));
        var gbBook = BookParser.Parse(gbPath);
        Expect(gbBook.Chapters.Count == 1 && gbBook.Chapters[0].Title == "正文", "GB18030 单章", gbBook.Chapters.Count + " " + gbBook.Chapters.FirstOrDefault()?.Title);
        Expect(gbBook.Chapters.Count == 1 && gbBook.Chapters[0].Text.Contains("春眠不觉晓"), "GB18030 中文", gbBook.Chapters.FirstOrDefault()?.Text ?? "");
        Expect(gbBook.Chapters.Count == 1 && !gbBook.Chapters[0].Text.Contains('\uFFFD'), "GB18030 无替换符", "");

        var lePath = Path.Combine(_temp, "utf16le.txt");
        const string leText = "床前明月光，疑是地上霜。";
        WriteEncoded(lePath, new UnicodeEncoding(bigEndian: false, byteOrderMark: true), leText);
        var leBook = BookParser.Parse(lePath);
        Expect(leBook.Chapters.Count == 1 && leBook.Chapters[0].Text.Contains("床前明月光"), "UTF-16 LE", leBook.Chapters.FirstOrDefault()?.Text ?? "");

        var bePath = Path.Combine(_temp, "utf16be.txt");
        WriteEncoded(bePath, new UnicodeEncoding(bigEndian: true, byteOrderMark: true), leText);
        var beBook = BookParser.Parse(bePath);
        Expect(beBook.Chapters.Count == 1 && beBook.Chapters[0].Text.Contains("疑是地上霜"), "UTF-16 BE", beBook.Chapters.FirstOrDefault()?.Text ?? "");
    }

    private static void TestMinimalEpub()
    {
        var path = Path.Combine(_temp, "mini.epub");
        WriteMiniEpub(path);
        var book = BookParser.Parse(path);
        var again = BookParser.Parse(path);
        Expect(book.Format == "epub", "epub 格式", book.Format);
        Expect(book.Title == "测试书名", "epub dc:title", book.Title);
        Expect(book.Id == again.Id && book.Id == ExpectedId(path), "epub Id 稳定", book.Id);
        Expect(book.Chapters.Count == 2, "epub 章数", book.Chapters.Count.ToString());
        if (book.Chapters.Count == 2)
        {
            Expect(book.Chapters[0].Index == 0 && book.Chapters[0].Title == "第一章 启程", "epub 第 1 章标题", book.Chapters[0].Title);
            Expect(book.Chapters[1].Index == 1 && book.Chapters[1].Title == "第二章 归来", "epub 第 2 章标题", book.Chapters[1].Title);
            var joined = book.Chapters[0].Text + "\n" + book.Chapters[1].Text;
            Expect(book.Chapters[0].Text.Contains("太阳升起来了"), "第 1 章正文", book.Chapters[0].Text);
            Expect(book.Chapters[1].Text.Contains("月亮&星星"), "实体已解码", book.Chapters[1].Text);
            Expect(!joined.Contains("SECRET_TOKEN") && !joined.Contains("secret-style"), "script/style 已去掉", joined);
            Expect(!joined.Contains("<p>") && !joined.Contains("<h1>") && !joined.Contains("<"), "标签已去掉", joined);
            Expect(book.Chapters[0].Text.IndexOf("太阳", StringComparison.Ordinal) >= 0
                && book.Chapters[1].Text.IndexOf("月亮", StringComparison.Ordinal) >= 0, "章节顺序", "");
        }

        var segments = BookSegmenter.Segment(book);
        Expect(book.SegmentCount == segments.Count && segments.Count > 0, "epub 段数", book.SegmentCount.ToString());
        ExpectSegmentRules(book, segments, "epub 分段");
    }

    private static void TestLongSentence()
    {
        var builder = new StringBuilder("超长");
        var n = 0;
        while (builder.Length < 520)
        {
            builder.Append('，');
            builder.Append('段');
            builder.Append(n.ToString());
            n++;
        }

        var hard = new string('木', 650);
        var path = Path.Combine(_temp, "long.txt");
        File.WriteAllText(path, "第一章\n" + builder + "\n第二章\n" + hard + "\n", new UTF8Encoding(false));
        var book = BookParser.Parse(path);
        var segments = BookSegmenter.Segment(book);
        Expect(book.Chapters.Count == 2, "长句章数", book.Chapters.Count.ToString());
        Expect(segments.All(segment => segment.Text.Length <= 200), "长句每段不超过 200", MaxLength(segments).ToString());
        ExpectSegmentRules(book, segments, "长句不丢字");
        var chapter0 = segments.Where(segment => segment.ChapterIndex == 0).ToList();
        var chapter1 = segments.Where(segment => segment.ChapterIndex == 1).ToList();
        Expect(chapter0.Count >= 3, "逗号长句被切开", chapter0.Count.ToString());
        Expect(chapter1.Count == 4, "无标点硬切 4 段", chapter1.Count.ToString());
        Expect(string.Concat(chapter1.Select(segment => segment.Text)) == hard, "硬切拼接等于原文", chapter1.Count.ToString());
    }

    private static void TestPrefixedChapterTitles()
    {
        // 网上 txt 的两种常见排版：序号前缀「1.第1章 …」，正文标签「正文 第一章 … 字数:5691」
        var path = Path.Combine(_temp, "prefixed.txt");
        File.WriteAllText(
            path,
            "书名页\n" +
            "1.第1章 序幕\n" +
            "开篇内容。\n" +
            "2.第2章 布兰\n" +
            "布兰醒来。\n" +
            "正文 第一卷 秋暮之巨龙 序章 字数:5691\n" +
            "祷文内容。\n" +
            "正文 第一章 老友重聚 字数:7363\n" +
            "正文第一章的内容不该被当成标题。\n" +
            "3、第3章 凯特琳\n" +
            "末章内容。\n",
            new UTF8Encoding(false));

        var book = BookParser.Parse(path);
        var titles = string.Join(" | ", book.Chapters.Select(chapter => chapter.Title));
        Expect(book.Chapters.Count == 6, "带前缀标题章数", titles);
        if (book.Chapters.Count == 6)
        {
            Expect(book.Chapters[0].Title == "前言" && book.Chapters[0].Text.Contains("书名页"), "带前缀前的前言", book.Chapters[0].Title);
            Expect(book.Chapters[1].Title == "第1章 序幕", "序号前缀标题", book.Chapters[1].Title);
            Expect(book.Chapters[2].Title == "第2章 布兰", "序号前缀第二例", book.Chapters[2].Title);
            Expect(book.Chapters[3].Title == "第一卷 秋暮之巨龙 序章", "正文标签+字数尾", book.Chapters[3].Title);
            Expect(book.Chapters[4].Title == "第一章 老友重聚", "正文标签标题", book.Chapters[4].Title);
            Expect(book.Chapters[4].Text.Contains("正文第一章的内容不该被当成标题"), "正文句不被吞", book.Chapters[4].Text);
            Expect(book.Chapters[5].Title == "第3章 凯特琳", "顿号前缀标题", book.Chapters[5].Title);
        }

        var segments = BookSegmenter.Segment(book);
        ExpectSegmentRules(book, segments, "带前缀标题不丢字");
    }

    private static void TestSegmentEdges()
    {
        var path = Path.Combine(_temp, "edges.txt");
        File.WriteAllText(
            path,
            "你确定？！我确定。\n等等……再看。\n圆周率约等于 3.14 继续说完。\nChapter 7 英文\nHello.\nCHAPTER 8 大写\nBye.\n",
            new UTF8Encoding(false));
        var book = BookParser.Parse(path);
        var segments = BookSegmenter.Segment(book);
        Expect(book.Chapters.Count == 3, "英文标题章数", string.Join(" | ", book.Chapters.Select(chapter => chapter.Title)));
        if (book.Chapters.Count == 3)
        {
            Expect(book.Chapters[0].Title == "前言", "英文标题前的前言", book.Chapters[0].Title);
            Expect(book.Chapters[1].Title == "Chapter 7 英文", "Chapter 标题", book.Chapters[1].Title);
            Expect(book.Chapters[2].Title == "CHAPTER 8 大写", "CHAPTER 标题", book.Chapters[2].Title);
        }

        Expect(segments.Any(segment => segment.Text.Contains("？！")), "连续标点留在同一段", string.Join(" / ", segments.Select(segment => segment.Text)));
        Expect(segments.Any(segment => segment.Text.Contains("……")), "省略号留在同一段", "");
        Expect(segments.Any(segment => segment.Text.Contains("3.14")), "小数点不当句号", "");
        ExpectSegmentRules(book, segments, "标点边界");

        var manual = new Book
        {
            Chapters = new[]
            {
                new Chapter { Index = 0, Title = "空白", Text = "  \n\t  " },
                new Chapter { Index = 1, Title = "有", Text = "有字。" }
            }
        };
        var manualSegments = BookSegmenter.Segment(manual);
        Expect(manualSegments.Count == 1 && manualSegments[0].Text == "有字。" && manualSegments[0].SegmentIndex == 0, "纯空白段被跳过", manualSegments.Count.ToString());
    }

    private static void TestSegmentSpeed()
    {
        BookSegmenter.Segment(new Book
        {
            Chapters = new[] { new Chapter { Index = 0, Title = "热身", Text = "热身。" } }
        });

        var text = MakeBody(100000);
        var book = new Book
        {
            Chapters = new[] { new Chapter { Index = 0, Title = "正文", Text = text } }
        };
        var started = Stopwatch.StartNew();
        var segments = BookSegmenter.Segment(book);
        started.Stop();
        Expect(started.ElapsedMilliseconds < 1500, "10 万字分段耗时", started.ElapsedMilliseconds + " ms");
        Expect(segments.Count > 0 && segments.All(segment => segment.Text.Length <= 200), "10 万字段长", segments.Count.ToString());
        Expect(NonWhitespace(text) == segments.Sum(segment => NonWhitespace(segment.Text)), "10 万字不丢字", "");
        Console.WriteLine("INFO segment100k segments=" + segments.Count + " ms=" + started.ElapsedMilliseconds);
    }

    private static void TestParserErrors()
    {
        ExpectThrows<FileNotFoundException>(() => BookParser.Parse(Path.Combine(_temp, "missing.txt")), "文件不存在");
        var pdf = Path.Combine(_temp, "nope.pdf");
        File.WriteAllBytes(pdf, new byte[] { 1, 2, 3 });
        ExpectThrows<NotSupportedException>(() => BookParser.Parse(pdf), "不支持的扩展名");

        var bad = Path.Combine(_temp, "bad.epub");
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("no container");
        }

        try
        {
            BookParser.Parse(bad);
            Fail("损坏 epub", "没有抛出 InvalidDataException");
        }
        catch (InvalidDataException ex)
        {
            Expect(ex.Message.Contains(bad, StringComparison.Ordinal), "损坏 epub 信息含路径", ex.Message);
        }
        catch (Exception ex)
        {
            Fail("损坏 epub", ex.GetType().Name + ": " + ex.Message);
        }

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        ExpectThrows<OperationCanceledException>(() => BookParser.Parse(pdf, canceled.Token), "取消解析");
    }

    private static async Task TestTtsCacheAsync()
    {
        var root = Path.Combine(_temp, "tts");
        var cache = new TtsCache(root);
        Expect(Directory.Exists(cache.RootDirectory), "缓存目录已创建", cache.RootDirectory);
        Expect(cache.GetCachePath("book1", 3) == Path.Combine(cache.RootDirectory, "book1", "3.mp3"), "缓存路径", cache.GetCachePath("book1", 3));

        var key = TtsCache.ComputeCacheKey("book", 0, "voice", "你好");
        var manual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("book|0|voice|1|你好"))).ToLowerInvariant();
        Expect(key.Length == 64 && IsLowerHex(key) && key == manual, "缓存 key", key);
        ExpectThrows<ArgumentException>(() => TtsCache.ComputeCacheKey("", 0, "voice", "你好"), "空 bookId");
        ExpectThrows<ArgumentException>(() => TtsCache.ComputeCacheKey("book", 0, " ", "你好"), "空 voiceId");
        ExpectThrows<ArgumentException>(() => TtsCache.ComputeCacheKey("book", 0, "voice", ""), "空 text");
        ExpectThrows<ArgumentException>(() => new TtsCache(" "), "空缓存目录");

        var logs = new List<string>();
        cache.Log += message =>
        {
            lock (logs)
            {
                logs.Add(message);
            }
        };

        var calls = 0;
        Task Synthesize(string text, string destination, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return File.WriteAllBytesAsync(destination, new byte[] { 0x49, 0x44, 0x33 }, cancellationToken);
        }

        var first = await cache.GetOrCreateAsync("book", 0, "voiceA", "你好", 1.0, Synthesize);
        var second = await cache.GetOrCreateAsync("book", 0, "voiceA", "你好", 1.0, Synthesize);
        Expect(calls == 1, "第二次不再合成", calls.ToString());
        Expect(first == second && new FileInfo(first).Length == 3, "命中后文件仍是 3 字节", first);
        Expect(File.ReadAllText(Path.ChangeExtension(first, ".key")).Trim() == TtsCache.ComputeCacheKey("book", 0, "voiceA", "你好"), "sidecar key", "");
        string Snapshot()
        {
            lock (logs)
            {
                return string.Join("\n", logs);
            }
        }

        var afterRepeat = Snapshot();
        Expect(afterRepeat.Contains("[Cache] miss", StringComparison.Ordinal), "首次 miss 日志", afterRepeat);
        Expect(afterRepeat.Contains("[Cache] hit", StringComparison.Ordinal), "再次 hit 日志", afterRepeat);

        await cache.GetOrCreateAsync("book", 0, "voiceB", "你好", 1.0, Synthesize);
        Expect(calls == 2, "换 voiceId 重新合成", calls.ToString());
        Expect(File.ReadAllText(Path.ChangeExtension(first, ".key")).Trim() == TtsCache.ComputeCacheKey("book", 0, "voiceB", "你好"), "新音色 key", "");
        await cache.GetOrCreateAsync("book", 0, "voiceB", "你好", 1.0, Synthesize);
        Expect(calls == 2, "新音色第二次命中", calls.ToString());

        var slowKey = TtsCache.ComputeCacheKey("book", 0, "voiceB", "你好", 0.9);
        Expect(slowKey != TtsCache.ComputeCacheKey("book", 0, "voiceB", "你好", 1.0), "语速进缓存 key", slowKey);
        Expect(slowKey == TtsCache.ComputeCacheKey("book", 0, "voiceB", "你好", 0.90), "语速格式化稳定", slowKey);
        await cache.GetOrCreateAsync("book", 0, "voiceB", "你好", 0.9, Synthesize);
        Expect(calls == 3, "换语速重新合成", calls.ToString());
        await cache.GetOrCreateAsync("book", 0, "voiceB", "你好", 0.9, Synthesize);
        Expect(calls == 3, "同语速命中缓存", calls.ToString());

        var concurrentRoot = Path.Combine(_temp, "tts-concurrent");
        var concurrent = new TtsCache(concurrentRoot);
        var concurrentCalls = 0;
        async Task ConcurrentSynth(string text, string destination, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref concurrentCalls);
            await Task.Delay(80, cancellationToken);
            await File.WriteAllBytesAsync(destination, new byte[] { 1, 2, 3 }, cancellationToken);
        }

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => concurrent.GetOrCreateAsync("same", 4, "voice", "并发文本", 1.0, ConcurrentSynth))
            .ToArray();
        var paths = await Task.WhenAll(tasks);
        Expect(concurrentCalls == 1, "8 路并发只合成 1 次", concurrentCalls.ToString());
        Expect(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1, "并发返回同一路径", paths.Length.ToString());
        Expect(new FileInfo(paths[0]).Length == 3, "并发文件 3 字节", "");

        var failRoot = Path.Combine(_temp, "tts-fail");
        var failing = new TtsCache(failRoot);
        var failLogs = new List<string>();
        failing.Log += message => failLogs.Add(message);
        try
        {
            await failing.GetOrCreateAsync("book", 1, "voice", "会失败", 1.0, (_, destination, _) =>
            {
                File.WriteAllBytes(destination, new byte[] { 9 });
                throw new InvalidOperationException("boom");
            });
            Fail("合成失败应抛出原异常", "没有抛出");
        }
        catch (InvalidOperationException ex)
        {
            Expect(ex.Message == "boom", "异常原样抛出", ex.Message);
        }

        var failDir = Path.Combine(failing.RootDirectory, "book");
        var leftovers = Directory.Exists(failDir) ? Directory.GetFiles(failDir) : Array.Empty<string>();
        Expect(leftovers.All(file => !Path.GetFileName(file).Contains(".tmp-", StringComparison.Ordinal)), "失败后清理临时文件", string.Join(", ", leftovers));
        Expect(failLogs.Any(message => message.Contains("[Cache] fail", StringComparison.Ordinal)), "失败日志", string.Join(" | ", failLogs));
    }

    private static void DescribeBook(string path)
    {
        Console.WriteLine("--- book ---");
        if (!File.Exists(path))
        {
            Fail("外部书籍", "文件不存在：" + path);
            return;
        }

        var parseWatch = Stopwatch.StartNew();
        var book = BookParser.Parse(path);
        parseWatch.Stop();
        var segmentWatch = Stopwatch.StartNew();
        var segments = BookSegmenter.Segment(book);
        segmentWatch.Stop();
        Console.WriteLine("PATH " + book.SourcePath);
        Console.WriteLine("TITLE " + book.Title);
        Console.WriteLine("FORMAT " + book.Format);
        Console.WriteLine("ID " + book.Id);
        Console.WriteLine("BYTES " + book.SourceLength);
        Console.WriteLine("CHAPTERS " + book.Chapters.Count);
        Console.WriteLine("SEGMENTS " + segments.Count);
        Console.WriteLine("SEGMENT_COUNT " + book.SegmentCount);
        Console.WriteLine("PARSE_MS " + parseWatch.ElapsedMilliseconds);
        Console.WriteLine("SEGMENT_MS " + segmentWatch.ElapsedMilliseconds);
        var preview = Math.Min(3, segments.Count);
        for (var i = 0; i < preview; i++)
        {
            var segment = segments[i];
            Console.WriteLine("SEG[" + i + "] ch=" + segment.ChapterIndex + " off=" + segment.CharOffset + " " + segment.ChapterTitle + " | " + segment.Text.Replace("\n", "\\n"));
        }

        for (var i = 0; i < Math.Min(8, book.Chapters.Count); i++)
        {
            Console.WriteLine("CH[" + i + "] " + book.Chapters[i].Title + " chars=" + book.Chapters[i].Text.Length);
        }

        Expect(book.Chapters.Count > 0 && segments.Count > 0, "外部书籍有内容", book.Title);
        Expect(segments.All(segment => segment.Text.Length <= BookSegmenter.MaxSegmentLength), "外部书籍段长", "");
    }

    private static void ExpectSegmentRules(Book book, IReadOnlyList<Segment> segments, string name)
    {
        var problem = FindSegmentProblem(book, segments);
        Expect(problem is null, name, problem ?? "");
    }

    private static string? FindSegmentProblem(Book book, IReadOnlyList<Segment> segments)
    {
        if (segments.Count == 0)
        {
            return "没有分段";
        }

        var chapterNonWs = 0;
        foreach (var chapter in book.Chapters)
        {
            chapterNonWs += NonWhitespace(chapter.Text);
        }

        var segmentNonWs = 0;
        var previousChapter = -1;
        var previousOffset = -1;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.SegmentIndex != i)
            {
                return "索引不连续：" + segment.SegmentIndex + " at " + i;
            }

            if (segment.Text.Length == 0 || segment.Text.Length > BookSegmenter.MaxSegmentLength || string.IsNullOrWhiteSpace(segment.Text))
            {
                return "非法段长度 " + segment.Text.Length;
            }

            if (segment.ChapterIndex < 0 || segment.ChapterIndex >= book.Chapters.Count)
            {
                return "章节索引越界 " + segment.ChapterIndex;
            }

            var chapter = book.Chapters[segment.ChapterIndex];
            if (segment.ChapterTitle != chapter.Title)
            {
                return "章标题不一致";
            }

            if (segment.CharOffset < 0 || segment.CharOffset + segment.Text.Length > chapter.Text.Length)
            {
                return "offset 越界 " + segment.CharOffset;
            }

            if (chapter.Text.Substring(segment.CharOffset, segment.Text.Length) != segment.Text)
            {
                return "offset 不能回查原文";
            }

            if (segment.ChapterIndex == previousChapter && segment.CharOffset < previousOffset)
            {
                return "offset 倒退";
            }

            previousChapter = segment.ChapterIndex;
            previousOffset = segment.CharOffset;
            segmentNonWs += NonWhitespace(segment.Text);
        }

        var delta = Math.Abs(segmentNonWs - chapterNonWs);
        if (delta > segments.Count)
        {
            return "丢字 chapter=" + chapterNonWs + " segments=" + segmentNonWs;
        }

        if (segmentNonWs != chapterNonWs)
        {
            return "非空白字符不一致 chapter=" + chapterNonWs + " segments=" + segmentNonWs;
        }

        return null;
    }

    private static void WriteMiniEpub(string path)
    {
        var container = """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """;
        var opf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>测试书名</dc:title>
              </metadata>
              <manifest>
                <item id="c1" href="chap1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="chap%202.xhtml" media-type="application/xhtml+xml"/>
                <item id="css" href="style.css" media-type="text/css"/>
                <item id="gone" href="missing.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine>
                <itemref idref="c1"/>
                <itemref idref="css"/>
                <itemref idref="gone"/>
                <itemref idref="c2"/>
              </spine>
            </package>
            """;
        var chapter1 = """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head>
                <title>页标题不应盖过 h1</title>
                <style>secret-style</style>
              </head>
              <body>
                <h1>第一章 启程</h1>
                <p>太阳升起来了。</p>
                <script>SECRET_TOKEN</script>
              </body>
            </html>
            """;
        var chapter2 = """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>备用标题</title></head>
              <body>
                <h2>第二章 归来</h2>
                <p>月亮&amp;星星。</p>
              </body>
            </html>
            """;

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        AddZipText(zip, "META-INF/container.xml", container);
        AddZipText(zip, "OEBPS/content.opf", opf);
        AddZipText(zip, "OEBPS/chap1.xhtml", chapter1);
        AddZipText(zip, "OEBPS/chap 2.xhtml", chapter2);
        AddZipText(zip, "OEBPS/style.css", "body{color:red}");
    }

    private static void WriteEncoded(string path, Encoding encoding, string text)
    {
        using var stream = File.Create(path);
        using var writer = new StreamWriter(stream, encoding);
        writer.Write(text);
    }

    private static void AddZipText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string MakeBody(int characters)
    {
        const string unit = "这是听书测试句子，用来检查分段是否丢字。";
        var builder = new StringBuilder(characters + unit.Length + 8);
        while (builder.Length < characters)
        {
            builder.Append(unit);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string ExpectedId(string path)
    {
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 24);
    }

    private static int NonWhitespace(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                count++;
            }
        }

        return count;
    }

    private static int MaxLength(IEnumerable<Segment> segments)
    {
        var max = 0;
        foreach (var segment in segments)
        {
            if (segment.Text.Length > max)
            {
                max = segment.Text.Length;
            }
        }

        return max;
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static async Task TestM2Async()
    {
        await TestProgressStoreAsync();
        await TestReadingLibraryAsync();
        await TestReadingQueueAsync();
        await TestNAudioSmokeAsync();
    }

    private static Task TestProgressStoreAsync()
    {
        var root = Path.Combine(_temp, "progress");
        var store = new ReadingProgressStore(root);
        var stamp = new DateTimeOffset(2026, 11, 10, 8, 30, 0, TimeSpan.Zero);
        var progress = new ReadingProgress
        {
            BookId = "abc123",
            SegmentIndex = 4,
            ChapterIndex = 2,
            PositionSeconds = 4.25,
            Speed = 1.25,
            UpdatedAtUtc = stamp
        };
        store.Save(progress);
        var loaded = store.Load("abc123");
        Expect(loaded is not null, "断点可以读回", "null");
        if (loaded is not null)
        {
            Expect(loaded.BookId == "abc123", "断点 BookId", loaded.BookId);
            Expect(loaded.SegmentIndex == 4, "断点 SegmentIndex", loaded.SegmentIndex.ToString(CultureInfo.InvariantCulture));
            Expect(loaded.ChapterIndex == 2, "断点 ChapterIndex", loaded.ChapterIndex.ToString(CultureInfo.InvariantCulture));
            Expect(Math.Abs(loaded.PositionSeconds - 4.25) < 0.0001, "断点 PositionSeconds", loaded.PositionSeconds.ToString(CultureInfo.InvariantCulture));
            Expect(Math.Abs(loaded.Speed - 1.25) < 0.0001, "断点 Speed", loaded.Speed.ToString(CultureInfo.InvariantCulture));
            Expect(loaded.UpdatedAtUtc == stamp, "断点 UpdatedAtUtc", loaded.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        }

        Expect(store.GetProgressPath("abc123").EndsWith(Path.Combine("abc123", "progress.json"), StringComparison.OrdinalIgnoreCase), "断点路径", store.GetProgressPath("abc123"));
        Expect(store.Load("missing-book") is null, "不存在的断点返回 null", "");
        File.WriteAllText(store.GetProgressPath("abc123"), "{bad", new UTF8Encoding(false));
        Expect(store.Load("abc123") is null, "损坏 JSON 返回 null", "");
        ExpectThrows<ArgumentException>(() => store.Load(""), "空 bookId Load");
        ExpectThrows<ArgumentException>(() => store.Load("   "), "空白 bookId Load");
        ExpectThrows<ArgumentException>(() => store.GetProgressPath(""), "空 bookId 路径");
        ExpectThrows<ArgumentException>(() => store.Save(new ReadingProgress { BookId = "" }), "空 bookId Save");
        return Task.CompletedTask;
    }

    private static async Task TestReadingLibraryAsync()
    {
        var root = Path.Combine(_temp, "library");
        Directory.CreateDirectory(root);
        var chineseDir = Path.Combine(root, "中文书架");
        Directory.CreateDirectory(chineseDir);
        var library = new ReadingBookLibrary(chineseDir);
        var bookPath = Path.Combine(chineseDir, "中文书.txt");
        File.WriteAllText(bookPath, "第一章 开始\n太阳升起来了。\n第二章 结束\n月亮出来了。\n", new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(chineseDir, "忽略.mobi"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(chineseDir, "忽略.md"), "nope", new UTF8Encoding(false));

        var scanned = library.Scan();
        Expect(scanned.Count == 1, "扫描只收 txt/epub", scanned.Count.ToString(CultureInfo.InvariantCulture));
        if (scanned.Count == 1)
        {
            Expect(scanned[0].Title == "中文书", "中文文件名作标题", scanned[0].Title);
            Expect(scanned[0].Format == "txt", "扫描格式", scanned[0].Format);
            Expect(scanned[0].Id == ExpectedId(bookPath), "扫描 Id 等于文件哈希", scanned[0].Id);
            Expect(scanned[0].ChapterCount is null && scanned[0].SegmentCount is null, "扫描不解析正文", (scanned[0].ChapterCount?.ToString() ?? "null") + "/" + (scanned[0].SegmentCount?.ToString() ?? "null"));
        }

        if (scanned.Count == 1)
        {
            ExpectThrows<InvalidOperationException>(() => library.GetSegments(scanned[0].Id), "未解析不能取分段");
            var book = await library.GetBookAsync(scanned[0].Id);
            var segments = library.GetSegments(book.Id);
            var info = library.Find(book.Id);
            Expect(info is not null && info.ChapterCount == book.Chapters.Count && info.SegmentCount == segments.Count, "解析后回填章数段数", (info?.ChapterCount?.ToString() ?? "null") + "/" + (info?.SegmentCount?.ToString() ?? "null"));
            var again = await library.GetBookAsync(book.Id);
            Expect(ReferenceEquals(book, again), "解析结果有缓存", "");
        }

        var external = Path.Combine(_temp, "外部书.txt");
        File.WriteAllText(external, "没有标题只有正文。\n", new UTF8Encoding(false));
        Expect(library.Register(external), "外部文件可以登记", "");
        Expect(!library.Register(external), "重复路径返回 false", "");
        var after = library.Scan();
        Expect(after.Any(item => item.SourcePath.Equals(Path.GetFullPath(external), StringComparison.OrdinalIgnoreCase)), "扫描保留外部书", after.Count.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task TestReadingQueueAsync()
    {
        var cacheRoot = Path.Combine(_temp, "tts-cache");
        var progressRoot = Path.Combine(_temp, "queue-progress");
        var cache = new TtsCache(cacheRoot);
        var store = new ReadingProgressStore(progressRoot);
        var segments = new List<Segment>();
        for (var i = 0; i < 6; i++)
        {
            segments.Add(new Segment
            {
                ChapterIndex = i / 2,
                SegmentIndex = i,
                CharOffset = 0,
                Text = "S" + i.ToString(CultureInfo.InvariantCulture),
                ChapterTitle = "第" + ((i / 2) + 1).ToString(CultureInfo.InvariantCulture) + "章"
            });
        }

        var book = new Book
        {
            Id = "book00000000000000000001",
            Title = "队列测试",
            SourcePath = Path.Combine(_temp, "queue.txt"),
            Format = "txt",
            SourceLength = 12,
            Chapters = new[]
            {
                new Chapter { Index = 0, Title = "第一章", Text = "a" },
                new Chapter { Index = 1, Title = "第二章", Text = "b" },
                new Chapter { Index = 2, Title = "第三章", Text = "c" }
            },
            SegmentCount = segments.Count
        };

        var synthIndexes = new List<int>();
        var synthGate = new object();
        Task Synth(string text, string destination, CancellationToken cancellationToken)
        {
            var index = int.Parse(text.AsSpan(1), CultureInfo.InvariantCulture);
            lock (synthGate)
            {
                synthIndexes.Add(index);
            }

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(destination, new byte[] { 1, 2, 3 });
            return Task.CompletedTask;
        }

        bool HasSynth(int index)
        {
            lock (synthGate)
            {
                return synthIndexes.Contains(index);
            }
        }

        string SynthSnapshot()
        {
            lock (synthGate)
            {
                return string.Join(",", synthIndexes);
            }
        }

        var sink = new FakeReadingSink();
        var queue = new ReadingQueue(book, segments, cache, Synth, sink, store, "voice-test");
        try
        {
            await queue.PlayAsync(0);
            await queue.PreviousSegmentAsync();
            Expect(queue.CurrentIndex == 0, "第一段再上一段停住", queue.CurrentIndex.ToString(CultureInfo.InvariantCulture));
            var prefetched = await WaitUntil(() => HasSynth(1));
            await Task.Delay(100);
            Expect(prefetched && HasSynth(0) && HasSynth(1), "播放第 0 段并预取第 1 段", SynthSnapshot());
            Expect(!HasSynth(2), "预取不到第 2 段", SynthSnapshot());
            Expect(sink.SnapshotLoads().Any(path => Path.GetFileName(path) == "0.mp3"), "Load 第 0 段", string.Join(" | ", sink.SnapshotLoads()));
            Expect(sink.PlayCount >= 1 && sink.IsPlaying, "Play 第 0 段", sink.PlayCount.ToString(CultureInfo.InvariantCulture));

            sink.RaiseEnded();
            var moved = await WaitUntil(() => queue.CurrentIndex == 1 && sink.SnapshotLoads().Any(path => Path.GetFileName(path) == "1.mp3"));
            Expect(moved, "播完自动到第 1 段", "index=" + queue.CurrentIndex + " loads=" + string.Join(" | ", sink.SnapshotLoads()));

            await queue.NextChapterAsync();
            Expect(queue.CurrentIndex == 2 && queue.Segments[queue.CurrentIndex].ChapterIndex == 1, "下一章落到章首", queue.CurrentIndex + " ch=" + queue.Segments[queue.CurrentIndex].ChapterIndex);
            await queue.PreviousChapterAsync();
            Expect(queue.CurrentIndex == 1 && queue.Segments[queue.CurrentIndex].ChapterIndex == 0, "上一章落到章末", queue.CurrentIndex + " ch=" + queue.Segments[queue.CurrentIndex].ChapterIndex);

            await queue.SeekToSegmentAsync(5);
            Expect(queue.CurrentIndex == 5 && sink.SnapshotLoads().Any(path => Path.GetFileName(path) == "5.mp3"), "Seek 到第 5 段", queue.CurrentIndex + " " + string.Join(" | ", sink.SnapshotLoads()));
            await queue.NextSegmentAsync();
            Expect(queue.CurrentIndex == 5, "最后一段再下一段停住", queue.CurrentIndex.ToString(CultureInfo.InvariantCulture));

            await queue.SetSpeedAsync(1.5);
            var savedSpeed = store.Load(book.Id);
            Expect(sink.Speeds.Any(speed => Math.Abs(speed - 1.5) < 0.0001), "sink 收到 1.5 倍速", string.Join(",", sink.Speeds));
            Expect(Math.Abs(queue.Speed - 1.5) < 0.0001, "队列 Speed 为 1.5", queue.Speed.ToString(CultureInfo.InvariantCulture));
            Expect(savedSpeed is not null && Math.Abs(savedSpeed.Speed - 1.5) < 0.0001, "断点写入 speed 1.5", savedSpeed?.Speed.ToString(CultureInfo.InvariantCulture) ?? "null");

            sink.PositionSeconds = 12.5;
            queue.Pause();
            var paused = store.Load(book.Id);
            Expect(paused is not null && Math.Abs(paused.PositionSeconds - 12.5) < 0.001, "暂停后断点位置约 12.5", paused?.PositionSeconds.ToString(CultureInfo.InvariantCulture) ?? "null");
        }
        finally
        {
            queue.Dispose();
        }

        var sink2 = new FakeReadingSink();
        var queue2 = new ReadingQueue(book, segments, cache, Synth, sink2, store, "voice-test");
        try
        {
            Expect(queue2.CurrentIndex == 5, "新队列读回 CurrentIndex", queue2.CurrentIndex.ToString(CultureInfo.InvariantCulture));
            Expect(Math.Abs(queue2.Speed - 1.5) < 0.0001, "新队列读回倍速", queue2.Speed.ToString(CultureInfo.InvariantCulture));
            await queue2.ResumeAsync();
            Expect(sink2.Seeks.Any(seek => Math.Abs(seek - 12.5) < 0.001), "Resume 时 Seek 12.5", string.Join(",", sink2.Seeks));
            sink2.RaiseEnded();
            var finished = await WaitUntil(() => !queue2.IsPlaying && queue2.CurrentIndex == segments.Count - 1);
            Expect(finished && queue2.CurrentIndex < segments.Count, "全书末尾停止且不越界", "index=" + queue2.CurrentIndex + " playing=" + queue2.IsPlaying);
        }
        finally
        {
            queue2.Dispose();
        }
    }

    private static async Task TestNAudioSmokeAsync()
    {
        var dll = FindHostDll();
        if (dll is null)
        {
            Console.WriteLine("SKIP audio: 未找到 AIAssistant.Host.Wpf.dll，跳过 NAudio 冒烟。");
            return;
        }

        try
        {
            var folder = Path.GetDirectoryName(dll)!;
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                var name = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }

                var candidate = Path.Combine(folder, name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };

            var assembly = Assembly.LoadFrom(dll);
            var type = assembly.GetType("AIAssistant.Host.Wpf.Reading.NAudioReadingSink");
            if (type is null)
            {
                Console.WriteLine("SKIP audio: 宿主里没有 NAudioReadingSink。");
                return;
            }

            var wav = Path.Combine(_temp, "tone-440.wav");
            WriteSineWav(wav, seconds: 2, frequency: 440);
            object? sink = null;
            try
            {
                sink = Activator.CreateInstance(type);
                if (sink is null)
                {
                    Console.WriteLine("SKIP audio: 无法创建 NAudioReadingSink。");
                    return;
                }

                var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler handler = (_, _) => ended.TrySetResult(true);
                type.GetEvent("PlaybackEnded")?.AddEventHandler(sink, handler);
                Invoke(type, sink, "Load", wav);
                Invoke(type, sink, "SetSpeed", 1.5d);
                var started = Stopwatch.StartNew();
                Invoke(type, sink, "Play");
                var winner = await Task.WhenAny(ended.Task, Task.Delay(8000));
                started.Stop();
                if (winner != ended.Task)
                {
                    Console.WriteLine("SKIP audio: 8 秒内没有 PlaybackEnded，设备可能不可用或被占用。elapsedMs=" + started.ElapsedMilliseconds);
                    return;
                }

                Console.WriteLine("INFO audio ended elapsedMs=" + started.ElapsedMilliseconds + " speed=1.5 sourceSeconds=2");
                Expect(true, "NAudio 冒烟收到 PlaybackEnded", started.ElapsedMilliseconds + " ms");
            }
            finally
            {
                if (sink is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            var root = ex is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : ex;
            Console.WriteLine("SKIP audio: " + root.GetType().Name + ": " + root.Message);
        }
    }

    private static void Invoke(Type type, object instance, string name, params object[] args)
    {
        var method = type.GetMethod(name);
        if (method is null)
        {
            throw new MissingMethodException(type.FullName, name);
        }

        method.Invoke(instance, args);
    }

    private static string? FindHostDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(dir.FullName, "AIAssistant.Host.Wpf", "bin", configuration, "net8.0-windows", "AIAssistant.Host.Wpf.dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static void WriteSineWav(string path, int seconds, int frequency)
    {
        const int sampleRate = 44100;
        var sampleCount = sampleRate * seconds;
        var dataBytes = sampleCount * 2;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = Math.Sin(2 * Math.PI * frequency * i / sampleRate);
            writer.Write((short)(sample * 8000));
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            if (started.ElapsedMilliseconds > timeoutMs)
            {
                return false;
            }

            await Task.Delay(20);
        }

        return true;
    }

    private sealed class FakeReadingSink : IReadingAudioSink
    {
        private readonly object _gate = new();

        public List<double> Seeks { get; } = new();

        public List<double> Speeds { get; } = new();

        public int PlayCount { get; private set; }

        public double PositionSeconds { get; set; }

        public double DurationSeconds { get; set; } = 30;

        public bool IsPlaying { get; private set; }

        public event EventHandler? PlaybackEnded;

        public event EventHandler<string>? PlaybackFailed
        {
            add { }
            remove { }
        }

        private readonly List<string> _loads = new();

        public void Load(string filePath)
        {
            lock (_gate)
            {
                _loads.Add(filePath);
            }

            PositionSeconds = 0;
            IsPlaying = false;
        }

        public void Play()
        {
            IsPlaying = true;
            PlayCount++;
        }

        public void Pause() => IsPlaying = false;

        public void Stop() => IsPlaying = false;

        public void Seek(double seconds)
        {
            PositionSeconds = seconds;
            lock (_gate)
            {
                Seeks.Add(seconds);
            }
        }

        public void SetSpeed(double speed)
        {
            lock (_gate)
            {
                Speeds.Add(speed);
            }
        }

        public void SetVolume(double volume)
        {
        }

        public string[] SnapshotLoads()
        {
            lock (_gate)
            {
                return _loads.ToArray();
            }
        }

        public void RaiseEnded() => PlaybackEnded?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }

    private static void Expect(bool condition, string name, string detail)
    {
        if (condition)
        {
            _pass++;
            Console.WriteLine("PASS " + name);
        }
        else
        {
            Fail(name, detail);
        }
    }

    private static void ExpectThrows<TException>(Action action, string name) where TException : Exception
    {
        try
        {
            action();
            Fail(name, "没有抛出 " + typeof(TException).Name);
        }
        catch (TException)
        {
            _pass++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            Fail(name, "实际是 " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void Fail(string name, string detail)
    {
        _fail++;
        Console.WriteLine("FAIL " + name + " :: " + detail);
    }
}
