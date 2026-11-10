#if WINDOWS
using System.IO;
#endif
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AIAssistant.Host.Wpf.Reading;

/// <summary>
/// 按书籍和段序号缓存 TTS mp3。旁路 key 文件用于区分音色和文本，避免复用过期音频。
/// </summary>
public sealed class TtsCache
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 创建缓存。根目录不存在时会立刻建出来。
    /// </summary>
    public TtsCache(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("缓存目录不能为空。", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RootDirectory);
    }

    /// <summary>缓存根目录的绝对路径。</summary>
    public string RootDirectory { get; }

    /// <summary>命中、未命中、合成和失败都会回调。</summary>
    public event Action<string>? Log;

    /// <summary>
    /// 返回 <c>&lt;root&gt;\&lt;bookId&gt;\&lt;segmentIndex&gt;.mp3</c>，不检查文件是否存在。
    /// </summary>
    public string GetCachePath(string bookId, int segmentIndex)
    {
        RequireNonEmpty(bookId, nameof(bookId));
        return Path.Combine(RootDirectory, bookId, IndexText(segmentIndex) + ".mp3");
    }

    /// <summary>
    /// SHA256(UTF-8(bookId|segmentIndex|voiceId|speed|text)) 的 64 位小写十六进制。
    /// 语速进 key：换语速后旧音频自动失效，不会被当成命中。
    /// </summary>
    public static string ComputeCacheKey(string bookId, int segmentIndex, string voiceId, string text, double speechSpeed = 1.0)
    {
        RequireNonEmpty(bookId, nameof(bookId));
        RequireNonEmpty(voiceId, nameof(voiceId));
        RequireNonEmpty(text, nameof(text));

        var raw = string.Concat(
            bookId, "|", IndexText(segmentIndex), "|", voiceId, "|", SpeedText(speechSpeed), "|", text);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// mp3 与 sidecar key 都存在且 key 一致时返回 true。
    /// </summary>
    public bool TryGetCached(string bookId, int segmentIndex, string voiceId, string text, out string path, double speechSpeed = 1.0)
    {
        var key = ComputeCacheKey(bookId, segmentIndex, voiceId, text, speechSpeed);
        var candidate = GetCachePath(bookId, segmentIndex);
        if (IsFresh(candidate, key))
        {
            path = candidate;
            WriteLog("[Cache] hit " + bookId + " #" + segmentIndex + " " + candidate);
            return true;
        }

        path = string.Empty;
        WriteLog("[Cache] miss " + bookId + " #" + segmentIndex);
        return false;
    }

    /// <summary>
    /// 命中则直接返回路径。未命中时调用 <paramref name="synthesizeAsync"/> 把音频写到临时文件，再原子替换到缓存路径。
    /// 同一目标路径的并发调用会串行，不同段可以并行。
    /// </summary>
    public async Task<string> GetOrCreateAsync(
        string bookId,
        int segmentIndex,
        string voiceId,
        string text,
        double speechSpeed,
        Func<string, string, CancellationToken, Task> synthesizeAsync,
        CancellationToken cancellationToken = default)
    {
        if (synthesizeAsync is null)
        {
            throw new ArgumentNullException(nameof(synthesizeAsync));
        }

        var key = ComputeCacheKey(bookId, segmentIndex, voiceId, text, speechSpeed);
        var path = GetCachePath(bookId, segmentIndex);
        var gate = _gates.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsFresh(path, key))
            {
                WriteLog("[Cache] hit " + bookId + " #" + segmentIndex + " " + path);
                return path;
            }

            WriteLog("[Cache] miss " + bookId + " #" + segmentIndex);
            try
            {
                WriteLog("[Cache] synthesize " + bookId + " #" + segmentIndex);
                await WriteNewEntryAsync(path, key, text, synthesizeAsync, cancellationToken).ConfigureAwait(false);
                return path;
            }
            catch (Exception ex)
            {
                WriteLog("[Cache] fail " + bookId + " #" + segmentIndex + " " + ex.Message);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WriteNewEntryAsync(
        string finalPath,
        string key,
        string text,
        Func<string, string, CancellationToken, Task> synthesizeAsync,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(directory);

        var token = Guid.NewGuid().ToString("N");
        var tempAudio = finalPath + ".tmp-" + token;
        var keyPath = Path.ChangeExtension(finalPath, ".key");
        var tempKey = keyPath + ".tmp-" + token;

        try
        {
            await synthesizeAsync(text, tempAudio, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(tempAudio))
            {
                throw new IOException("合成回调没有写下目标文件：" + tempAudio);
            }

            await File.WriteAllTextAsync(tempKey, key, Utf8NoBom, cancellationToken).ConfigureAwait(false);

            // 先拿掉旧 key，避免新 mp3 和旧音色 key 被同时看成命中。
            if (File.Exists(keyPath))
            {
                File.Delete(keyPath);
            }

            File.Move(tempAudio, finalPath, overwrite: true);
            File.Move(tempKey, keyPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempAudio);
            TryDelete(tempKey);
            throw;
        }
    }

    private static bool IsFresh(string mp3Path, string key)
    {
        if (!File.Exists(mp3Path))
        {
            return false;
        }

        var keyPath = Path.ChangeExtension(mp3Path, ".key");
        if (!File.Exists(keyPath))
        {
            return false;
        }

        try
        {
            var stored = File.ReadAllText(keyPath, Utf8NoBom).Trim();
            return stored.Equals(key, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void WriteLog(string message) => Log?.Invoke(message);

    private static string IndexText(int segmentIndex) =>
        segmentIndex.ToString(CultureInfo.InvariantCulture);

    private static string SpeedText(double speechSpeed) =>
        (speechSpeed <= 0 ? 1.0 : speechSpeed).ToString("0.###", CultureInfo.InvariantCulture);

    private static void RequireNonEmpty(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("不能为空。", paramName);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
