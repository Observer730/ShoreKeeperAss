using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIAssistant.Host.Wpf.OpenAI;

public sealed class OpenAITranscriptionService
{
    private const string Endpoint = "https://api.openai.com/v1/audio/transcriptions";
    private const string LocalApiKeyFileName = "OpenAI.ApiKey.local.txt";

    // Only use this for quick local experiments. Prefer OPENAI_API_KEY or OpenAI.ApiKey.local.txt.
    private const string DeveloperFallbackApiKey = "";

    private readonly HttpClient _httpClient = new();

    public string Model { get; set; } = "gpt-4o-transcribe";

    public async Task<string> TranscribeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Audio file path is empty.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Audio file was not found.", filePath);
        }

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OpenAI API key is not set. Set OPENAI_API_KEY, put it in OpenAI.ApiKey.local.txt, or temporarily fill DeveloperFallbackApiKey.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        await using var fileStream = File.OpenRead(filePath);
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(filePath));

        content.Add(new StringContent(Model), "model");
        content.Add(new StringContent("text"), "response_format");
        content.Add(fileContent, "file", Path.GetFileName(filePath));
        request.Content = content;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateOpenAIException((int)response.StatusCode, response.ReasonPhrase, responseText);
        }

        return responseText.Trim();
    }

    private static string GetContentType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".webm" => "audio/webm",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            _ => "application/octet-stream"
        };
    }

    private static string? ResolveApiKey()
    {
        var environmentApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(environmentApiKey))
        {
            return environmentApiKey.Trim();
        }

        var localApiKeyPath = ResolveLocalApiKeyPath();
        if (File.Exists(localApiKeyPath))
        {
            var localApiKey = File.ReadAllText(localApiKeyPath).Trim();
            if (!string.IsNullOrWhiteSpace(localApiKey))
            {
                return localApiKey;
            }
        }

        return string.IsNullOrWhiteSpace(DeveloperFallbackApiKey)
            ? null
            : DeveloperFallbackApiKey.Trim();
    }

    private static string ResolveLocalApiKeyPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var projectFilePath = Path.Combine(directory.FullName, "AIAssistant.Host.Wpf.csproj");
            if (File.Exists(projectFilePath))
            {
                return Path.Combine(directory.FullName, LocalApiKeyFileName);
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, LocalApiKeyFileName);
    }

    private static OpenAIServiceException CreateOpenAIException(int statusCode, string? reasonPhrase, string responseText)
    {
        var errorCode = TryReadJsonString(responseText, "code");
        var errorMessage = TryReadJsonString(responseText, "message");
        var userMessage = statusCode switch
        {
            401 => "OpenAI API key is invalid or expired.",
            403 => "OpenAI API key does not have permission for this request.",
            413 => "The audio file is too large for transcription.",
            429 when errorCode == "insufficient_quota" => "OpenAI quota is insufficient. Check billing, project budget, or account credits.",
            429 => "OpenAI rate limit was hit. Wait a moment and try again.",
            >= 500 => "OpenAI service is temporarily unavailable. Try again later.",
            _ => $"OpenAI transcription failed with HTTP {statusCode}."
        };

        var detail = string.Join(Environment.NewLine, new[]
        {
            $"HTTP {statusCode} {reasonPhrase}",
            string.IsNullOrWhiteSpace(errorCode) ? null : $"Code: {errorCode}",
            string.IsNullOrWhiteSpace(errorMessage) ? null : $"Message: {errorMessage}",
            "Raw response:",
            responseText
        }.Where(line => line is not null));

        return new OpenAIServiceException(userMessage, detail);
    }

    private static string? TryReadJsonString(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
