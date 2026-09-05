using AIAssistant.Host.Wpf.Audio;
using AIAssistant.Host.Wpf.Communication;
using AIAssistant.Host.Wpf.MiniMax;
using AIAssistant.Host.Wpf.OpenAI;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Input;

namespace AIAssistant.Host.Wpf
{
    public partial class MainWindow : Window
    {
        private readonly TcpMessageServer _server = new();
        private readonly AudioRecordingService _recorder = new();
        private readonly OpenAITranscriptionService _transcriptionService = new();
        private readonly LocalWhisperTranscriptionService _localWhisperTranscriptionService = new();
        private readonly MiniMaxChatService _chatService = new();
        private readonly MiniMaxTtsService _ttsService = new();
        private readonly ConcurrentDictionary<string, List<(string role, string content)>> _sessions = new();
        private string? _lastRecordingFilePath;

        public MainWindow()
        {
            InitializeComponent();
            _server.MessageReceived += OnMessageReceived;
            _server.LogReceived += OnLogReceived;
            _server.ClientCountChanged += OnClientCountChanged;
            _recorder.LogReceived += OnLogReceived;
            _recorder.RecordingStopped += OnRecordingStopped;
            _ttsService.OutputDirectory = System.IO.Path.Combine(_recorder.RecordingsDirectory, "TTS");
            RecordingsFolderTextBlock.Text = $"Folder: {_recorder.RecordingsDirectory}";
            Closed += MainWindow_Closed;
            VoiceStatusTextBlock.Text = $"当前 voice: {_ttsService.VoiceId}";
            CustomVoiceIdTextBox.IsEnabled = false;  // 默认非 Custom,禁用

            // medium 模型切换 -> LocalWhisperTranscriptionService
            UseMediumModelCheckBox.Checked += (_, _) => _localWhisperTranscriptionService.ModelName = "ggml-medium.bin";
            UseMediumModelCheckBox.Unchecked += (_, _) => _localWhisperTranscriptionService.ModelName = null;

            // 手动初始化 voice 到 ComboBox 第 0 项(避免 XAML 在 InitializeComponent 阶段触发 SelectionChanged 导致 null ref)
            if (VoiceComboBox.Items.Count > 0 && VoiceComboBox.SelectedIndex < 0)
            {
                VoiceComboBox.SelectedIndex = 0;
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PortTextBox.Text, out var port) || port <= 0 || port > 65535)
            {
                AddLog("Port must be between 1 and 65535");
                return;
            }

            try
            {
                await _server.StartAsync(port);
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                SendButton.IsEnabled = true;
                PortTextBox.IsEnabled = false;
                UpdateStatus();
            }
            catch (Exception ex)
            {
                AddLog($"Start failed: {ex.Message}");
            }
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            await _server.StopAsync();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            SendButton.IsEnabled = false;
            PortTextBox.IsEnabled = true;
            UpdateStatus();
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendHostMessageAsync();
        }

        private async void MessageTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && SendButton.IsEnabled)
            {
                e.Handled = true;
                await SendHostMessageAsync();
            }
        }

        private async Task SendHostMessageAsync()
        {
            var text = MessageTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var message = new AssistantMessage
            {
                Source = "host",
                Type = "chat",
                Text = text
            };

            await _server.BroadcastAsync(message);
            AddMessage(message);
            MessageTextBox.SelectAll();
            MessageTextBox.Focus();
        }

        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var filePath = _recorder.StartRecording();
                _lastRecordingFilePath = filePath;
                RecordButton.IsEnabled = false;
                StopRecordButton.IsEnabled = true;
                TranscribeButton.IsEnabled = false;
                RecordingStatusTextBlock.Text = $"Recording: {filePath}";
            }
            catch (Exception ex)
            {
                AddLog($"Record failed: {ex.Message}");
            }
        }

        private void StopRecordButton_Click(object sender, RoutedEventArgs e)
        {
            _recorder.StopRecording();
            StopRecordButton.IsEnabled = false;
            RecordingStatusTextBlock.Text = "Stopping recorder...";
        }

        private async void TranscribeButton_Click(object sender, RoutedEventArgs e)
        {
            await TranscribeLastRecordingAsync();
        }

        private async Task TranscribeLastRecordingAsync()
        {
            if (string.IsNullOrWhiteSpace(_lastRecordingFilePath))
            {
                AddLog("No recording file to transcribe.");
                return;
            }

            TranscribeButton.IsEnabled = false;
            TranscriptTextBox.Text = "Transcribing...";

            try
            {
                var transcript = await TranscribeWithSelectedProviderAsync(_lastRecordingFilePath);
                TranscriptTextBox.Text = transcript;
                AddLog($"Transcript: {transcript}");

                await _server.BroadcastAsync(new AssistantMessage
                {
                    Source = "host",
                    Type = "transcript",
                    Text = transcript
                });

                // WPF 端自己转录产生的 transcript,不会走 OnMessageReceived,
                // 所以在转录完成时直接触发 LLM。
                await HandleUserUtteranceAsync(new AssistantMessage
                {
                    Source = "host",
                    Type = "transcript",
                    Text = transcript,
                    Role = "user",
                    ConversationId = "default",
                });
            }
            catch (OpenAIServiceException ex)
            {
                TranscriptTextBox.Text = ex.Message;
                AddLog($"Transcribe failed: {ex.Message}");
                AddLog(ex.Detail);
            }
            catch (Exception ex)
            {
                TranscriptTextBox.Text = ex.Message;
                AddLog($"Transcribe failed: {ex.Message}");
            }
            finally
            {
                TranscribeButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastRecordingFilePath);
            }
        }

        private Task<string> TranscribeWithSelectedProviderAsync(string filePath)
        {
            var selectedProvider = (TranscriptionProviderComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString();
            if (selectedProvider == "OpenAI")
            {
                AddLog("Transcribing with OpenAI...");
                return _transcriptionService.TranscribeAsync(filePath);
            }

            AddLog("Transcribing with Local Whisper...");
            return _localWhisperTranscriptionService.TranscribeAsync(filePath);
        }

        private void OnMessageReceived(AssistantMessage message)
        {
            Dispatcher.Invoke(() => AddMessage(message));

            // 新增:收到 Unity 端的 transcript,自动调 M3
            if (message.Source == "unity" && message.Type == "transcript" && !string.IsNullOrWhiteSpace(message.Text))
            {
                _ = HandleUserUtteranceAsync(message);
            }
        }

        private async Task HandleUserUtteranceAsync(AssistantMessage userMsg)
        {
            var convId = string.IsNullOrWhiteSpace(userMsg.ConversationId) ? "default" : userMsg.ConversationId;
            var history = _sessions.GetOrAdd(convId, _ => new List<(string, string)>());

            history.Add(("user", userMsg.Text));

            try
            {
                AddLog($"[LLM] → M3 ({_chatService.Model})");
                var reply = await _chatService.ChatAsync(convId, history);
                AddLog($"[LLM] ← {reply}");

                history.Add(("assistant", reply));

                // 1) 清洗掉 M3 回复里混入的 `` 思考块(防 TTS 读出 think 标签)
                var cleanReply = MiniMaxTtsService.StripThinkBlocks(reply);

                // 2) 调 MiniMax TTS 落 mp3,拿到本地绝对路径
                string? audioPath = null;
                try
                {
                    AddLog($"[TTS] → {cleanReply}");
                    audioPath = await _ttsService.SynthesizeAsync(cleanReply);
                    AddLog($"[TTS] ← {audioPath}");
                }
                catch (MiniMaxServiceException ex)
                {
                    AddLog($"[TTS] failed: {ex.Message}");
                    AddLog(ex.Detail ?? "");
                }
                catch (Exception ex)
                {
                    AddLog($"[TTS] error: {ex.Message}");
                }

                // 3) 广播给 Unity(同时带 text 和 audioPath;audioPath 为空时 Unity 仅显示文字)
                await _server.BroadcastAsync(new AssistantMessage
                {
                    Source = "assistant",
                    Type = string.IsNullOrWhiteSpace(audioPath) ? "chat" : "audio",
                    Role = "assistant",
                    ConversationId = convId,
                    Text = cleanReply,
                    AudioPath = string.IsNullOrWhiteSpace(audioPath)
                        ? string.Empty
                        : new Uri(audioPath).AbsoluteUri,
                });
            }
            catch (MiniMaxServiceException ex)
            {
                AddLog($"[LLM] failed: {ex.Message}");
                AddLog(ex.Detail ?? "");
            }
            catch (Exception ex)
            {
                AddLog($"[LLM] error: {ex.Message}");
            }
        }

        private void OnLogReceived(string text)
        {
            Dispatcher.Invoke(() => AddLog(text));
        }

        private void OnClientCountChanged(int _)
        {
            Dispatcher.Invoke(UpdateStatus);
        }

        private void OnRecordingStopped(string filePath)
        {
            Dispatcher.Invoke(() =>
            {
                _lastRecordingFilePath = filePath;
                RecordButton.IsEnabled = true;
                StopRecordButton.IsEnabled = false;
                TranscribeButton.IsEnabled = !string.IsNullOrWhiteSpace(filePath);
                RecordingStatusTextBlock.Text = string.IsNullOrWhiteSpace(filePath)
                    ? "Recorder idle"
                    : $"Saved: {filePath}";

                if (!string.IsNullOrWhiteSpace(filePath) && TranscribeButton.IsEnabled)
                {
                    // 自动转录
                    _ = TranscribeLastRecordingAsync();
                }
            });
        }

        private static readonly string[] VoiceDisplayNames = new[]
        {
            "danya_xuejie",
            "Chinese (Mandarin)_Crisp_Girl",
            "female-yujie",
            "Chinese (Mandarin)_Gentle_Senior",
            "Chinese (Mandarin)_Soft_Girl",
            "male-qn-qingse",
        };

        private void VoiceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (VoiceComboBox == null) return;  // 初始化时为 null
            if (CustomVoiceIdTextBox == null) return;  // 防御:InitializeComponent 期间触发时,TextBox 还没实例化
            if (VoiceStatusTextBlock == null) return;  // 同上
            if (VoiceComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;

            var content = item.Content?.ToString() ?? string.Empty;
            // 形如 "danya_xuejie (淡雅学姐 - 清冷)" — 取第一个空格之前的 voice_id
            var voiceId = content.Split(' ')[0].Trim();

            if (voiceId == "Custom...")
            {
                CustomVoiceIdTextBox.IsEnabled = true;
                voiceId = CustomVoiceIdTextBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(voiceId))
                {
                    AddLog("[Voice] Custom voice_id is empty. Fill the textbox first.");
                    return;
                }
            }
            else
            {
                CustomVoiceIdTextBox.IsEnabled = false;
            }

            _ttsService.VoiceId = voiceId;
            VoiceStatusTextBlock.Text = $"当前 voice: {voiceId}";
            AddLog($"[Voice] → {voiceId}");
        }

        private async void CloneVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            // 1) 选音频文件
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要复刻的音频(mp3/m4a/wav, 10 秒-5 分钟, ≤20MB)",
                Filter = "音频文件 (*.mp3;*.m4a;*.wav)|*.mp3;*.m4a;*.wav|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;

            // 2) 决定 voice_id
            var customId = CustomVoiceIdTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(customId))
            {
                AddLog("[Clone] voice_id 文本框为空,无法克隆。");
                return;
            }
            // 命名规则:8-256 字符,首字母英文,允许字母数字和 -_
            if (customId.Length < 8 || customId.Length > 256 || !char.IsLetter(customId[0]))
            {
                AddLog("[Clone] voice_id 须满足:8-256 字符,首字符英文字母。已自动生成。");
                customId = $"my_voice_{DateTime.UtcNow:yyyyMMddHHmmss}";
                CustomVoiceIdTextBox.Text = customId;
            }

            CloneVoiceButton.IsEnabled = false;
            try
            {
                AddLog($"[Clone] 上传源音频: {dlg.FileName}");
                var cloneService = new MiniMaxVoiceCloneService();
                var newVoiceId = await cloneService.CloneFromFileAsync(dlg.FileName, customId);
                AddLog($"[Clone] 成功:voice_id = {newVoiceId}");

                _ttsService.VoiceId = newVoiceId;
                VoiceStatusTextBlock.Text = $"当前 voice: {newVoiceId} (复刻)";
                VoiceComboBox.SelectedIndex = -1;  // 让 ComboBox 不显示任何项,TextBox 控制
                AddLog($"[Clone] 已切到复刻 voice。下次 M3 回复就会用它。");
            }
            catch (MiniMaxServiceException ex)
            {
                AddLog($"[Clone] failed: {ex.Message}");
                AddLog(ex.Detail ?? "");
            }
            catch (Exception ex)
            {
                AddLog($"[Clone] error: {ex.Message}");
            }
            finally
            {
                CloneVoiceButton.IsEnabled = true;
            }
        }

        private void AddMessage(AssistantMessage message)
        {
            MessagesListBox.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message.Source}/{message.Type}: {message.Text}");
            MessagesListBox.ScrollIntoView(MessagesListBox.Items[^1]);
        }

        private void AddLog(string text)
        {
            LogsTextBox.AppendText(text);
            LogsTextBox.AppendText(Environment.NewLine);
            LogsTextBox.ScrollToEnd();
        }

        private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
        {
            LogsTextBox.Clear();
        }

        private void UpdateStatus()
        {
            StatusTextBlock.Text = _server.IsRunning
                ? $"Listening - clients: {_server.ClientCount}"
                : "Stopped";
        }

        private async void MainWindow_Closed(object? sender, EventArgs e)
        {
            _recorder.Dispose();
            await _server.DisposeAsync();
        }
    }
}
