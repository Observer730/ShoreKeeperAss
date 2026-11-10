using System.Diagnostics;
using System.IO;
using AIAssistant.Host.Wpf.Reading;

namespace Reading.LongRun;

/// <summary>
/// M2 长跑验收工具：用预置缓存（不会调用 TTS）连续播放 N 个 10 秒音频段，
/// 验证 ReadingQueue + NAudioReadingSink 能连续跑满指定分钟数。
/// 用法：dotnet run --project Reading.LongRun -- --mp3 <10s.mp3> --segments 125 --minutes 20
/// </summary>
internal static class Program
{
    private const string BookId = "cccccccccccccccccccccccc";
    private const string VoiceId = "longrun";

    public static async Task<int> Main(string[] args)
    {
        var mp3 = ReadArg(args, "--mp3") ?? throw new ArgumentException("--mp3 is required");
        var segments = int.TryParse(ReadArg(args, "--segments"), out var s) ? s : 125;
        var minutes = double.TryParse(ReadArg(args, "--minutes"), out var m) ? m : 20.0;
        if (!File.Exists(mp3))
        {
            throw new FileNotFoundException("mp3 not found", mp3);
        }

        var root = Path.Combine(Path.GetTempPath(), "reading-longrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cache = new TtsCache(Path.Combine(root, "TTS"));
        var store = new ReadingProgressStore(Path.Combine(root, "books"));

        var chapters = new List<Chapter>
        {
            new() { Index = 0, Title = "长跑", Text = "长跑测试" }
        };
        var segmentList = new List<Segment>(segments);
        for (var i = 0; i < segments; i++)
        {
            var text = "长跑第" + i + "段";
            segmentList.Add(new Segment
            {
                ChapterIndex = 0,
                SegmentIndex = i,
                CharOffset = i * 5,
                Text = text,
                ChapterTitle = "长跑"
            });

            var path = cache.GetCachePath(BookId, i);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(mp3, path, overwrite: true);
            File.WriteAllText(Path.ChangeExtension(path, ".key"), TtsCache.ComputeCacheKey(BookId, i, VoiceId, text));
        }

        var book = new Book
        {
            Id = BookId,
            Title = "长跑测试",
            SourcePath = mp3,
            Format = "mp3",
            SourceLength = new FileInfo(mp3).Length,
            Chapters = chapters,
            SegmentCount = segments
        };

        using var sink = new NAudioReadingSink();
        using var queue = new ReadingQueue(
            book,
            segmentList,
            cache,
            (_, _, _) => throw new InvalidOperationException("cache miss: prefetch/synth should not run"),
            sink,
            store,
            VoiceId);

        queue.Log += message => Console.WriteLine("[log] " + message);
        queue.CurrentSegmentChanged += (_, segment) =>
            Console.WriteLine($"[seg] {segment.SegmentIndex} at {DateTime.Now:HH:mm:ss.fff}");
        queue.StateChanged += (_, _) =>
            Console.WriteLine($"[state] idx={queue.CurrentIndex} playing={queue.IsPlaying}");

        var started = Stopwatch.StartNew();
        await queue.PlayAsync(0);
        Console.WriteLine($"[start] segments={segments} minutes={minutes} mp3={mp3}");

        var target = TimeSpan.FromMinutes(minutes);
        var hardStop = target + TimeSpan.FromMinutes(1);
        var lastIndex = -1;
        while (started.Elapsed < hardStop)
        {
            await Task.Delay(1000);
            if (queue.CurrentIndex != lastIndex)
            {
                lastIndex = queue.CurrentIndex;
                if (lastIndex % 10 == 0)
                {
                    Console.WriteLine($"[progress] idx={lastIndex} elapsed={started.Elapsed:hh\\:mm\\:ss}");
                }
            }

            if (!queue.IsPlaying && queue.CurrentIndex >= segments - 1 && started.Elapsed > TimeSpan.FromSeconds(3))
            {
                break;
            }
        }

        queue.SaveProgress();
        var done = queue.CurrentIndex >= segments - 1;
        Console.WriteLine($"[done] elapsed={started.Elapsed:hh\\:mm\\:ss} idx={queue.CurrentIndex}/{segments} playing={queue.IsPlaying} complete={done}");
        try { Directory.Delete(root, recursive: true); } catch { }
        return done ? 0 : 1;
    }

    private static string? ReadArg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
