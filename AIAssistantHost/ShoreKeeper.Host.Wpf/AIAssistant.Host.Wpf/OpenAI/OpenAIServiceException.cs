namespace AIAssistant.Host.Wpf.OpenAI;

public sealed class OpenAIServiceException : Exception
{
    public OpenAIServiceException(string userMessage, string detail)
        : base(userMessage)
    {
        Detail = detail;
    }

    public string Detail { get; }
}
