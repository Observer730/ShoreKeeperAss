namespace ShoreKeeper.Host.Wpf.Reminders;

public enum ReminderKind
{
    /// <summary>每月固定日期 + 固定时间触发。</summary>
    Monthly,

    /// <summary>只触发一次的具体日期时间。</summary>
    OneTime
}

public sealed class ReminderItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "领取 Unreal 免费资产";

    public ReminderKind Kind { get; set; } = ReminderKind.Monthly;

    /// <summary>Monthly：每月几号（1-31，超出当月天数时自动取月末）。</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Monthly：当天触发时间。</summary>
    public TimeOnly TimeOfDay { get; set; } = new(10, 0);

    /// <summary>OneTime：具体触发时间（本地时间）。</summary>
    public DateTime? SpecificDateTime { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 已触发标记。Monthly 用 "yyyy-MM" 表示本月已提醒；OneTime 用 "fired" 表示已提醒。
    /// </summary>
    public string? LastFiredKey { get; set; }

    public string Describe()
    {
        return Kind switch
        {
            ReminderKind.Monthly => $"每月 {DayOfMonth} 日 {TimeOfDay:HH\\:mm}",
            ReminderKind.OneTime => SpecificDateTime?.ToString("yyyy-MM-dd HH:mm") ?? "未设置时间",
            _ => string.Empty
        };
    }

    public override string ToString()
    {
        var state = Enabled ? "" : "（已停用）";
        return $"[{Describe()}] {Title}{state}";
    }
}
