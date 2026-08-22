using ShoreKeeper.Host.Wpf.Audio;
using ShoreKeeper.Host.Wpf.Communication;
using ShoreKeeper.Host.Wpf.OpenAI;
using ShoreKeeper.Host.Wpf.Reminders;
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
        private readonly ReminderService _reminderService = new();
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
            _reminderService.ReminderFired += OnReminderFired;
            RefreshRemindersList();
            _reminderService.Start();
            AddLog($"Reminders store: {_reminderService.StoreFilePath}");
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

        private void AddMonthlyReminderButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(MonthlyDayTextBox.Text.Trim(), out var day) || day < 1 || day > 31)
            {
                AddLog("每月提醒：日期必须是 1-31 之间的数字");
                return;
            }

            if (!TimeOnly.TryParse(MonthlyTimeTextBox.Text.Trim(), out var time))
            {
                AddLog("每月提醒：时间格式不正确，请使用 HH:mm（如 10:00）");
                return;
            }

            _reminderService.Add(new ReminderItem
            {
                Title = GetReminderTitle(),
                Kind = ReminderKind.Monthly,
                DayOfMonth = day,
                TimeOfDay = time
            });
            RefreshRemindersList();
            AddLog($"已添加每月提醒：每月 {day} 日 {time:HH:mm}");
        }

        private void AddOneTimeReminderButton_Click(object sender, RoutedEventArgs e)
        {
            if (OneTimeDatePicker.SelectedDate is not { } date)
            {
                AddLog("具体日期提醒：请先选择日期");
                return;
            }

            if (!TimeOnly.TryParse(OneTimeTimeTextBox.Text.Trim(), out var time))
            {
                AddLog("具体日期提醒：时间格式不正确，请使用 HH:mm（如 10:00）");
                return;
            }

            var at = date.Date + time.ToTimeSpan();
            if (at <= DateTime.Now)
            {
                AddLog("具体日期提醒：触发时间必须晚于当前时间");
                return;
            }

            _reminderService.Add(new ReminderItem
            {
                Title = GetReminderTitle(),
                Kind = ReminderKind.OneTime,
                SpecificDateTime = at
            });
            RefreshRemindersList();
            AddLog($"已添加单次提醒：{at:yyyy-MM-dd HH:mm}");
        }

        private void RemoveReminderButton_Click(object sender, RoutedEventArgs e)
        {
            if (RemindersListBox.SelectedItem is not ReminderItem selected)
            {
                AddLog("请先在列表中选择要删除的提醒");
                return;
            }

            _reminderService.Remove(selected);
            RefreshRemindersList();
        }

        private string GetReminderTitle()
        {
            var title = ReminderTitleTextBox.Text.Trim();
            return string.IsNullOrWhiteSpace(title) ? "领取 Unreal 免费资产" : title;
        }

        private void RefreshRemindersList()
        {
            RemindersListBox.Items.Clear();
            foreach (var item in _reminderService.Reminders)
            {
                RemindersListBox.Items.Add(item);
            }
        }

        private void OnReminderFired(ReminderItem item)
        {
            Dispatcher.Invoke(async () =>
            {
                RefreshRemindersList();
                AddLog($"提醒触发：{item}");

                var message = new AssistantMessage
                {
                    Source = "host",
                    Type = "reminder",
                    Text = $"⏰ {item.Title}"
                };
                AddMessage(message);
                await _server.BroadcastAsync(message);

                MessageBox.Show(this, item.Title, "Unreal 免费资产提醒",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            });
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
            _reminderService.Stop();
            _recorder.Dispose();
            await _server.DisposeAsync();
        }
    }
}
