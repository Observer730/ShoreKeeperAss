using System.Windows.Threading;

namespace ShoreKeeper.Host.Wpf.Reminders;

/// <summary>
/// 定时检查提醒事项，到点后触发 ReminderFired 事件。
/// 每月提醒当月只触发一次；具体日期提醒触发一次后自动停用。
/// 应用未运行时错过的提醒，会在下次启动时补触发一次。
/// </summary>
public sealed class ReminderService
{
    private readonly ReminderStore _store = new();
    private readonly DispatcherTimer _timer;
    private readonly List<ReminderItem> _reminders;

    public ReminderService()
    {
        _reminders = _store.Load();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => CheckDueReminders();
    }

    public event Action<ReminderItem>? ReminderFired;

    public IReadOnlyList<ReminderItem> Reminders => _reminders;

    public string StoreFilePath => _store.FilePath;

    public void Start()
    {
        _timer.Start();
        // 启动时立即检查一次，补触发运行间隙错过的提醒。
        CheckDueReminders();
    }

    public void Stop() => _timer.Stop();

    public void Add(ReminderItem item)
    {
        _reminders.Add(item);
        Save();
    }

    public void Remove(ReminderItem item)
    {
        _reminders.Remove(item);
        Save();
    }

    public void Save() => _store.Save(_reminders);

    private void CheckDueReminders()
    {
        var now = DateTime.Now;
        var changed = false;

        foreach (var item in _reminders.Where(r => r.Enabled))
        {
            var firedKey = GetDueKey(item, now);
            if (firedKey is null || firedKey == item.LastFiredKey)
            {
                continue;
            }

            item.LastFiredKey = firedKey;
            changed = true;

            if (item.Kind == ReminderKind.OneTime)
            {
                item.Enabled = false;
            }

            ReminderFired?.Invoke(item);
        }

        if (changed)
        {
            Save();
        }
    }

    /// <summary>
    /// 如果该提醒当前到期，返回本周期唯一的触发标记；否则返回 null。
    /// </summary>
    private static string? GetDueKey(ReminderItem item, DateTime now)
    {
        switch (item.Kind)
        {
            case ReminderKind.Monthly:
            {
                var day = Math.Min(item.DayOfMonth, DateTime.DaysInMonth(now.Year, now.Month));
                var dueThisMonth = new DateTime(now.Year, now.Month, day,
                    item.TimeOfDay.Hour, item.TimeOfDay.Minute, 0);
                // 本月触发时间已过，且本月还没提醒过。
                return now >= dueThisMonth ? now.ToString("yyyy-MM") : null;
            }
            case ReminderKind.OneTime:
            {
                if (item.SpecificDateTime is { } at && now >= at)
                {
                    return "fired";
                }
                return null;
            }
            default:
                return null;
        }
    }
}
