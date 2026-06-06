using NAudio.Wave;
using System.IO;

namespace ShoreKeeper.Host.Wpf.Audio;

public sealed class AudioRecordingService : IDisposable
{
    private WaveInEvent? _capture;
    private WaveFileWriter? _writer;

    public event Action<string>? LogReceived;
    public event Action<string>? RecordingStopped;

    public bool IsRecording => _capture is not null;
    public string RecordingsDirectory { get; } = ResolveRecordingsDirectory();
    public string? CurrentFilePath { get; private set; }

    public string StartRecording()
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("Recording is already running.");
        }

        Directory.CreateDirectory(RecordingsDirectory);

        CurrentFilePath = Path.Combine(
            RecordingsDirectory,
            $"shorekeeper-{DateTime.Now:yyyyMMdd-HHmmss}.wav");

        _capture = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50
        };

        _writer = new WaveFileWriter(CurrentFilePath, _capture.WaveFormat);
        _capture.DataAvailable += Capture_DataAvailable;
        _capture.RecordingStopped += Capture_RecordingStopped;
        _capture.StartRecording();

        Log($"Recording started: {CurrentFilePath}");
        return CurrentFilePath;
    }

    public void StopRecording()
    {
        if (_capture is null)
        {
            return;
        }

        _capture.StopRecording();
    }

    public void Dispose()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= Capture_DataAvailable;
            _capture.RecordingStopped -= Capture_RecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _writer?.Dispose();
        _writer = null;
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        _writer?.Flush();
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        var stoppedFilePath = CurrentFilePath ?? string.Empty;

        if (_capture is not null)
        {
            _capture.DataAvailable -= Capture_DataAvailable;
            _capture.RecordingStopped -= Capture_RecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _writer?.Dispose();
        _writer = null;

        if (e.Exception is not null)
        {
            Log($"Recording stopped with error: {e.Exception.Message}");
        }
        else
        {
            Log($"Recording saved: {stoppedFilePath}");
        }

        RecordingStopped?.Invoke(stoppedFilePath);
    }

    private static string ResolveRecordingsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var projectFilePath = Path.Combine(directory.FullName, "ShoreKeeper.Host.Wpf.csproj");
            if (File.Exists(projectFilePath))
            {
                return Path.Combine(directory.FullName, "Recordings");
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "Recordings");
    }

    private void Log(string text)
    {
        LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {text}");
    }
}
