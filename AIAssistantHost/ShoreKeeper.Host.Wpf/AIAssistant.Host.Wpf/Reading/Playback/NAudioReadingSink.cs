using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AIAssistant.Host.Wpf.Reading;

/// <summary>
/// 用 WaveOutEvent 播放本地音频。倍速会改变播放时长，并用移调把音高补回去。
/// </summary>
public sealed class NAudioReadingSink : IReadingAudioSink
{
    private readonly object _sync = new();
    private WaveOutEvent? _waveOut;
    private AudioFileReader? _reader;
    private string? _path;
    private double _speed = 1.0;
    private float _volume = 1f;
    private double _positionSeconds;
    private bool _intentionalStop;
    private bool _disposed;

    public event EventHandler? PlaybackEnded;

    public event EventHandler<string>? PlaybackFailed;

    public bool IsPlaying
    {
        get
        {
            lock (_sync)
            {
                return _waveOut?.PlaybackState == PlaybackState.Playing;
            }
        }
    }

    public double PositionSeconds
    {
        get
        {
            lock (_sync)
            {
                if (_reader is null)
                {
                    return 0;
                }

                try
                {
                    return _reader.CurrentTime.TotalSeconds;
                }
                catch (Exception)
                {
                    return _positionSeconds;
                }
            }
        }
    }

    public double DurationSeconds
    {
        get
        {
            lock (_sync)
            {
                if (_reader is null)
                {
                    return 0;
                }

                try
                {
                    return _reader.TotalTime.TotalSeconds;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }
    }

    public void Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("音频路径不能为空。", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到音频文件。", filePath);
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            _path = Path.GetFullPath(filePath);
            _positionSeconds = 0;
            _intentionalStop = true;
            BuildGraph(play: false);
        }
    }

    public void Play()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_waveOut is null)
            {
                throw new InvalidOperationException("尚未加载音频。");
            }

            _intentionalStop = false;
            if (_waveOut.PlaybackState != PlaybackState.Playing)
            {
                _waveOut.Play();
            }
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_reader is not null)
            {
                _positionSeconds = SafePosition(_reader);
            }

            _intentionalStop = true;
            if (_waveOut is not null && _waveOut.PlaybackState == PlaybackState.Playing)
            {
                _waveOut.Pause();
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _intentionalStop = true;
            if (_reader is not null)
            {
                _positionSeconds = SafePosition(_reader);
            }

            StopWaveOut(_waveOut);
        }
    }

    public void Seek(double seconds)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _positionSeconds = seconds < 0 ? 0 : seconds;
            if (_path is null)
            {
                return;
            }

            var playing = _waveOut?.PlaybackState == PlaybackState.Playing;
            BuildGraph(playing);
        }
    }

    public void SetSpeed(double speed)
    {
        var clamped = ClampSpeed(speed);
        lock (_sync)
        {
            ThrowIfDisposed();
            var changed = Math.Abs(_speed - clamped) > 0.0001;
            _speed = clamped;
            if (!changed || _path is null || _reader is null)
            {
                return;
            }

            _positionSeconds = SafePosition(_reader);
            var playing = _waveOut?.PlaybackState == PlaybackState.Playing;
            BuildGraph(playing);
        }
    }

    public void SetVolume(double volume)
    {
        var clamped = volume < 0 ? 0 : volume > 1 ? 1 : volume;
        lock (_sync)
        {
            ThrowIfDisposed();
            _volume = (float)clamped;
            if (_reader is not null)
            {
                _reader.Volume = _volume;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _intentionalStop = true;
            DestroyGraph();
        }
    }

    private void BuildGraph(bool play)
    {
        DestroyGraph();
        if (string.IsNullOrEmpty(_path))
        {
            return;
        }

        AudioFileReader? reader = null;
        WaveOutEvent? waveOut = null;
        try
        {
            reader = new AudioFileReader(_path);
            reader.Volume = _volume;
            var duration = reader.TotalTime.TotalSeconds;
            var position = duration <= 0 ? 0 : Math.Clamp(_positionSeconds, 0, Math.Max(0, duration - 0.01));
            if (position > 0)
            {
                reader.CurrentTime = TimeSpan.FromSeconds(position);
            }

            waveOut = new WaveOutEvent { DesiredLatency = 200 };
            waveOut.PlaybackStopped += OnPlaybackStopped;
            if (Math.Abs(_speed - 1.0) < 0.001)
            {
                waveOut.Init(reader);
            }
            else
            {
                waveOut.Init(CreateTempoProvider(reader, _speed));
            }

            _reader = reader;
            _waveOut = waveOut;
            _positionSeconds = position;
            if (play)
            {
                _intentionalStop = false;
                waveOut.Play();
            }
        }
        catch
        {
            if (waveOut is not null)
            {
                waveOut.PlaybackStopped -= OnPlaybackStopped;
                try
                {
                    waveOut.Dispose();
                }
                catch (Exception)
                {
                }
            }

            if (reader is not null)
            {
                try
                {
                    reader.Dispose();
                }
                catch (Exception)
                {
                }
            }

            _reader = null;
            _waveOut = null;
            throw;
        }
    }

    /// <summary>
    /// 1 倍速直连 AudioFileReader。其它倍速：先按 1/speed 降低音高，
    /// 再重采样到 sampleRate/speed（样本变少或变多），最后仍按原始采样率送给声卡。
    /// 这样墙上播放时长会变成原来的 1/speed，音高回到接近原调。
    /// 若把重采样结果按其自身采样率播放，时长不会变。
    /// </summary>
    private static IWaveProvider CreateTempoProvider(AudioFileReader reader, double speed)
    {
        var pitch = new SmbPitchShiftingSampleProvider(reader, 4096, 4L, (float)(1.0 / speed));
        var resampledRate = Math.Max(1, (int)Math.Round(reader.WaveFormat.SampleRate / speed));
        var resampled = new WdlResamplingSampleProvider(pitch, resampledRate);
        var playback = new ClaimedRateSampleProvider(resampled, reader.WaveFormat.SampleRate);
        return playback.ToWaveProvider();
    }

    private void DestroyGraph()
    {
        var waveOut = _waveOut;
        _waveOut = null;
        if (waveOut is not null)
        {
            waveOut.PlaybackStopped -= OnPlaybackStopped;
            StopWaveOut(waveOut);
            try
            {
                waveOut.Dispose();
            }
            catch (Exception)
            {
            }
        }

        if (_reader is not null)
        {
            try
            {
                _reader.Dispose();
            }
            catch (Exception)
            {
            }

            _reader = null;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        EventHandler? ended = null;
        EventHandler<string>? failed = null;
        string? message = null;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _waveOut))
            {
                return;
            }

            if (e.Exception is not null)
            {
                message = e.Exception.Message;
                failed = PlaybackFailed;
            }
            else if (!_intentionalStop && IsNearEnd())
            {
                ended = PlaybackEnded;
            }
        }

        if (message is not null)
        {
            failed?.Invoke(this, message);
            return;
        }

        ended?.Invoke(this, EventArgs.Empty);
    }

    private bool IsNearEnd()
    {
        if (_reader is null)
        {
            return false;
        }

        double position;
        double duration;
        try
        {
            position = _reader.CurrentTime.TotalSeconds;
            duration = _reader.TotalTime.TotalSeconds;
        }
        catch (Exception)
        {
            return false;
        }

        if (duration <= 0.05)
        {
            return true;
        }

        var tolerance = Math.Min(1.0, Math.Max(0.35, duration * 0.08));
        return position >= duration - tolerance;
    }

    private static void StopWaveOut(WaveOutEvent? waveOut)
    {
        if (waveOut is null)
        {
            return;
        }

        try
        {
            if (waveOut.PlaybackState != PlaybackState.Stopped)
            {
                waveOut.Stop();
            }
        }
        catch (Exception)
        {
        }
    }

    private static double SafePosition(AudioFileReader reader)
    {
        try
        {
            var position = reader.CurrentTime.TotalSeconds;
            return position < 0 || double.IsNaN(position) ? 0 : position;
        }
        catch (Exception)
        {
            return 0;
        }
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

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NAudioReadingSink));
        }
    }

    /// <summary>
    /// 读取上游样本，但向 WaveOut 声明另一个采样率，从而改变实际播放时长。
    /// </summary>
    private sealed class ClaimedRateSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;

        public ClaimedRateSampleProvider(ISampleProvider source, int sampleRate)
        {
            _source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, source.WaveFormat.Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count) => _source.Read(buffer, offset, count);
    }
}
