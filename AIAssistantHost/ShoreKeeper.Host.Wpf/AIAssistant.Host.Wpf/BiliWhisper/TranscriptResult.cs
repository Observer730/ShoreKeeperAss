namespace AIAssistant.Host.Wpf.BiliWhisper;

/// <summary>
/// Result of a transcription job: plain text + SRT with timestamps + metadata.
/// PlainText can be derived from SrtText by stripping timestamps, but we keep them separate
/// so callers don't pay the parsing cost unless they need the SRT form.
/// </summary>
public sealed record TranscriptResult(
    string PlainText,
    string SrtText,
    string? SrtPath,
    TimeSpan Duration,
    string ModelUsed,
    string SourceDescription);
