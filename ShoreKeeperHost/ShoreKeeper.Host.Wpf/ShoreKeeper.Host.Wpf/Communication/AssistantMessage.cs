namespace ShoreKeeper.Host.Wpf.Communication;

public sealed class AssistantMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Source { get; set; } = "host";

    public string Type { get; set; } = "chat";

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset UtcTime { get; set; } = DateTimeOffset.UtcNow;
}
