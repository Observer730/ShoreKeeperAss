using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIAssistant.Host.Wpf.MiniMax;

public sealed class MiniMaxVoiceCloneService
{
    private const string BaseUrl = "https://api.minimax.cn/v1";
    private const string UploadPath = "/files/upload";
    private const string VoiceClonePath = "/voice_clone";
    private const string LocalApiKeyFileName = "MiniMax.ApiKey.local.txt";
    private const string DeveloperFallbackApiKey = "";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = new();
    public string Model { get; set; } = "speech-2.8-hd";
    public string PreviewText { get; set; } =
        "这是一段试听文本,用来验证克隆音色的相似度。";

    /// <summary>
    /// 上传本地音频 → 调 voice_clone → 返回新的 voice_id。
    /// 同时把 trial 试听音频落到 OutputDirectory(若有)。
    /// </summary>
    public async Task<string> CloneFromFileAsync(
        string audioFilePath,
        string voiceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath))
        {
            throw new ArgumentException("Audio file path is required.", nameof(audioFilePath));
        }
        if (!File.Exists(audioFilePath))
        {
            throw new FileNotFoundException("Audio file not found.", audioFilePath);
        }
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            throw new ArgumentException("Voice id is required.", nameof(voiceId));
        }

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "MiniMax API key is not set. Set MINIMAX_API_KEY, put it in MiniMax.ApiKey.local.txt, or fill DeveloperFallbackApiKey.");
        }

        // 1) 上传源音频拿 file_id
        long fileId = await UploadAsync(audioFilePath, "voice_clone", apiKey, cancellationToken);

        // 2) 调 voice_clone
        var requestBody = new
        {
            file_id = fileId,
            voice_id = voiceId,
            text = PreviewText,
            model = Model,
            need_noise_reduction = true,
            need_volume_normalization = true
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        var url = BaseUrl.TrimEnd('/') + VoiceClonePath;
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

        // 3) 解析返回(检查 base_resp.status_code)
        using var document = JsonDocument.Parse(responseText);
        if (document.RootElement.TryGetProperty("base_resp", out var br) &&
            br.TryGetProperty("status_code", out var sc) &&
            sc.ValueKind == JsonValueKind.Number &&
            sc.GetInt32() != 0)
        {
            var msg = br.TryGetProperty("status_msg", out var sm) && sm.ValueKind == JsonValueKind.String
                ? sm.GetString() : "unknown error";
            throw new InvalidOperationException(
                $"MiniMax voice_clone returned error: {msg}. Raw: {responseText}");
        }

        // 4) trial_audio 如果是 hex 就落盘(可选,便于用户试听)
        if (document.RootElement.TryGetProperty("trial_audio", out var trialAudio) &&
            trialAudio.ValueKind == JsonValueKind.String)
        {
            var hex = trialAudio.GetString();
            if (!string.IsNullOrWhiteSpace(hex))
            {
                try
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "Recordings", "Clones");
                    Directory.CreateDirectory(dir);
                    var trialPath = Path.Combine(dir, $"trial_{voiceId}_{DateTime.UtcNow:yyyyMMddHHmmss}.mp3");
                    await File.WriteAllBytesAsync(trialPath, Convert.FromHexString(hex), cancellationToken);
                }
                catch
                {
                    // 落盘失败不影响 voice_clone 成功
                }
            }
        }

        return voiceId;
    }

    private async Task<long> UploadAsync(
        string filePath, string purpose, string apiKey, CancellationToken cancellationToken)
    {
        var url = BaseUrl.TrimEnd('/') + UploadPath;
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var fileName = Path.GetFileName(filePath);
        var content = new MultipartFormDataContent
        {
            { new StringContent(purpose), "purpose" },
            { new ByteArrayContent(fileBytes), "file", fileName }
        };
        request.Content = content;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw MiniMaxServiceException.FromHttp(
                (int)response.StatusCode, response.ReasonPhrase, responseText);
        }

        using var document = JsonDocument.Parse(responseText);
        // 返回形如 { "file": { "file_id": "..." } }
        if (document.RootElement.TryGetProperty("file", out var file) &&
            file.TryGetProperty("file_id", out var fileId))
        {
            return fileId.ValueKind switch
            {
                JsonValueKind.String => long.Parse(fileId.GetString()!),  // "437523098431784" → 437523098431784
                JsonValueKind.Number => fileId.GetInt64(),                // 直接读为 int64
                _ => throw new InvalidOperationException(
                    $"MiniMax file upload returned unexpected file_id type: {fileId.ValueKind}. Raw: {responseText}")
            };
        }

        throw new InvalidOperationException(
            "MiniMax file upload response did not contain file.file_id. Raw: " + responseText);
    }

    private static string? ResolveApiKey()
    {
        var env = Environment.GetEnvironmentVariable("MINIMAX_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        var localPath = ResolveLocalApiKeyPath();
        if (File.Exists(localPath))
        {
            var key = File.ReadAllText(localPath).Trim();
            if (!string.IsNullOrWhiteSpace(key)) return key;
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
