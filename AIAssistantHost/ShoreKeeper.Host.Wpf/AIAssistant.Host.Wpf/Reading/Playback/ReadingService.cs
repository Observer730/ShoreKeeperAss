using System.IO;
using AIAssistant.Host.Wpf.MiniMax;

namespace AIAssistant.Host.Wpf.Reading;

/// <summary>
/// 本机听书入口。书库、断点和 TTS 缓存都挂在这里，同一时刻只保留一个播放队列。
/// </summary>
public sealed class ReadingService : IDisposable, IReadingCatalog
{
    private readonly MiniMaxTtsService _tts = new();
    private readonly object _gate = new();
    private ReadingQueue? _queue;
    private NAudioReadingSink? _sink;
    private string _voiceId;
    private double _speechSpeed = 1.0;
    private int _disposed;

    public ReadingService(string projectDirectory, string recordingsDirectory, string voiceId)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            throw new ArgumentException("项目目录不能为空。", nameof(projectDirectory));
        }

        if (string.IsNullOrWhiteSpace(recordingsDirectory))
        {
            throw new ArgumentException("录音目录不能为空。", nameof(recordingsDirectory));
        }

        if (string.IsNullOrWhiteSpace(voiceId))
        {
            throw new ArgumentException("voiceId 不能为空。", nameof(voiceId));
        }

        var project = Path.GetFullPath(projectDirectory);
        Library = new ReadingBookLibrary(Path.Combine(project, "Book"));
        ProgressStore = new ReadingProgressStore(Path.Combine(project, "books"));
        Cache = new TtsCache(Path.Combine(recordingsDirectory, "TTS"));
        _voiceId = voiceId.Trim();
        _tts.VoiceId = _voiceId;
        _tts.OutputDirectory = Cache.RootDirectory;
        Library.Log += OnLog;
        Cache.Log += OnLog;
    }

    public ReadingBookLibrary Library { get; }

    public TtsCache Cache { get; }

    public ReadingProgressStore ProgressStore { get; }

    public string VoiceId
    {
        get => _voiceId;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("voiceId 不能为空。", nameof(value));
            }

            _voiceId = value.Trim();
            _tts.VoiceId = _voiceId;
        }
    }

    /// <summary>
    /// 朗读合成语速（0.5–2.0，默认 1.0）。这是让 TTS 直接读快/读慢，
    /// 不是播放端变速，因此不会有变速产生的电音。语速进缓存 key，换值后相关段会重新合成。
    /// </summary>
    public double SpeechSpeed
    {
        get => _speechSpeed;
        set => _speechSpeed = ClampSpeechSpeed(value);
    }

    public event Action<string>? Log;

    public Task<Book> OpenBookAsync(string bookId, CancellationToken cancellationToken = default)
    {
        return Library.GetBookAsync(bookId, cancellationToken);
    }

    public IReadOnlyList<BookInfo> ListBooks()
    {
        ThrowIfDisposed();
        return Library.Scan();
    }

    public Task<Book> GetBookAsync(string bookId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return OpenBookAsync(bookId, cancellationToken);
    }

    public IReadOnlyList<Segment> GetSegments(string bookId)
    {
        ThrowIfDisposed();
        return Library.GetSegments(bookId);
    }

    public ReadingProgress? GetProgress(string bookId)
    {
        ThrowIfDisposed();
        return ProgressStore.Load(bookId);
    }

    public void SaveProgress(ReadingProgress progress)
    {
        ThrowIfDisposed();
        if (progress is null)
        {
            throw new ArgumentNullException(nameof(progress));
        }

        ProgressStore.Save(progress);
    }

    public async Task<ReadingQueue> CreateQueueAsync(string bookId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var book = await OpenBookAsync(bookId, cancellationToken).ConfigureAwait(false);
        var segments = Library.GetSegments(book.Id);
        NAudioReadingSink? sink = null;
        ReadingQueue? queue = null;
        var published = false;
        try
        {
            sink = new NAudioReadingSink();
            queue = new ReadingQueue(
                book,
                segments,
                Cache,
                SynthesizeToDestinationAsync,
                sink,
                ProgressStore,
                VoiceId,
                SpeechSpeed);
            queue.Log += OnLog;

            ReadingQueue? previous;
            NAudioReadingSink? previousSink;
            lock (_gate)
            {
                ThrowIfDisposed();
                previous = _queue;
                previousSink = _sink;
                _queue = queue;
                _sink = sink;
                published = true;
            }

            if (previous is not null)
            {
                previous.Log -= OnLog;
                previous.Dispose();
            }

            previousSink?.Dispose();
            return queue;
        }
        catch
        {
            if (!published)
            {
                if (queue is not null)
                {
                    queue.Log -= OnLog;
                    queue.Dispose();
                }

                sink?.Dispose();
            }

            throw;
        }
    }

    public async Task<string> EnsureSegmentAudioAsync(
        string bookId,
        int segmentIndex,
        double? speechSpeed = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var book = await Library.GetBookAsync(bookId, cancellationToken).ConfigureAwait(false);
        var segments = Library.GetSegments(book.Id);
        Segment? segment = null;
        foreach (var item in segments)
        {
            if (item.SegmentIndex == segmentIndex)
            {
                segment = item;
                break;
            }
        }

        if (segment is null)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex), "段序号不存在。");
        }

        var speed = ClampSpeechSpeed(speechSpeed ?? SpeechSpeed);
        try
        {
            return await Cache.GetOrCreateAsync(
                book.Id,
                segment.SegmentIndex,
                VoiceId,
                segment.Text,
                speed,
                (text, destination, token) => SynthesizeAtSpeedAsync(text, destination, speed, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnLog("[Reading] TTS 失败: " + ex.Message);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Library.Log -= OnLog;
        Cache.Log -= OnLog;
        ReadingQueue? queue;
        NAudioReadingSink? sink;
        lock (_gate)
        {
            queue = _queue;
            sink = _sink;
            _queue = null;
            _sink = null;
        }

        if (queue is not null)
        {
            queue.Log -= OnLog;
            queue.Dispose();
        }

        sink?.Dispose();
    }

    private Task SynthesizeToDestinationAsync(string text, string destinationPath, CancellationToken cancellationToken)
    {
        return SynthesizeAtSpeedAsync(text, destinationPath, SpeechSpeed, cancellationToken);
    }

    private async Task SynthesizeAtSpeedAsync(string text, string destinationPath, double speechSpeed, CancellationToken cancellationToken)
    {
        try
        {
            var produced = await _tts.SynthesizeAsync(text, cancellationToken, speechSpeed).ConfigureAwait(false);
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Move(produced, destinationPath, overwrite: true);
        }
        catch (Exception ex)
        {
            OnLog("[Reading] TTS 失败: " + ex.Message);
            throw;
        }
    }

    private void OnLog(string message)
    {
        try
        {
            Log?.Invoke(message);
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ReadingService));
        }
    }

    private static double ClampSpeechSpeed(double speed)
    {
        // MiniMax voice_setting.speed 的有效区间是 0.5–2.0
        if (double.IsNaN(speed) || speed <= 0)
        {
            return 1.0;
        }

        return speed < 0.5 ? 0.5 : speed > 2.0 ? 2.0 : speed;
    }
}
