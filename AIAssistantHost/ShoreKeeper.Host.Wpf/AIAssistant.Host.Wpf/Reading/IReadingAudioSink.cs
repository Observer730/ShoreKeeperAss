namespace AIAssistant.Host.Wpf.Reading;

/// <summary>
/// 播放器抽象。测试使用假实现；本机播放由 NAudio 实现。
/// PositionSeconds 是源音频时间，不是倍速之后的墙上时钟。
/// </summary>
public interface IReadingAudioSink : IDisposable
{
    event EventHandler? PlaybackEnded;

    event EventHandler<string>? PlaybackFailed;

    bool IsPlaying { get; }

    double PositionSeconds { get; }

    double DurationSeconds { get; }

    void Load(string filePath);

    void Play();

    void Pause();

    void Stop();

    void Seek(double seconds);

    /// <summary>播放倍速，范围 0.5 到 2.0。</summary>
    void SetSpeed(double speed);

    /// <summary>音量，范围 0 到 1。</summary>
    void SetVolume(double volume);
}
