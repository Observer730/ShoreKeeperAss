namespace AIAssistant.Host.Wpf.Communication;

public sealed class AssistantMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Source { get; set; } = "host";

    public string Type { get; set; } = "chat";

    public string Text { get; set; } = string.Empty;

    public string Role { get; set; } = "user";        // system | user | assistant
    public string ConversationId { get; set; } = "default";

    public string AudioPath { get; set; } = string.Empty;  // TTS 音频的绝对路径(file:// URI 或本地路径)

    public DateTimeOffset UtcTime { get; set; } = DateTimeOffset.UtcNow;
}
