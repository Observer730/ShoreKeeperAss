namespace AIAssistant.Host.Wpf.Reading;

/// <summary>
/// 按段播放一本书：当前段合成完成后立刻播放，并只预取下一段。
/// 所有公开异步操作共用一把锁串行执行，事件在锁外触发。
/// </summary>
public sealed class ReadingQueue : IDisposable
{
    private readonly TtsCache _cache;
    private readonly Func<string, string, CancellationToken, Task> _synthesizeAsync;
    private readonly IReadingAudioSink _sink;
    private readonly ReadingProgressStore _progressStore;
    private readonly string _voiceId;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private int _currentIndex;
    private double _speed = 1.0;
    private double _positionSeconds;
    private bool _audioLoaded;
    private bool _acceptNaturalEnd;
    private int _disposeOnce;

    public ReadingQueue(
        Book book,
        IReadOnlyList<Segment> segments,
        TtsCache cache,
        Func<string, string, CancellationToken, Task> synthesizeAsync,
        IReadingAudioSink sink,
        ReadingProgressStore progressStore,
        string voiceId)
    {
        Book = book ?? throw new ArgumentNullException(nameof(book));
        Segments = segments ?? throw new ArgumentNullException(nameof(segments));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _synthesizeAsync = synthesizeAsync ?? throw new ArgumentNullException(nameof(synthesizeAsync));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            throw new ArgumentException("voiceId 不能为空。", nameof(voiceId));
        }

        _voiceId = voiceId.Trim();
        _sink.PlaybackEnded += OnPlaybackEnded;
        _sink.PlaybackFailed += OnPlaybackFailed;

        var saved = SafeLoadProgress();
        if (saved is not null && string.Equals(saved.BookId, Book.Id, StringComparison.Ordinal))
        {
            _currentIndex = ClampIndex(saved.SegmentIndex);
            _speed = ClampSpeed(saved.Speed);
            _positionSeconds = saved.PositionSeconds < 0 ? 0 : saved.PositionSeconds;
        }
    }

    public Book Book { get; }

    public IReadOnlyList<Segment> Segments { get; }

    public int CurrentIndex => _currentIndex;

    public bool IsPlaying
    {
        get
        {
            try
            {
                return _sink.IsPlaying;
            }
            catch
            {
                return false;
            }
        }
    }

    public double Speed => _speed;

    /// <summary>当前段的源音频时长。还没加载时为 0。</summary>
    public double DurationSeconds
    {
        get
        {
            if (!_audioLoaded)
            {
                return 0;
            }

            try
            {
                return _sink.DurationSeconds;
            }
            catch
            {
                return 0;
            }
        }
    }

    public ReadingProgress CurrentProgress
    {
        get
        {
            var index = ClampIndex(_currentIndex);
            var chapterIndex = Segments.Count == 0 ? 0 : Segments[index].ChapterIndex;
            var position = _audioLoaded ? ReadSinkPosition() : _positionSeconds;
            if (position < 0 || double.IsNaN(position))
            {
                position = 0;
            }

            return new ReadingProgress
            {
                BookId = Book.Id,
                SegmentIndex = index,
                ChapterIndex = chapterIndex,
                PositionSeconds = position,
                Speed = _speed,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }
    }

    public event EventHandler? StateChanged;

    public event EventHandler<Segment>? CurrentSegmentChanged;

    public event Action<string>? Log;

    public async Task PlayAsync(int? segmentIndex = null, double positionSeconds = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var index = segmentIndex ?? _currentIndex;
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        Segment? segment = null;
        try
        {
            segment = await PlayCoreAsync(index, positionSeconds, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        Publish(segment);
    }

    public void Pause()
    {
        ThrowIfDisposed();
        _mutex.Wait();
        try
        {
            _acceptNaturalEnd = false;
            try
            {
                _sink.Pause();
            }
            catch (Exception ex)
            {
                WriteLog("[Reading] 暂停失败: " + ex.Message);
            }

            _positionSeconds = _audioLoaded ? ReadSinkPosition() : _positionSeconds;
            SaveCore();
        }
        finally
        {
            _mutex.Release();
        }

        Publish(null);
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        return PlayAsync(_currentIndex, _positionSeconds, cancellationToken);
    }

    public async Task StopAsync(bool markFinished = false)
    {
        ThrowIfDisposed();
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            StopCore(markFinished);
        }
        finally
        {
            _mutex.Release();
        }

        Publish(null);
    }

    public Task NextSegmentAsync(CancellationToken cancellationToken = default)
    {
        return MoveSegmentAsync(+1, cancellationToken);
    }

    public Task PreviousSegmentAsync(CancellationToken cancellationToken = default)
    {
        return MoveSegmentAsync(-1, cancellationToken);
    }

    public async Task NextChapterAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        Segment? segment = null;
        try
        {
            if (Segments.Count == 0)
            {
                WriteLog("[Reading] 这本书没有可播放的段。");
                return;
            }

            var target = FindNextChapterStart(ClampIndex(_currentIndex));
            if (target < 0)
            {
                WriteLog("[Reading] 已经是最后一章。");
                return;
            }

            segment = await PlayCoreAsync(target, 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        Publish(segment);
    }

    public async Task PreviousChapterAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        Segment? segment = null;
        try
        {
            if (Segments.Count == 0)
            {
                WriteLog("[Reading] 这本书没有可播放的段。");
                return;
            }

            var target = FindPreviousChapterEnd(ClampIndex(_currentIndex));
            if (target < 0)
            {
                WriteLog("[Reading] 已经是第一章。");
                return;
            }

            segment = await PlayCoreAsync(target, 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        Publish(segment);
    }

    public async Task SeekToSegmentAsync(int segmentIndex, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        Segment? segment = null;
        try
        {
            if (segmentIndex < 0 || segmentIndex >= Segments.Count)
            {
                WriteLog("[Reading] 段序号超出范围，保持当前段。");
                return;
            }

            segment = await PlayCoreAsync(segmentIndex, 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        Publish(segment);
    }

    public async Task SetSpeedAsync(double speed)
    {
        ThrowIfDisposed();
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _speed = ClampSpeed(speed);
            _sink.SetSpeed(_speed);
            SaveCore();
        }
        finally
        {
            _mutex.Release();
        }

        Publish(null);
    }

    public void SaveProgress()
    {
        if (Volatile.Read(ref _disposeOnce) != 0)
        {
            return;
        }

        _mutex.Wait();
        try
        {
            SaveCore();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0)
        {
            return;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _mutex.Wait();
            try
            {
                _acceptNaturalEnd = false;
                try
                {
                    _sink.Stop();
                }
                catch (Exception ex)
                {
                    WriteLog("[Reading] 停止失败: " + ex.Message);
                }

                SaveCore();
            }
            finally
            {
                _mutex.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        _sink.PlaybackEnded -= OnPlaybackEnded;
        _sink.PlaybackFailed -= OnPlaybackFailed;
        _mutex.Dispose();
        _lifetime.Dispose();
    }

    private async Task MoveSegmentAsync(int delta, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        Segment? segment = null;
        try
        {
            if (Segments.Count == 0)
            {
                WriteLog("[Reading] 这本书没有可播放的段。");
                return;
            }

            var target = _currentIndex + delta;
            if (target < 0)
            {
                WriteLog("[Reading] 已经是第一段。");
                return;
            }

            if (target >= Segments.Count)
            {
                WriteLog("[Reading] 已经是最后一段。");
                return;
            }

            segment = await PlayCoreAsync(target, 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        Publish(segment);
    }

    private async Task<Segment?> PlayCoreAsync(int index, double positionSeconds, CancellationToken cancellationToken)
    {
        if (Segments.Count == 0)
        {
            WriteLog("[Reading] 这本书没有可播放的段。");
            return null;
        }

        if (index < 0 || index >= Segments.Count)
        {
            WriteLog("[Reading] 段序号超出范围，改为边界段。");
            index = ClampIndex(index);
        }

        _acceptNaturalEnd = false;
        var segment = Segments[index];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var path = await _cache.GetOrCreateAsync(
            Book.Id,
            segment.SegmentIndex,
            _voiceId,
            segment.Text,
            _synthesizeAsync,
            linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();

        KickPrefetch(index + 1);
        var position = positionSeconds < 0 ? 0 : positionSeconds;
        _sink.Load(path);
        _audioLoaded = true;
        if (position > 0)
        {
            _sink.Seek(position);
        }

        _sink.SetSpeed(_speed);
        _sink.Play();
        _currentIndex = index;
        _positionSeconds = position;
        _acceptNaturalEnd = true;
        SaveCore();
        return segment;
    }

    private void KickPrefetch(int index)
    {
        if (index < 0 || index >= Segments.Count)
        {
            return;
        }

        var segment = Segments[index];
        var token = _lifetime.Token;
        _ = PrefetchAsync(segment, token);
    }

    private async Task PrefetchAsync(Segment segment, CancellationToken cancellationToken)
    {
        try
        {
            await _cache.GetOrCreateAsync(
                Book.Id,
                segment.SegmentIndex,
                _voiceId,
                segment.Text,
                _synthesizeAsync,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            WriteLog("[Reading] 预取下一段失败 #" + segment.SegmentIndex + " " + ex.Message);
        }
    }

    private void StopCore(bool markFinished)
    {
        _acceptNaturalEnd = false;
        try
        {
            _sink.Stop();
        }
        catch (Exception ex)
        {
            WriteLog("[Reading] 停止失败: " + ex.Message);
        }

        if (markFinished && Segments.Count > 0)
        {
            _currentIndex = Segments.Count - 1;
            WriteLog("[Reading] 全书已播放完毕。");
        }

        _positionSeconds = _audioLoaded ? ReadSinkPosition() : _positionSeconds;
        SaveCore();
    }

    private void SaveCore()
    {
        try
        {
            if (_audioLoaded)
            {
                _positionSeconds = ReadSinkPosition();
            }

            _progressStore.Save(CurrentProgress);
        }
        catch (Exception ex)
        {
            WriteLog("[Reading] 保存断点失败: " + ex.Message);
        }
    }

    private ReadingProgress? SafeLoadProgress()
    {
        try
        {
            return _progressStore.Load(Book.Id);
        }
        catch (Exception ex)
        {
            WriteLog("[Reading] 读取断点失败: " + ex.Message);
            return null;
        }
    }

    private double ReadSinkPosition()
    {
        try
        {
            var position = _sink.PositionSeconds;
            return position < 0 || double.IsNaN(position) ? 0 : position;
        }
        catch
        {
            return 0;
        }
    }

    private int FindNextChapterStart(int currentIndex)
    {
        var chapter = Segments[currentIndex].ChapterIndex;
        for (var i = currentIndex + 1; i < Segments.Count; i++)
        {
            if (Segments[i].ChapterIndex > chapter)
            {
                return i;
            }
        }

        return -1;
    }

    private int FindPreviousChapterEnd(int currentIndex)
    {
        var chapter = Segments[currentIndex].ChapterIndex;
        var target = -1;
        for (var i = 0; i < currentIndex; i++)
        {
            if (Segments[i].ChapterIndex < chapter)
            {
                target = i;
            }
        }

        return target;
    }

    private int ClampIndex(int index)
    {
        if (Segments.Count == 0)
        {
            return 0;
        }

        if (index < 0)
        {
            return 0;
        }

        if (index >= Segments.Count)
        {
            return Segments.Count - 1;
        }

        return index;
    }

    private static double ClampSpeed(double speed)
    {
        if (double.IsNaN(speed) || double.IsInfinity(speed))
        {
            return 1.0;
        }

        if (speed < 0.5)
        {
            return 0.5;
        }

        if (speed > 2.0)
        {
            return 2.0;
        }

        return speed;
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        _ = HandleNaturalEndAsync();
    }

    private async Task HandleNaturalEndAsync()
    {
        await Task.Yield();
        if (Volatile.Read(ref _disposeOnce) != 0)
        {
            return;
        }

        try
        {
            await _mutex.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Segment? segment = null;
        var finished = false;
        try
        {
            if (Volatile.Read(ref _disposeOnce) != 0 || !_acceptNaturalEnd)
            {
                return;
            }

            _acceptNaturalEnd = false;
            var next = _currentIndex + 1;
            if (next >= Segments.Count)
            {
                StopCore(markFinished: true);
                finished = true;
            }
            else
            {
                segment = await PlayCoreAsync(next, 0, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            WriteLog("[Reading] 自动下一段失败: " + ex.Message);
        }
        finally
        {
            try
            {
                _mutex.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (segment is not null || finished)
        {
            Publish(segment);
        }
    }

    private void OnPlaybackFailed(object? sender, string message)
    {
        WriteLog("[Reading] 播放失败: " + message);
        Publish(null);
    }

    private void Publish(Segment? segment)
    {
        if (segment is not null)
        {
            CurrentSegmentChanged?.Invoke(this, segment);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void WriteLog(string message)
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
        if (Volatile.Read(ref _disposeOnce) != 0)
        {
            throw new ObjectDisposedException(nameof(ReadingQueue));
        }
    }
}
