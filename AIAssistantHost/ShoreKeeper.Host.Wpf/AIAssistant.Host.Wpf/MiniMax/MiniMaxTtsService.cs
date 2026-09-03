using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIAssistant.Host.Wpf.MiniMax;

public sealed class MiniMaxTtsService
{
    private const string BaseUrl = "https://api.minimax.cn/v1";
    private const string TtsPath = "/t2a_v2";
    private const string LocalApiKeyFileName = "MiniMax.ApiKey.local.txt";
    private const string DeveloperFallbackApiKey = "";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = new();

    public string Model { get; set; } = "speech-2.8-hd";
    public string VoiceId { get; set; } = "danya_xuejie";
    public double Speed { get; set; } = 1.0;
    public double Volume { get; set; } = 1.0;
    public int Pitch { get; set; } = 0;
    public int SampleRate { get; set; } = 32000;
    public int Bitrate { get; set; } = 128000;
    public string Format { get; set; } = "mp3";
    public int Channel { get; set; } = 1;
    public string LanguageBoost { get; set; } = "auto";

    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 合成指定文本,落 mp3 到 OutputDirectory,返回绝对路径。
    /// </summary>
    public async Task<string> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text cannot be empty.", nameof(text));
        }

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "MiniMax API key is not set. Set MINIMAX_API_KEY, put it in MiniMax.ApiKey.local.txt, or fill DeveloperFallbackApiKey.");
        }

        // 防御性二次清洗(虽然调用方已经过滤过 think)
        var cleanText = StripThinkBlocks(text);
        if (string.IsNullOrWhiteSpace(cleanText))
        {
            throw new InvalidOperationException("Text is empty after stripping think blocks.");
        }

        var requestBody = new
        {
            model = Model,
            text = cleanText,
            stream = false,
            voice_setting = new
            {
                voice_id = VoiceId,
                speed = Speed,
                vol = Volume,
                pitch = Pitch
            },
            audio_setting = new
            {
                sample_rate = SampleRate,
                bitrate = Bitrate,
                format = Format,
                channel = Channel
            },
            pronunciation_dict = new { tone = Array.Empty<string>() },
            language_boost = LanguageBoost,
            output_format = "hex"
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        var url = BaseUrl.TrimEnd('/') + TtsPath;
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

        var audioBytes = ExtractAudioBytes(responseText);

        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        var fileName = $"tts_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.{Format}";
        var fullPath = Path.Combine(dir, fileName);
        await File.WriteAllBytesAsync(fullPath, audioBytes, cancellationToken);

        return fullPath;
    }

    /// <summary>
    /// 清除 M3/M 系列回复中混入的 ``...`` 思考块(防 TTS 把"小于号 think 大于号"念出来)。
    /// </summary>
    public static string StripThinkBlocks(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        // 多次匹配非贪婪,支持多段 think
        var stripped = Regex.Replace(text, @"<think>.*?</think>", string.Empty, RegexOptions.Singleline);
        return stripped.Trim();
    }

    private static byte[] ExtractAudioBytes(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);

        if (document.RootElement.TryGetProperty("base_resp", out var baseResp) &&
            baseResp.TryGetProperty("status_code", out var statusCode) &&
            statusCode.ValueKind == JsonValueKind.Number &&
            statusCode.GetInt32() != 0)
        {
            var msg = baseResp.TryGetProperty("status_msg", out var sm) && sm.ValueKind == JsonValueKind.String
                ? sm.GetString() : "unknown error";
            throw new InvalidOperationException($"MiniMax TTS returned error: {msg}. Raw: {responseText}");
        }

        if (document.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("audio", out var audio) &&
            audio.ValueKind == JsonValueKind.String)
        {
            var hex = audio.GetString();
            if (string.IsNullOrWhiteSpace(hex))
            {
                throw new InvalidOperationException("MiniMax TTS returned empty audio. Raw: " + responseText);
            }
            return Convert.FromHexString(hex);
        }

        throw new InvalidOperationException(
            "MiniMax TTS response did not contain data.audio. Raw: " + responseText);
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

    private string ResolveOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(OutputDirectory))
        {
            return OutputDirectory;
        }
        // 默认 <AppContext.BaseDirectory>/Recordings/TTS
        return Path.Combine(AppContext.BaseDirectory, "Recordings", "TTS");
    }
}
