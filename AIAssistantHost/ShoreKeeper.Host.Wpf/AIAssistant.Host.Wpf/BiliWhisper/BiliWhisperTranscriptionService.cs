using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIAssistant.Host.Wpf.BiliWhisper;

/// <summary>
/// Downloads audio from a Bilibili video URL (or accepts a local file) and runs
/// whisper.cpp locally to produce a transcript (plain text + SRT).
///
/// Tooling (resolved at call time, in this order):
///   1. PATH
///   2. Env var: WHISPER_YTDLP, WHISPER_FFMPEG, WHISPER_CLI, WHISPER_MODELS
///   3. Hard-coded fallback (the author's machine layout)
///
/// Designed to be invokable by an LLM tool-calling layer: see <see cref="GetToolDefinition"/>.
/// </summary>
public sealed class BiliWhisperTranscriptionService
{
    private static readonly string[] ExecutableNames = ["whisper-cli.exe", "main.exe"];

    // medium is the sweet spot for B站 content (mixed speech + music + SFX)
    private static readonly string[] ModelNames = ["ggml-medium.bin", "ggml-small.bin", "ggml-base.bin"];

    public string PreferredModel { get; set; } = "ggml-medium.bin";
    public string Language { get; set; } = "auto";
    public bool KeepIntermediateFiles { get; set; } = false;

    /// <summary>Surface progress / errors. Subscribers can pipe to UI log.</summary>
    public event Action<string>? Log;

    // ---------- public API ----------

    public async Task<TranscriptResult> TranscribeFromUrlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL is empty.", nameof(url));
        }

        var workDir = CreateWorkDir();
        var baseName = ExtractBaseName(url);
        var m4aPath = Path.Combine(workDir, baseName + ".m4a");
        var wavPath = Path.Combine(workDir, baseName + ".wav");

        try
        {
            // 1) download audio via yt-dlp
            Log?.Invoke($"[yt-dlp] fetching {url}");
            await RunYtDlpAsync(url, m4aPath, cancellationToken);

            // 2) convert to 16k mono wav
            Log?.Invoke($"[ffmpeg] converting {Path.GetFileName(m4aPath)} -> wav");
            await RunFfmpegAsync(m4aPath, wavPath, cancellationToken);

            // 3) transcribe
            return await TranscribeWavAsync(wavPath, baseName, workDir, url, cancellationToken);
        }
        finally
        {
            if (!KeepIntermediateFiles)
            {
                SafeDelete(m4aPath);
                SafeDelete(wavPath);
            }
        }
    }

    public async Task<TranscriptResult> TranscribeFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is empty.", nameof(filePath));
        }
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Audio file was not found.", filePath);
        }

        var workDir = CreateWorkDir();
        var baseName = Path.GetFileNameWithoutExtension(filePath);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        string wavPath;
        string? originalPath = null;

        if (ext == ".wav")
        {
            // still copy into work dir so cleanup is uniform
            wavPath = Path.Combine(workDir, baseName + ".wav");
            File.Copy(filePath, wavPath, overwrite: true);
        }
        else
        {
            originalPath = filePath;
            wavPath = Path.Combine(workDir, baseName + ".wav");
            Log?.Invoke($"[ffmpeg] converting {Path.GetFileName(filePath)} -> wav");
            await RunFfmpegAsync(filePath, wavPath, cancellationToken);
        }

        try
        {
            return await TranscribeWavAsync(wavPath, baseName, workDir, filePath, cancellationToken);
        }
        finally
        {
            if (!KeepIntermediateFiles)
            {
                SafeDelete(wavPath);
            }
        }
    }

    // ---------- LLM tool definition (for future function-calling) ----------

    /// <summary>
    /// Returns an OpenAI-compatible function/tool schema so the chat layer can offer
    /// this service to the LLM as a callable tool.
    /// </summary>
    public static JsonElement GetToolDefinition()
    {
        var schema = new
        {
            type = "function",
            function = new
            {
                name = "transcribe_bilibili_video",
                description = "Download audio from a Bilibili (B站) video URL and return a speech transcript "
                    + "with timestamps. Use this when the user asks to read, summarize, evaluate, or quote a B站 video. "
                    + "Input: a bilibili.com URL. Output: plain text plus SRT subtitles.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        url = new
                        {
                            type = "string",
                            description = "Bilibili video URL, e.g. https://www.bilibili.com/video/BV1xxxxxxxxx/"
                        },
                        language = new
                        {
                            type = "string",
                            @enum = new[] { "auto", "zh", "en", "ja" },
                            description = "Audio language. Default: auto (whisper detects)."
                        }
                    },
                    required = new[] { "url" }
                }
            }
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    // ---------- internals ----------

    private async Task<TranscriptResult> TranscribeWavAsync(
        string wavPath, string baseName, string workDir, string source, CancellationToken ct)
    {
        var rootDir = ResolveLocalWhisperDirectory();
        var exe = ResolveFirstExistingPath(rootDir, ExecutableNames)
            ?? throw new InvalidOperationException($"whisper-cli.exe / main.exe not found in {rootDir}");

        var model = ResolveModelPath(rootDir)
            ?? throw new InvalidOperationException($"No whisper model found in {Path.Combine(rootDir, "models")}");

        var srtPath = Path.Combine(workDir, baseName + ".srt");
        var outputBase = Path.Combine(workDir, baseName);

        Log?.Invoke($"[whisper] model={Path.GetFileName(model)} lang={Language}");

        var sw = Stopwatch.StartNew();
        var result = await RunWhisperAsync(exe, model, wavPath, outputBase, Language, ct);
        sw.Stop();
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"whisper-cli failed (exit {result.ExitCode}).\n--- stderr ---\n{result.StandardError}\n--- stdout ---\n{result.StandardOutput}");
        }

        var srtText = File.Exists(srtPath) ? await File.ReadAllTextAsync(srtPath, ct) : string.Empty;
        var plainText = SrtToPlainText(srtText);

        return new TranscriptResult(
            PlainText: plainText,
            SrtText: srtText,
            SrtPath: File.Exists(srtPath) ? srtPath : null,
            Duration: sw.Elapsed,
            ModelUsed: Path.GetFileName(model),
            SourceDescription: source);
    }

    private async Task RunYtDlpAsync(string url, string outputPath, CancellationToken ct)
    {
        var ytDlp = ResolveToolPath(
            "WHISPER_YTDLP",
            "yt-dlp.exe",
            fallbackHints: new[] {
                @"C:\Users\ASUS\AppData\Local\Packages\PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0\LocalCache\local-packages\Python312\Scripts\yt-dlp.exe"
            });

        var args = new List<string>
        {
            "-f", "bestaudio[ext=m4a]/bestaudio",
            "--no-warnings", "--no-progress",
            "-o", outputPath,
            url
        };

        var psi = new ProcessStartInfo
        {
            FileName = ytDlp,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) { psi.ArgumentList.Add(a); }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp failed to start");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        if (p.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"yt-dlp failed (exit {p.ExitCode}).\n--- stderr ---\n{stderr}\n--- stdout ---\n{stdout}");
        }
    }

    private async Task RunFfmpegAsync(string inputPath, string outputPath, CancellationToken ct)
    {
        var ffmpeg = ResolveToolPath(
            "WHISPER_FFMPEG",
            "ffmpeg.exe",
            fallbackHints: new[] {
                @"C:\Users\ASUS\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe"
            });

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add(outputPath);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed to start");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        if (p.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"ffmpeg failed (exit {p.ExitCode}).\n--- stderr ---\n{stderr}\n--- stdout ---\n{stdout}");
        }
    }

    private static async Task<WhisperProcessResult> RunWhisperAsync(
        string executablePath, string modelPath, string audioFilePath,
        string outputBasePath, string language, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(modelPath);
        psi.ArgumentList.Add("-l"); psi.ArgumentList.Add(language);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(audioFilePath);
        psi.ArgumentList.Add("-osrt");              // SRT output (with timestamps)
        psi.ArgumentList.Add("-otxt");              // also plain text
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add(outputBasePath);
        psi.ArgumentList.Add("-nt");                // no prints (cleaner logs)

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("whisper-cli failed to start");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        return new WhisperProcessResult(p.ExitCode, stdout, stderr);
    }

    // ---------- path resolution ----------

    private static string ResolveLocalWhisperDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var proj = Path.Combine(dir.FullName, "AIAssistant.Host.Wpf.csproj");
            if (File.Exists(proj))
            {
                return Path.Combine(dir.FullName, "LocalWhisper");
            }
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "LocalWhisper");
    }

    private string? ResolveModelPath(string rootDir)
    {
        var modelsDir = Path.Combine(rootDir, "models");
        if (!string.IsNullOrWhiteSpace(PreferredModel))
        {
            var p = Path.Combine(modelsDir, PreferredModel);
            if (File.Exists(p)) return p;
        }
        return ResolveFirstExistingPath(modelsDir, ModelNames);
    }

    private static string? ResolveFirstExistingPath(string dir, IEnumerable<string> names)
    {
        foreach (var n in names)
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Resolve an external tool: env var -> PATH -> fallback hints.
    /// </summary>
    private static string ResolveToolPath(string envVar, string exeName, string[] fallbackHints)
    {
        var envVal = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(envVal) && File.Exists(envVal)) return envVal;

        // search PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* skip malformed PATH entries */ }
        }

        // fallback hints
        foreach (var hint in fallbackHints)
        {
            if (File.Exists(hint)) return hint;
        }

        throw new FileNotFoundException(
            $"Could not locate {exeName}. Set ${envVar} or add it to PATH.",
            exeName);
    }

    // ---------- helpers ----------

    private static string CreateWorkDir()
    {
        var root = ResolveLocalWhisperDirectory();
        var outDir = Path.Combine(root, "output");
        Directory.CreateDirectory(outDir);
        var session = $"bili-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}".Substring(0, 32);
        var work = Path.Combine(outDir, session);
        Directory.CreateDirectory(work);
        return work;
    }

    private static string ExtractBaseName(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"BV[0-9A-Za-z]+");
        return m.Success ? m.Value : "video";
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>
    /// Convert SRT (sequence / timestamp / text blocks) into plain text, one line per cue.
    /// </summary>
    private static string SrtToPlainText(string srt)
    {
        if (string.IsNullOrWhiteSpace(srt)) return string.Empty;
        var sb = new StringBuilder();
        foreach (var line in srt.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (int.TryParse(trimmed, out _)) continue;                    // sequence number
            if (trimmed.Contains("-->")) continue;                         // timestamp line
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(trimmed);
        }
        return sb.ToString();
    }

    private sealed record WhisperProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
