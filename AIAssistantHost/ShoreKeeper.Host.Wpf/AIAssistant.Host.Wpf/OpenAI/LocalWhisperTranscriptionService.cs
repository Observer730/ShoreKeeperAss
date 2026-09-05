using System.Diagnostics;
using System.IO;

namespace AIAssistant.Host.Wpf.OpenAI;

public sealed class LocalWhisperTranscriptionService
{
    private static readonly string[] ExecutableNames =
    [
        "whisper-cli.exe",
        "main.exe"
    ];

    private static readonly string[] ModelNames =
    [
        "ggml-small.bin",
        "ggml-base.bin",
        "ggml-tiny.bin"
    ];

    public string Language { get; set; } = "zh";

    /// <summary>
    /// When set, this exact model file is used (e.g. "ggml-medium.bin").
    /// When null, the first available model in <see cref="ModelNames"/> order is used.
    /// </summary>
    public string? ModelName { get; set; }

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

        var rootDirectory = ResolveLocalWhisperDirectory();
        var executablePath = ResolveFirstExistingPath(rootDirectory, ExecutableNames);
        var modelPath = ResolveModelPath(rootDirectory);

        if (executablePath is null)
        {
            throw new InvalidOperationException(
                $"Local Whisper executable was not found. Put whisper-cli.exe or main.exe in: {rootDirectory}");
        }

        if (modelPath is null)
        {
            throw new InvalidOperationException(
                $"Local Whisper model was not found. Put ggml-base.bin, ggml-small.bin, or ggml-tiny.bin in: {Path.Combine(rootDirectory, "models")}");
        }

        var outputDirectory = Path.Combine(rootDirectory, "output");
        Directory.CreateDirectory(outputDirectory);

        var outputBasePath = Path.Combine(
            outputDirectory,
            Path.GetFileNameWithoutExtension(filePath) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        var result = await RunWhisperAsync(executablePath, modelPath, filePath, outputBasePath, Language, cancellationToken);
        var outputTextPath = outputBasePath + ".txt";

        if (File.Exists(outputTextPath))
        {
            return (await File.ReadAllTextAsync(outputTextPath, cancellationToken)).Trim();
        }

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return result.StandardOutput.Trim();
        }

        throw new InvalidOperationException(
            $"Local Whisper did not produce transcript text.{Environment.NewLine}{result.StandardError}");
    }

    private static async Task<WhisperProcessResult> RunWhisperAsync(
        string executablePath,
        string modelPath,
        string audioFilePath,
        string outputBasePath,
        string language,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add(modelPath);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(audioFilePath);
        process.StartInfo.ArgumentList.Add("-l");
        process.StartInfo.ArgumentList.Add(language);
        process.StartInfo.ArgumentList.Add("-otxt");
        process.StartInfo.ArgumentList.Add("-of");
        process.StartInfo.ArgumentList.Add(outputBasePath);
        process.StartInfo.ArgumentList.Add("-nt");

        process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Local Whisper failed with exit code {process.ExitCode}.{Environment.NewLine}{standardError}{Environment.NewLine}{standardOutput}");
        }

        return new WhisperProcessResult(standardOutput, standardError);
    }

    private static string ResolveLocalWhisperDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var projectFilePath = Path.Combine(directory.FullName, "AIAssistant.Host.Wpf.csproj");
            if (File.Exists(projectFilePath))
            {
                return Path.Combine(directory.FullName, "LocalWhisper");
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "LocalWhisper");
    }

    private string? ResolveModelPath(string rootDirectory)
    {
        var modelsDir = Path.Combine(rootDirectory, "models");

        // 1. Explicit override via ModelName
        if (!string.IsNullOrWhiteSpace(ModelName))
        {
            var explicitPath = Path.Combine(modelsDir, ModelName);
            if (File.Exists(explicitPath))
            {
                return explicitPath;
            }
            // ModelName was set but file is missing - fall through to default search
        }

        // 2. Default search order
        return ResolveFirstExistingPath(modelsDir, ModelNames);
    }

    private static string? ResolveFirstExistingPath(string directory, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private sealed record WhisperProcessResult(string StandardOutput, string StandardError);
}
