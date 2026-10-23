using System.Text.Json;
using Scada.Web.Services;

namespace Scada.Web.Plugins.PlgMimBasicComp.Code;

public sealed class ParameterValue
{
    public int Channel { get; set; }
    public int FeedbackChannel { get; set; }
    public double Value { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;
    public double Step { get; set; } = 1;
    public double Tolerance { get; set; } = 0.01;
}
public sealed class SavedRecipe
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ParameterValue> Parameters { get; set; } = [];
}
public sealed class SavedSchedule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ParameterValue Command { get; set; } = new();
    public DateTimeOffset NextRunUtc { get; set; }
    public bool Enabled { get; set; }
    public string RepeatDays { get; set; } = ""; // 0=Sunday ... 6=Saturday; empty = once
    public string TimeZoneId { get; set; } = "";
    public int OwnerUserId { get; set; }
    public string LastResult { get; set; } = "尚未执行";
    public string Version { get; set; } = "";
}
public sealed class OperationEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
    public int UserId { get; set; }
    public string User { get; set; } = "";
    public string Action { get; set; } = "";
    public int Channel { get; set; }
    public double? Before { get; set; }
    public double? Value { get; set; }
    public string Result { get; set; } = "";
}
public sealed class OperationsState
{
    public List<SavedRecipe> Recipes { get; set; } = [];
    public List<SavedSchedule> Schedules { get; set; } = [];
    public List<OperationEntry> Records { get; set; } = [];
}

// Atomic, durable configuration and bounded operation history. No browser-local control state.
public sealed class OperationsStore
{
    private readonly object gate = new();
    private readonly string path;
    private OperationsState state;
    private static readonly JsonSerializerOptions options = new() { WriteIndented = true };
    public OperationsStore(IWebContext web)
    {
        path = Path.Combine(web.AppDirs.StorageDir, "industrial-operations.json");
        state = File.Exists(path) ? JsonSerializer.Deserialize<OperationsState>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("操作组件配置为空") : new();
    }
    private static OperationsState Clone(OperationsState value) => JsonSerializer.Deserialize<OperationsState>(JsonSerializer.Serialize(value, options), options);
    public OperationsState Read() { lock (gate) return Clone(state); }
    public void Update(Action<OperationsState> update)
    {
        lock (gate)
        {
            var next = Clone(state);
            update(next);
            next.Records = next.Records.TakeLast(2000).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, options));
            File.Move(temp, path, true);
            state = next;
        }
    }
    public void Record(OperationEntry entry) => Update(s => s.Records.Add(entry));

    public static void ValidateParameters(IReadOnlyList<ParameterValue> values)
    {
        if (values == null || values.Count is < 1 or > 8 || values.Select(p => p.Channel).Distinct().Count() != values.Count)
            throw new ArgumentException("参数必须包含 1–8 个互不重复的输出通道");
        foreach (var p in values)
        {
            if (p.Channel <= 0 || p.FeedbackChannel < 0 || !double.IsFinite(p.Value) || !double.IsFinite(p.Minimum) || !double.IsFinite(p.Maximum) ||
                !double.IsFinite(p.Step) || !double.IsFinite(p.Tolerance) || p.Step <= 0 || p.Tolerance < 0 || p.Maximum <= p.Minimum ||
                p.Value < p.Minimum || p.Value > p.Maximum || Math.Abs((p.Value - p.Minimum) / p.Step - Math.Round((p.Value - p.Minimum) / p.Step)) > 1e-6)
                throw new ArgumentException("参数值、范围、步长或反馈容差无效");
        }
    }
    public static DateTimeOffset? NextOccurrence(SavedSchedule schedule, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(schedule.RepeatDays)) return null;
        var days = schedule.RepeatDays.Split(',').Select(int.Parse).ToHashSet();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var time = TimeZoneInfo.ConvertTime(schedule.NextRunUtc, zone).TimeOfDay;
        for (int day = 0; day <= 7; day++)
        {
            var candidate = DateTime.SpecifyKind(localNow.Date.AddDays(day).Add(time), DateTimeKind.Unspecified);
            if (days.Contains((int)candidate.DayOfWeek) && !zone.IsInvalidTime(candidate))
            {
                var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(candidate, zone), TimeSpan.Zero);
                if (utc > now) return utc;
            }
        }
        return null;
    }
}
