using System.Text.Json;

namespace AIAssistant.Host.Wpf.MiniMax;

public sealed class MiniMaxServiceException : Exception
{
    public int StatusCode { get; }
    public string? Detail { get; }

    public MiniMaxServiceException(string message, int statusCode, string? detail)
        : base(message)
    {
        StatusCode = statusCode;
        Detail = detail;
    }

    public static MiniMaxServiceException FromHttp(int statusCode, string? reason, string responseText)
    {
        var (errorCode, errorMsg) = TryReadError(responseText);
        var userMessage = statusCode switch
        {
            401 => "MiniMax API key is invalid or expired.",
            403 => "MiniMax API key does not have permission for this request.",
            429 => "MiniMax rate limit was hit. Wait a moment and try again.",
            >= 500 => "MiniMax service is temporarily unavailable. Try again later.",
            _ => $"MiniMax chat request failed with HTTP {statusCode} {reason}."
        };

        var detail = string.Join(Environment.NewLine, new[]
        {
            $"HTTP {statusCode} {reason}",
            string.IsNullOrWhiteSpace(errorCode) ? null : $"Code: {errorCode}",
            string.IsNullOrWhiteSpace(errorMsg)  ? null : $"Message: {errorMsg}",
            "Raw response:",
            responseText
        }.Where(s => s is not null));

        return new MiniMaxServiceException(userMessage, statusCode, detail);
    }

    private static (string? code, string? message) TryReadError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("base_resp", out var br))
            {
                string? c = br.TryGetProperty("status_code", out var cs) && cs.ValueKind == JsonValueKind.Number
                    ? cs.GetRawText() : null;
                string? m = br.TryGetProperty("status_msg", out var ms) && ms.ValueKind == JsonValueKind.String
                    ? ms.GetString() : null;
                if (!string.IsNullOrWhiteSpace(c) || !string.IsNullOrWhiteSpace(m))
                {
                    return (c, m);
                }
            }
        }
        catch (JsonException) { }
        return (null, null);
    }
}
