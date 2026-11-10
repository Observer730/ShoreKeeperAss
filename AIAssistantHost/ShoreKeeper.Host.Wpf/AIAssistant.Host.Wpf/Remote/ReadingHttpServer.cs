using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIAssistant.Host.Wpf.Reading;

namespace AIAssistant.Host.Wpf.Remote;

/// <summary>
/// 只监听 127.0.0.1 的听书 HTTP 服务。手机侧由 PhoneView /reader 反代或 tailscale serve 进入。
/// 手写最小 HTTP/1.1，避免 HttpListener 的 URL ACL 要求。
/// </summary>
public sealed class ReadingHttpServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 128 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;
    private const int CopyBufferBytes = 64 * 1024;

    private static readonly Regex BookIdPattern = new("^[0-9a-f]{24}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IReadingCatalog _catalog;
    private readonly int _requestedPort;
    private readonly string _pageHtml;
    private readonly object _gate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private int _port;
    private int _disposed;

    public ReadingHttpServer(IReadingCatalog catalog, int port = 47630, string? pageHtml = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        if (port < 0 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 0-65535 之间。");
        }

        _requestedPort = port;
        _port = port;
        _pageHtml = pageHtml ?? LoadPageFromAssembly() ?? throw new InvalidOperationException(
            "找不到内嵌的 ReaderPage.html。请确认 csproj 里有 EmbeddedResource。");
    }

    public int Port => Volatile.Read(ref _port);

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _listener is not null;
            }
        }
    }

    public event Action<string>? Log;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_listener is not null)
            {
                return Task.CompletedTask;
            }

            var listener = new TcpListener(IPAddress.Loopback, _requestedPort);
            listener.Start();
            _listener = listener;
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _acceptTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token), CancellationToken.None);
        }

        WriteLog("[Reading] HTTP listening on 127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        Task? acceptTask;
        lock (_gate)
        {
            listener = _listener;
            cts = _cts;
            acceptTask = _acceptTask;
            _listener = null;
            _cts = null;
            _acceptTask = null;
        }

        if (listener is null)
        {
            return;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            listener.Stop();
        }
        catch (SocketException)
        {
        }

        if (acceptTask is not null)
        {
            try
            {
                await acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        cts?.Dispose();
        WriteLog("[Reading] HTTP stopped");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                WriteLog("[Reading] accept 失败: " + ex.Message);
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        string label = "-";
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                label = request.Method + " " + request.Path;
                await RouteAsync(stream, request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            WriteLog("[Reading] " + label + " 连接中断: " + ex.Message);
        }
        catch (Exception ex)
        {
            WriteLog("[Reading] " + label + " 处理失败: " + ex.Message);
        }
        finally
        {
            WriteLog("[Reading] " + label + " " + started.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms");
        }
    }

    private async Task RouteAsync(NetworkStream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        var method = request.Method;
        var path = request.Path;

        if (method is not ("GET" or "HEAD" or "POST"))
        {
            await SendJsonAsync(stream, 405, new { error = "method not allowed" }, headOnly: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method is "GET" or "HEAD")
        {
            if (path is "/" or "/index.html")
            {
                var body = Encoding.UTF8.GetBytes(_pageHtml);
                await SendResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", body,
                    new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
                    headOnly: method == "HEAD", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path == "/favicon.ico")
            {
                await SendResponseAsync(stream, 204, "No Content", "text/plain", Array.Empty<byte>(), null,
                    headOnly: true, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path == "/api/books")
            {
                var books = _catalog.ListBooks();
                var payload = new
                {
                    books = books.Select(book => new
                    {
                        id = book.Id,
                        title = book.Title,
                        format = book.Format,
                        chapterCount = book.ChapterCount,
                        segmentCount = book.SegmentCount,
                        sourceLength = book.SourceLength
                    })
                };
                await SendJsonAsync(stream, 200, payload, method == "HEAD", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path == "/api/progress" && request.Query.TryGetValue("bookId", out var progressBookId))
            {
                var progress = _catalog.GetProgress(progressBookId);
                await SendJsonAsync(stream, 200, new { progress }, method == "HEAD", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (TryMatchBookPath(path, out var bookId, out var bookAction))
            {
                if (bookAction.Length == 0)
                {
                    Book book;
                    try
                    {
                        book = await _catalog.GetBookAsync(bookId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        WriteLog("[Reading] 打开书籍失败 " + bookId + " " + ex.Message);
                        await SendJsonAsync(stream, 404, new { error = "book not found" }, method == "HEAD", cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    var segments = _catalog.GetSegments(book.Id);
                    var chapters = BuildChapters(book, segments);
                    var payload = new
                    {
                        id = book.Id,
                        title = book.Title,
                        format = book.Format,
                        chapterCount = chapters.Count,
                        segmentCount = segments.Count,
                        chapters,
                        progress = _catalog.GetProgress(book.Id)
                    };
                    await SendJsonAsync(stream, 200, payload, method == "HEAD", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (bookAction == "segments")
                {
                    Book book;
                    try
                    {
                        book = await _catalog.GetBookAsync(bookId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        WriteLog("[Reading] 打开书籍失败 " + bookId + " " + ex.Message);
                        await SendJsonAsync(stream, 404, new { error = "book not found" }, method == "HEAD", cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    var all = _catalog.GetSegments(book.Id);
                    var from = ParseInt(request.Query, "from", 0);
                    var count = ParseInt(request.Query, "count", 200);
                    count = Math.Clamp(count, 1, 500);
                    if (request.Query.TryGetValue("chapter", out var chapterText)
                        && int.TryParse(chapterText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chapter))
                    {
                        from = -1;
                        for (var i = 0; i < all.Count; i++)
                        {
                            if (all[i].ChapterIndex == chapter)
                            {
                                from = i;
                                break;
                            }
                        }

                        if (from < 0)
                        {
                            from = 0;
                        }
                    }

                    from = Math.Clamp(from, 0, all.Count);
                    var take = Math.Min(count, Math.Max(0, all.Count - from));
                    var slice = new List<object>(take);
                    for (var i = 0; i < take; i++)
                    {
                        var segment = all[from + i];
                        slice.Add(new
                        {
                            i = segment.SegmentIndex,
                            chapterIndex = segment.ChapterIndex,
                            chapterTitle = segment.ChapterTitle,
                            text = segment.Text,
                            charOffset = segment.CharOffset
                        });
                    }

                    await SendJsonAsync(stream, 200, new
                    {
                        bookId = book.Id,
                        total = all.Count,
                        from,
                        count = take,
                        hasMore = from + take < all.Count,
                        segments = slice
                    }, method == "HEAD", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (bookAction == "progress")
                {
                    var progress = _catalog.GetProgress(bookId);
                    await SendJsonAsync(stream, 200, new { progress }, method == "HEAD", cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            if (TryMatchMediaPath(path, out var mediaBookId, out var mediaIndex))
            {
                await ServeMediaAsync(stream, mediaBookId, mediaIndex, request, method == "HEAD", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (method == "POST" && path == "/api/progress")
        {
            await SaveProgressAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendJsonAsync(stream, 404, new { error = "not found", path }, headOnly: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SaveProgressAsync(NetworkStream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        ProgressRequest? body;
        try
        {
            body = JsonSerializer.Deserialize<ProgressRequest>(request.Body, JsonOptions);
        }
        catch (JsonException ex)
        {
            await SendJsonAsync(stream, 400, new { error = "invalid json: " + ex.Message }, headOnly: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (body is null || string.IsNullOrWhiteSpace(body.BookId))
        {
            await SendJsonAsync(stream, 400, new { error = "bookId is required" }, headOnly: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!BookIdPattern.IsMatch(body.BookId))
        {
            await SendJsonAsync(stream, 400, new { error = "invalid bookId" }, headOnly: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        Book book;
        try
        {
            book = await _catalog.GetBookAsync(body.BookId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await SendJsonAsync(stream, 404, new { error = "book not found" }, headOnly: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var total = book.SegmentCount;
        if (body.SegmentIndex < 0 || body.SegmentIndex >= total)
        {
            await SendJsonAsync(stream, 400, new { error = "segmentIndex out of range" }, headOnly: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (body.ChapterIndex < 0 || body.ChapterIndex >= Math.Max(1, book.Chapters.Count))
        {
            await SendJsonAsync(stream, 400, new { error = "chapterIndex out of range" }, headOnly: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var speed = body.Speed;
        if (double.IsNaN(speed) || speed < 0.5 || speed > 2.0)
        {
            speed = 1.0;
        }

        var progress = new ReadingProgress
        {
            BookId = book.Id,
            SegmentIndex = body.SegmentIndex,
            ChapterIndex = body.ChapterIndex,
            PositionSeconds = body.PositionSeconds < 0 || double.IsNaN(body.PositionSeconds) ? 0 : body.PositionSeconds,
            Speed = speed,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _catalog.SaveProgress(progress);
        await SendJsonAsync(stream, 200, new { ok = true }, headOnly: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task ServeMediaAsync(
        NetworkStream stream,
        string bookId,
        int segmentIndex,
        HttpRequest request,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        string audioPath;
        try
        {
            audioPath = await _catalog.EnsureSegmentAudioAsync(bookId, segmentIndex, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteLog("[Reading] TTS/缓存失败 " + bookId + "#" + segmentIndex + " " + ex.Message);
            await SendJsonAsync(stream, 500, new { error = "audio unavailable: " + ex.Message }, headOnly: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            await SendJsonAsync(stream, 404, new { error = "audio not found" }, headOnly: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var total = new FileInfo(audioPath).Length;
        var hasRange = request.Headers.TryGetValue("range", out var rangeText) && !string.IsNullOrWhiteSpace(rangeText);
        long start = 0;
        long end = total - 1;
        var partial = false;
        if (hasRange)
        {
            if (!TryParseRange(rangeText!, total, out start, out end))
            {
                await SendResponseAsync(stream, 416, "Range Not Satisfiable", "text/plain", Array.Empty<byte>(),
                    new Dictionary<string, string> { ["Content-Range"] = "bytes */" + total.ToString(CultureInfo.InvariantCulture) },
                    headOnly: true, cancellationToken).ConfigureAwait(false);
                return;
            }

            partial = true;
        }

        if (total <= 0)
        {
            await SendResponseAsync(stream, 200, "OK", "audio/mpeg", Array.Empty<byte>(),
                new Dictionary<string, string> { ["Accept-Ranges"] = "bytes", ["Content-Length"] = "0" },
                headOnly, cancellationToken).ConfigureAwait(false);
            return;
        }

        var length = end - start + 1;
        var headersOut = new Dictionary<string, string>
        {
            ["Accept-Ranges"] = "bytes",
            ["Content-Length"] = length.ToString(CultureInfo.InvariantCulture),
            ["Cache-Control"] = "private, max-age=3600"
        };
        if (partial)
        {
            headersOut["Content-Range"] = "bytes " + start.ToString(CultureInfo.InvariantCulture) + "-"
                + end.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);
        }

        await SendResponseHeadAsync(stream, partial ? 206 : 200, partial ? "Partial Content" : "OK", "audio/mpeg", headersOut)
            .ConfigureAwait(false);
        if (headOnly)
        {
            return;
        }

        try
        {
            using var file = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            file.Position = start;
            var buffer = new byte[CopyBufferBytes];
            var remaining = length;
            while (remaining > 0)
            {
                var want = (int)Math.Min(buffer.Length, remaining);
                var read = await file.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // 手机拖动进度/断线重连会主动断开连接，属正常。
        }
    }

    private async Task SendJsonAsync(NetworkStream stream, int status, object payload, bool headOnly, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await SendResponseAsync(stream, status, ReasonPhrase(status), "application/json; charset=utf-8", body,
            new Dictionary<string, string> { ["Cache-Control"] = "no-store" }, headOnly, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendResponseAsync(
        NetworkStream stream,
        int status,
        string reason,
        string contentType,
        byte[] body,
        Dictionary<string, string>? headers,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        var outHeaders = headers is null ? new Dictionary<string, string>() : new Dictionary<string, string>(headers);
        outHeaders["Content-Type"] = contentType;
        if (!outHeaders.ContainsKey("Content-Length"))
        {
            outHeaders["Content-Length"] = body.Length.ToString(CultureInfo.InvariantCulture);
        }

        await SendResponseHeadAsync(stream, status, reason, contentType, outHeaders).ConfigureAwait(false);
        if (!headOnly && body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendResponseHeadAsync(
        NetworkStream stream,
        int status,
        string reason,
        string contentType,
        Dictionary<string, string> headers)
    {
        var builder = new StringBuilder(256);
        builder.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        builder.Append("Connection: close\r\n");
        foreach (var pair in headers)
        {
            builder.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
        }

        var bytes = Encoding.ASCII.GetBytes(builder.ToString() + "\r\n");
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeaderBytes];
        var used = 0;
        var headerEnd = -1;
        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            used += read;
            headerEnd = IndexOfHeaderEnd(buffer, used);
            if (headerEnd >= 0)
            {
                break;
            }
        }

        if (headerEnd <= 0)
        {
            return null;
        }

        var headerText = Encoding.UTF8.GetString(buffer, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
        {
            return null;
        }

        var method = requestLine[0].ToUpperInvariant();
        var target = requestLine[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
        }

        var contentLength = 0;
        if (headers.TryGetValue("content-length", out var contentLengthText))
        {
            if (!int.TryParse(contentLengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength)
                || contentLength < 0)
            {
                return null;
            }

            if (contentLength > MaxBodyBytes)
            {
                return null;
            }
        }

        var body = new byte[contentLength];
        var already = used - (headerEnd + 4);
        if (already > 0)
        {
            Array.Copy(buffer, headerEnd + 4, body, 0, Math.Min(already, contentLength));
        }

        var bodyRead = Math.Min(already, contentLength);
        while (bodyRead < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(bodyRead, contentLength - bodyRead), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            bodyRead += read;
        }

        var httpIndex = target.IndexOf("://", StringComparison.Ordinal);
        if (httpIndex >= 0)
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
            {
                target = absolute.PathAndQuery;
            }
        }

        var queryIndex = target.IndexOf('?');
        var rawPath = queryIndex >= 0 ? target.Substring(0, queryIndex) : target;
        var rawQuery = queryIndex >= 0 ? target.Substring(queryIndex + 1) : string.Empty;
        string path;
        try
        {
            path = Uri.UnescapeDataString(rawPath);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (path.Length == 0)
        {
            path = "/";
        }

        if (path == "/reader" || path.StartsWith("/reader/", StringComparison.Ordinal))
        {
            path = path.Length == "/reader".Length ? "/" : path.Substring("/reader".Length);
        }

        if (!path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rawQuery.Length > 0)
        {
            foreach (var pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = eq >= 0 ? pair.Substring(0, eq) : pair;
                var value = eq >= 0 ? pair.Substring(eq + 1) : string.Empty;
                try
                {
                    query[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
                }
                catch (UriFormatException)
                {
                }
            }
        }

        return new HttpRequest(method, path, query, headers, body);
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return i - 3;
            }
        }

        return -1;
    }

    private static bool TryMatchBookPath(string path, out string bookId, out string action)
    {
        bookId = string.Empty;
        action = string.Empty;
        if (!path.StartsWith("/api/books/", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = path.Substring("/api/books/".Length);
        var slash = rest.IndexOf('/');
        if (slash < 0)
        {
            bookId = rest;
        }
        else
        {
            bookId = rest.Substring(0, slash);
            action = rest.Substring(slash + 1);
        }

        return BookIdPattern.IsMatch(bookId) && (action.Length == 0 || action is "segments" or "progress");
    }

    private static bool TryMatchMediaPath(string path, out string bookId, out int segmentIndex)
    {
        bookId = string.Empty;
        segmentIndex = -1;
        if (!path.StartsWith("/media/", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = path.Substring("/media/".Length).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[1].EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var indexText = parts[1].Substring(0, parts[1].Length - 4);
        if (!BookIdPattern.IsMatch(parts[0])
            || !int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out segmentIndex)
            || segmentIndex < 0)
        {
            return false;
        }

        bookId = parts[0];
        return true;
    }

    private static List<object> BuildChapters(Book book, IReadOnlyList<Segment> segments)
    {
        var result = new List<object>();
        var start = 0;
        while (start < segments.Count)
        {
            var chapterIndex = segments[start].ChapterIndex;
            var count = 0;
            var first = segments[start].SegmentIndex;
            while (start + count < segments.Count && segments[start + count].ChapterIndex == chapterIndex)
            {
                count++;
            }

            var title = chapterIndex >= 0 && chapterIndex < book.Chapters.Count
                ? book.Chapters[chapterIndex].Title
                : segments[start].ChapterTitle;
            result.Add(new
            {
                index = chapterIndex,
                title,
                firstSegmentIndex = first,
                segmentCount = count
            });
            start += count;
        }

        return result;
    }

    private static int ParseInt(Dictionary<string, string> query, string key, int fallback)
    {
        return query.TryGetValue(key, out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static bool TryParseRange(string value, long total, out long start, out long end)
    {
        start = 0;
        end = 0;
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || trimmed.Contains(','))
        {
            return false;
        }

        var spec = trimmed.Substring("bytes=".Length).Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var startText = spec.Substring(0, dash).Trim();
        var endText = spec.Substring(dash + 1).Trim();
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, total - suffix);
            end = total - 1;
            return total > 0;
        }

        if (!long.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) || start < 0)
        {
            return false;
        }

        if (endText.Length == 0)
        {
            end = total - 1;
        }
        else if (!long.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out end) || end < start)
        {
            return false;
        }

        if (start >= total)
        {
            return false;
        }

        end = Math.Min(end, total - 1);
        return true;
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        206 => "Partial Content",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        416 => "Range Not Satisfiable",
        500 => "Internal Server Error",
        _ => "OK"
    };

    private static string? LoadPageFromAssembly()
    {
        var assembly = typeof(ReadingHttpServer).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(item => item.EndsWith("ReaderPage.html", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void WriteLog(string message)
    {
        try
        {
            Log?.Invoke(message);
        }
        catch
        {
        }
    }

    private sealed record HttpRequest(
        string Method,
        string Path,
        Dictionary<string, string> Query,
        Dictionary<string, string> Headers,
        byte[] Body);

    private sealed class ProgressRequest
    {
        public string BookId { get; set; } = "";

        public int SegmentIndex { get; set; }

        public int ChapterIndex { get; set; }

        public double PositionSeconds { get; set; }

        public double Speed { get; set; } = 1.0;
    }
}
