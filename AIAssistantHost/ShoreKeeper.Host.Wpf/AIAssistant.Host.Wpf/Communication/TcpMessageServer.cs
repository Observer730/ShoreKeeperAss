using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AIAssistant.Host.Wpf.Communication;

public sealed class TcpMessageServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
    private readonly object _lifetimeLock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public event Action<AssistantMessage>? MessageReceived;
    public event Action<string>? LogReceived;
    public event Action<int>? ClientCountChanged;

    public int Port { get; private set; }
    public bool IsRunning => _listener is not null;
    public int ClientCount => _clients.Count;

    public Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        lock (_lifetimeLock)
        {
            if (_listener is not null)
            {
                return Task.CompletedTask;
            }

            Port = port;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _ = AcceptLoopAsync(_cts.Token);
        }

        Log($"Server listening on 127.0.0.1:{port}");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;

        lock (_lifetimeLock)
        {
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
        }

        if (listener is null)
        {
            return;
        }

        cts?.Cancel();
        listener.Stop();

        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync();
        }

        _clients.Clear();
        ClientCountChanged?.Invoke(0);
        cts?.Dispose();
        Log("Server stopped");
    }

    public async Task BroadcastAsync(AssistantMessage message, CancellationToken cancellationToken = default)
    {
        message.Source = string.IsNullOrWhiteSpace(message.Source) ? "host" : message.Source;
        message.UtcTime = DateTimeOffset.UtcNow;

        var line = JsonSerializer.Serialize(message, JsonOptions) + "\n";
        var deadClients = new List<Guid>();

        foreach (var pair in _clients)
        {
            try
            {
                await pair.Value.SendAsync(line, cancellationToken);
            }
            catch (Exception ex)
            {
                deadClients.Add(pair.Key);
                Log($"Send failed: {ex.Message}");
            }
        }

        foreach (var id in deadClients)
        {
            await RemoveClientAsync(id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener!.AcceptTcpClientAsync(cancellationToken);
                tcpClient.NoDelay = true;

                var id = Guid.NewGuid();
                var connection = new ClientConnection(tcpClient);
                _clients[id] = connection;
                ClientCountChanged?.Invoke(_clients.Count);
                Log($"Client connected: {id:N}");
                _ = ReceiveLoopAsync(id, connection, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Accept failed: {ex.Message}");
            }
        }
    }

    private async Task ReceiveLoopAsync(Guid id, ClientConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await connection.Reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var message = JsonSerializer.Deserialize<AssistantMessage>(line, JsonOptions);
                    if (message is null)
                    {
                        Log("Received empty message");
                        continue;
                    }

                    MessageReceived?.Invoke(message);
                }
                catch (JsonException ex)
                {
                    Log($"Invalid json: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            Log($"Client IO closed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log($"Receive failed: {ex.Message}");
        }
        finally
        {
            await RemoveClientAsync(id);
        }
    }

    private async Task RemoveClientAsync(Guid id)
    {
        if (_clients.TryRemove(id, out var connection))
        {
            await connection.DisposeAsync();
            ClientCountChanged?.Invoke(_clients.Count);
            Log($"Client disconnected: {id:N}");
        }
    }

    private void Log(string text)
    {
        LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {text}");
    }

    private sealed class ClientConnection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public ClientConnection(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
            Reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
            _writer = new StreamWriter(_stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
        }

        public StreamReader Reader { get; }

        public async Task SendAsync(string line, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteAsync(line.AsMemory(), cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _sendLock.WaitAsync();
            try
            {
                Reader.Dispose();
                await _writer.DisposeAsync();
                await _stream.DisposeAsync();
                _client.Dispose();
            }
            finally
            {
                _sendLock.Release();
                _sendLock.Dispose();
            }
        }
    }
}
