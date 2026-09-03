using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIAssistant.Host.Wpf.MiniMax;

public sealed class MiniMaxChatService
{
    private const string BaseUrl = "https://api.minimax.cn/v1";
    private const string ChatCompletionsPath = "/chat/completions";
    private const string LocalApiKeyFileName = "MiniMax.ApiKey.local.txt";
    private const string DeveloperFallbackApiKey = "";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = new();

    public string Model { get; set; } = "MiniMax-M3";
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 1024;
    public string SystemPrompt { get; set; } =
        "你是一个友好、简洁的中文助手。回答控制在 1-3 句话以内。";

    public async Task<string> ChatAsync(
        string conversationId,
        IReadOnlyList<(string role, string content)> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages is null || messages.Count == 0)
        {
            throw new ArgumentException("Messages cannot be empty.", nameof(messages));
        }

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "MiniMax API key is not set. Set MINIMAX_API_KEY, put it in MiniMax.ApiKey.local.txt, or fill DeveloperFallbackApiKey.");
        }

        var requestMessages = new List<object>();
        if (!string.IsNullOrWhiteSpace(SystemPrompt))
        {
            requestMessages.Add(new { role = "system", content = SystemPrompt });
        }
        foreach (var (role, content) in messages)
        {
            requestMessages.Add(new { role, content });
        }

        var requestBody = new
        {
            model = Model,
            messages = requestMessages,
            temperature = Temperature,
            max_tokens = MaxTokens
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        var url = BaseUrl.TrimEnd('/') + ChatCompletionsPath;
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw MiniMaxServiceException.FromHttp(
                (int)response.StatusCode, response.ReasonPhrase, responseText);
        }

        return ExtractReply(responseText);
    }

    private static string ExtractReply(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);

        if (document.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString()?.Trim() ?? string.Empty;
            }
        }

        throw new InvalidOperationException(
            "MiniMax chat response did not contain choices[0].message.content. Raw: " + responseText);
    }

    private static string? ResolveApiKey()
    {
        var env = Environment.GetEnvironmentVariable("MINIMAX_API_KEY");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        var localPath = ResolveLocalApiKeyPath();
        if (File.Exists(localPath))
        {
            var key = File.ReadAllText(localPath).Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key;
            }
        }

        return string.IsNullOrWhiteSpace(DeveloperFallbackApiKey)
            ? null
            : DeveloperFallbackApiKey.Trim();
    }

    private static string ResolveLocalApiKeyPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var csproj = Path.Combine(dir.FullName, "AIAssistant.Host.Wpf.csproj");
            if (File.Exists(csproj))
            {
                return Path.Combine(dir.FullName, LocalApiKeyFileName);
            }
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, LocalApiKeyFileName);
    }
}
