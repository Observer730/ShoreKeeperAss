using System.IO;
using System.Text.Json;

namespace ShoreKeeper.Host.Wpf.Reminders;

/// <summary>
/// 提醒事项的 JSON 持久化，保存在 %LocalAppData%\ShoreKeeperHost\reminders.json。
/// </summary>
public sealed class ReminderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public ReminderStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShoreKeeperHost");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "reminders.json");
    }

    public string FilePath => _filePath;

    public List<ReminderItem> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new List<ReminderItem>();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<ReminderItem>>(json, JsonOptions)
                   ?? new List<ReminderItem>();
        }
        catch
        {
            // 文件损坏时不阻塞启动，返回空列表。
            return new List<ReminderItem>();
        }
    }

    public void Save(IEnumerable<ReminderItem> reminders)
    {
        var json = JsonSerializer.Serialize(reminders.ToList(), JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
