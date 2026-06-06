using System;

[Serializable]
public class AssistantMessage
{
    public string id;
    public string source;
    public string type;
    public string text;
    public string utcTime;

    public static AssistantMessage Create(string source, string type, string text)
    {
        return new AssistantMessage
        {
            id = Guid.NewGuid().ToString("N"),
            source = source,
            type = type,
            text = text,
            utcTime = DateTime.UtcNow.ToString("O")
        };
    }
}
