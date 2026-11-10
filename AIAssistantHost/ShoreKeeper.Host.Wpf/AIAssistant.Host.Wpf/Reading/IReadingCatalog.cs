namespace AIAssistant.Host.Wpf.Reading;

/// <summary>
/// 远程听书服务需要的最小书库接口。HTTP 层只依赖它，测试可以注入假实现。
/// </summary>
public interface IReadingCatalog
{
    /// <summary>列出书库里的书（不强制解析正文）。</summary>
    IReadOnlyList<BookInfo> ListBooks();

    /// <summary>解析并返回一本书。</summary>
    Task<Book> GetBookAsync(string bookId, CancellationToken cancellationToken = default);

    /// <summary>返回已解析书的全部段。</summary>
    IReadOnlyList<Segment> GetSegments(string bookId);

    /// <summary>读取断点；没有返回 null。</summary>
    ReadingProgress? GetProgress(string bookId);

    /// <summary>保存断点。</summary>
    void SaveProgress(ReadingProgress progress);

    /// <summary>
    /// 确保某段 mp3 已在缓存里，返回绝对路径；必要时触发 TTS。
    /// <paramref name="speechSpeed"/> 为 null 时用服务当前语速；手机端会带 <c>?speed=</c> 指定，
    /// 这样慢速由 TTS 直接合成，不用播放端变速（不会出电音）。
    /// </summary>
    Task<string> EnsureSegmentAudioAsync(
        string bookId,
        int segmentIndex,
        double? speechSpeed = null,
        CancellationToken cancellationToken = default);
}
