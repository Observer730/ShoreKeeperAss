using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class ShoreKeeperHostClient : MonoBehaviour
{
    [SerializeField] private string host = "127.0.0.1";
    [SerializeField] private int port = 47621;
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private string testMessage = "来自 Unity Client 的测试消息";

    private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();

    private TcpClient _client;
    private StreamReader _reader;
    private StreamWriter _writer;
    private CancellationTokenSource _cts;

    public bool IsConnected
    {
        get { return _client != null && _client.Connected; }
    }

    private async void Start()
    {
        if (connectOnStart)
        {
            await ConnectAsync();
        }
    }

    private void Update()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            action.Invoke();
        }

        if (Input.GetKeyDown(KeyCode.F8))
        {
            _ = SendChatAsync(testMessage);
        }
    }

    private async void OnDestroy()
    {
        await DisconnectAsync();
    }

    public async Task ConnectAsync()
    {
        if (IsConnected)
        {
            return;
        }

        _cts = new CancellationTokenSource();

        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(host, port);
            _client.NoDelay = true;

            NetworkStream stream = _client.GetStream();
            _reader = new StreamReader(stream, Encoding.UTF8);
            _writer = new StreamWriter(stream, new UTF8Encoding(false))
            {
                AutoFlush = true
            };

            Debug.Log("[ShoreKeeper] Connected to host.");
            _ = ReceiveLoopAsync(_cts.Token);
            await SendAsync(AssistantMessage.Create("unity", "hello", "Unity client connected"));
        }
        catch (Exception ex)
        {
            Debug.LogError("[ShoreKeeper] Connect failed: " + ex.Message);
            await DisconnectAsync();
        }
    }

    public Task DisconnectAsync()
    {
        _cts?.Cancel();
        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Close();

        _reader = null;
        _writer = null;
        _client = null;

        _cts?.Dispose();
        _cts = null;

        return Task.CompletedTask;
    }

    [ContextMenu("Send Test Message")]
    public async void SendTestMessage()
    {
        await SendChatAsync(testMessage);
    }

    [ContextMenu("Connect To Host")]
    private async void ConnectFromInspector()
    {
        await ConnectAsync();
    }

    [ContextMenu("Disconnect From Host")]
    private async void DisconnectFromInspector()
    {
        await DisconnectAsync();
    }

    public Task SendChatAsync(string text)
    {
        return SendAsync(AssistantMessage.Create("unity", "chat", text));
    }

    public async Task SendAsync(AssistantMessage message)
    {
        if (!IsConnected || _writer == null)
        {
            Debug.LogWarning("[ShoreKeeper] Not connected. Start WPF host first.");
            return;
        }

        string json = JsonUtility.ToJson(message);
        await _writer.WriteLineAsync(json);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _reader != null)
            {
                string line = await _reader.ReadLineAsync();
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                AssistantMessage message = JsonUtility.FromJson<AssistantMessage>(line);
                _mainThreadActions.Enqueue(() => OnHostMessage(message));
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException ex)
        {
            Debug.LogWarning("[ShoreKeeper] Connection closed: " + ex.Message);
        }
        catch (Exception ex)
        {
            Debug.LogError("[ShoreKeeper] Receive failed: " + ex.Message);
        }
    }

    private void OnHostMessage(AssistantMessage message)
    {
        Debug.LogFormat("[ShoreKeeper] Host message {0}/{1}: {2}", message.source, message.type, message.text);
    }
}
