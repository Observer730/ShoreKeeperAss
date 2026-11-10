namespace AIAssistant.Host.Wpf.Reading;

/// <summary>
/// 一本书的播放断点。倍速一并保存，下次打开继续用。
/// </summary>
public sealed class ReadingProgress
{
    public string BookId { get; init; } = "";

    public int SegmentIndex { get; init; }

    public int ChapterIndex { get; init; }

    /// <summary>当前段内的源音频时间，单位秒。</summary>
    public double PositionSeconds { get; init; }

    public double Speed { get; init; } = 1.0;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
