using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AIAssistant.Host.Wpf.Reading;
using Microsoft.Win32;

namespace AIAssistant.Host.Wpf;

public partial class ReadingWindow : Window
{
    private static readonly string[] SpeedChoices = ["0.75", "1.0", "1.25", "1.5", "2.0"];

    private readonly ReadingService _service;
    private readonly DispatcherTimer _timer;
    private ReadingQueue? _queue;
    private int _suppressDepth;
    private bool _opening;
    private int _ticks;

    private bool IsSuppressed => _suppressDepth > 0;

    public ReadingWindow(ReadingService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InitializeComponent();
        foreach (var speed in SpeedChoices)
        {
            SpeedComboBox.Items.Add(speed);
        }

        SpeedComboBox.SelectedItem = "1.0";
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        Loaded += ReadingWindow_Loaded;
    }

    private async void ReadingWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ReloadBooksAsync(null);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = (BookList.SelectedItem as BookRow)?.Info.Id;
            await ReloadBooksAsync(selected);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "打开书籍",
                Filter = "书籍 (*.txt;*.epub)|*.txt;*.epub|文本 (*.txt)|*.txt|EPUB (*.epub)|*.epub"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            _service.Library.Register(dialog.FileName);
            await ReloadBooksAsync(null);
            SelectByPath(dialog.FileName);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async void BookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            await OpenSelectedAsync();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        var queue = _queue;
        if (queue is null)
        {
            return;
        }

        try
        {
            if (queue.IsPlaying)
            {
                await Task.Run(queue.Pause);
            }
            else
            {
                await queue.ResumeAsync();
            }

            ShowTransport();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async void PrevSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        await RunQueueAsync(queue => queue.PreviousSegmentAsync());
    }

    private async void NextSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        await RunQueueAsync(queue => queue.NextSegmentAsync());
    }

    private async void PrevChapterButton_Click(object sender, RoutedEventArgs e)
    {
        await RunQueueAsync(queue => queue.PreviousChapterAsync());
    }

    private async void NextChapterButton_Click(object sender, RoutedEventArgs e)
    {
        await RunQueueAsync(queue => queue.NextChapterAsync());
    }

    private async void ChapterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsSuppressed || _opening || _queue is null || ChapterComboBox.SelectedItem is not Chapter chapter)
        {
            return;
        }

        try
        {
            var current = _queue.CurrentIndex;
            if (current >= 0 && current < _queue.Segments.Count && _queue.Segments[current].ChapterIndex == chapter.Index)
            {
                return;
            }

            var target = -1;
            foreach (var segment in _queue.Segments)
            {
                if (segment.ChapterIndex == chapter.Index)
                {
                    target = segment.SegmentIndex;
                    break;
                }
            }

            if (target >= 0)
            {
                await _queue.SeekToSegmentAsync(target);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsSuppressed || _queue is null || SpeedComboBox.SelectedItem is not string text)
        {
            return;
        }

        try
        {
            var speed = double.Parse(text, CultureInfo.InvariantCulture);
            await _queue.SetSpeedAsync(speed);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async void SegmentSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsSuppressed || _queue is null)
        {
            return;
        }

        try
        {
            var index = (int)Math.Round(SegmentSlider.Value);
            if (index == _queue.CurrentIndex)
            {
                return;
            }

            await _queue.SeekToSegmentAsync(index);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            ShowClock();
            _ticks++;
            if (_ticks % 4 != 0 || _queue is null)
            {
                return;
            }

            var queue = _queue;
            _ = Task.Run(() =>
            {
                try
                {
                    queue.SaveProgress();
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() => SetStatus(ex.Message));
                }
            });
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        var queue = _queue;
        DetachQueue();
        if (queue is not null)
        {
            try
            {
                Task.Run(() =>
                {
                    queue.SaveProgress();
                    queue.Pause();
                }).Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message);
            }
        }

        base.OnClosed(e);
    }

    private async Task ReloadBooksAsync(string? selectId)
    {
        SetBusy(true);
        SetStatus("正在刷新书库…");
        var books = await Task.Run(() => _service.Library.Scan()).ConfigureAwait(true);
        PushSuppress();
        try
        {
            BookList.Items.Clear();
            foreach (var book in books)
            {
                BookList.Items.Add(new BookRow(book));
            }

            if (!string.IsNullOrEmpty(selectId))
            {
                SelectById(selectId);
            }
        }
        finally
        {
            PopSuppress();
            SetBusy(false);
        }

        SetStatus("书库 " + books.Count + " 本。");
    }

    private async Task OpenSelectedAsync()
    {
        if (IsSuppressed || _opening || BookList.SelectedItem is not BookRow row)
        {
            return;
        }

        _opening = true;
        SetBusy(true);
        SetStatus("解析中…");
        try
        {
            var book = await _service.OpenBookAsync(row.Info.Id);
            DetachQueue();
            var queue = await _service.CreateQueueAsync(book.Id);
            _queue = queue;
            queue.StateChanged += OnQueueState;
            queue.CurrentSegmentChanged += OnQueueSegment;
            queue.Log += OnQueueLog;
            var refreshed = _service.Library.Find(book.Id);
            if (refreshed is not null && BookList.SelectedIndex >= 0)
            {
                var index = BookList.SelectedIndex;
                PushSuppress();
                BookList.Items[index] = new BookRow(refreshed);
                BookList.SelectedIndex = index;
                PopSuppress();
            }

            ShowBook(book);
            SetStatus("已打开 " + book.Title);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
        finally
        {
            _opening = false;
            SetBusy(false);
        }
    }

    private async Task RunQueueAsync(Func<ReadingQueue, Task> action)
    {
        var queue = _queue;
        if (queue is null)
        {
            return;
        }

        try
        {
            await action(queue);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void ShowBook(Book book)
    {
        var queue = _queue;
        if (queue is null)
        {
            return;
        }

        PushSuppress();
        try
        {
            BookTitleText.Text = book.Title;
            ChapterComboBox.Items.Clear();
            foreach (var chapter in book.Chapters)
            {
                ChapterComboBox.Items.Add(chapter);
            }

            var index = queue.Segments.Count == 0 ? 0 : Math.Clamp(queue.CurrentIndex, 0, queue.Segments.Count - 1);
            SegmentSlider.Maximum = Math.Max(0, queue.Segments.Count - 1);
            SegmentSlider.Value = index;
            SelectChapter(index);
            SelectSpeed(queue.Speed);
            ShowSegment(index);
            ShowClock();
            PlayButton.Content = queue.IsPlaying ? "暂停" : "播放";
        }
        finally
        {
            PopSuppress();
        }
    }

    private void ShowTransport()
    {
        var queue = _queue;
        if (queue is null)
        {
            return;
        }

        var index = queue.Segments.Count == 0 ? 0 : Math.Clamp(queue.CurrentIndex, 0, queue.Segments.Count - 1);
        if (!SegmentSlider.IsMouseCaptureWithin)
        {
            PushSuppress();
            SegmentSlider.Value = index;
            PopSuppress();
        }

        SelectChapter(index);
        ShowSegment(index);
        ShowClock();
        PlayButton.Content = queue.IsPlaying ? "暂停" : "播放";
    }

    private void ShowSegment(int index)
    {
        var queue = _queue;
        if (queue is null || index < 0 || index >= queue.Segments.Count)
        {
            SegmentTextBox.Text = "";
            return;
        }

        var segment = queue.Segments[index];
        SegmentTextBox.Text = segment.ChapterTitle + "\n\n" + segment.Text;
    }

    private void ShowClock()
    {
        var queue = _queue;
        if (queue is null)
        {
            TimeText.Text = "00:00 / 00:00";
            return;
        }

        var progress = queue.CurrentProgress;
        var count = Math.Max(1, queue.Segments.Count);
        TimeText.Text = "第 " + (progress.SegmentIndex + 1) + "/" + count + " 段    "
            + FormatClock(progress.PositionSeconds) + " / " + FormatClock(queue.DurationSeconds);
        PlayButton.Content = queue.IsPlaying ? "暂停" : "播放";
    }

    private void SelectChapter(int segmentIndex)
    {
        var queue = _queue;
        if (queue is null || segmentIndex < 0 || segmentIndex >= queue.Segments.Count)
        {
            return;
        }

        var chapterIndex = queue.Segments[segmentIndex].ChapterIndex;
        PushSuppress();
        try
        {
            for (var i = 0; i < ChapterComboBox.Items.Count; i++)
            {
                if (ChapterComboBox.Items[i] is Chapter chapter && chapter.Index == chapterIndex)
                {
                    ChapterComboBox.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            PopSuppress();
        }
    }

    private void SelectSpeed(double speed)
    {
        var best = SpeedChoices[1];
        var bestDistance = double.MaxValue;
        foreach (var choice in SpeedChoices)
        {
            var value = double.Parse(choice, CultureInfo.InvariantCulture);
            var distance = Math.Abs(value - speed);
            if (distance < bestDistance)
            {
                best = choice;
                bestDistance = distance;
            }
        }

        PushSuppress();
        SpeedComboBox.SelectedItem = best;
        PopSuppress();
    }

    private void PushSuppress() => _suppressDepth++;

    private void PopSuppress() => _suppressDepth = Math.Max(0, _suppressDepth - 1);

    private void SelectById(string bookId)
    {
        for (var i = 0; i < BookList.Items.Count; i++)
        {
            if (BookList.Items[i] is BookRow row && string.Equals(row.Info.Id, bookId, StringComparison.Ordinal))
            {
                BookList.SelectedIndex = i;
                return;
            }
        }
    }

    private void SelectByPath(string path)
    {
        var full = Path.GetFullPath(path);
        for (var i = 0; i < BookList.Items.Count; i++)
        {
            if (BookList.Items[i] is BookRow row
                && string.Equals(Path.GetFullPath(row.Info.SourcePath), full, StringComparison.OrdinalIgnoreCase))
            {
                BookList.SelectedIndex = i;
                return;
            }
        }
    }

    private void OnQueueState(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowTransport);
            return;
        }

        try
        {
            ShowTransport();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void OnQueueSegment(object? sender, Segment segment)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnQueueSegment(sender, segment)));
            return;
        }

        try
        {
            ShowTransport();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void OnQueueLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetStatus(message));
            return;
        }

        SetStatus(message);
    }

    private void DetachQueue()
    {
        if (_queue is null)
        {
            return;
        }

        _queue.StateChanged -= OnQueueState;
        _queue.CurrentSegmentChanged -= OnQueueSegment;
        _queue.Log -= OnQueueLog;
        _queue = null;
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        OpenFileButton.IsEnabled = !busy;
        BookList.IsEnabled = !busy;
        RightPanel.IsEnabled = !busy && _queue is not null;
    }

    private void SetStatus(string text)
    {
        try
        {
            StatusText.Text = text;
        }
        catch (Exception)
        {
        }
    }

    private static string FormatClock(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private sealed class BookRow
    {
        public BookRow(BookInfo info) => Info = info;

        public BookInfo Info { get; }

        public override string ToString()
        {
            var chapters = Info.ChapterCount?.ToString(CultureInfo.InvariantCulture) ?? "-";
            var segments = Info.SegmentCount?.ToString(CultureInfo.InvariantCulture) ?? "-";
            return Info.Title + "    [" + Info.Format + "]    章 " + chapters + "    段 " + segments;
        }
    }
}
