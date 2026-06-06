using ShoreKeeper.Host.Wpf.Audio;
using ShoreKeeper.Host.Wpf.Communication;
using ShoreKeeper.Host.Wpf.OpenAI;
using System.Windows;
using System.Windows.Input;

namespace ShoreKeeper.Host.Wpf
{
    public partial class MainWindow : Window
    {
        private readonly TcpMessageServer _server = new();
        private readonly AudioRecordingService _recorder = new();
        private readonly OpenAITranscriptionService _transcriptionService = new();
        private readonly LocalWhisperTranscriptionService _localWhisperTranscriptionService = new();
        private string? _lastRecordingFilePath;

        public MainWindow()
        {
            InitializeComponent();
            _server.MessageReceived += OnMessageReceived;
            _server.LogReceived += OnLogReceived;
            _server.ClientCountChanged += OnClientCountChanged;
            _recorder.LogReceived += OnLogReceived;
            _recorder.RecordingStopped += OnRecordingStopped;
            RecordingsFolderTextBlock.Text = $"Folder: {_recorder.RecordingsDirectory}";
            Closed += MainWindow_Closed;
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
            });
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
