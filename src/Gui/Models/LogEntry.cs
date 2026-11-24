namespace Gui.Models;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
    Critical = 4
}

// A run log keeps the Run and Plan Item it came from, so the activity panel can group it under its task.
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message,
    Guid? RunId = null, Guid? PlanItemId = null, string? TaskLabel = null)
{
    public string DisplayLevel => Level.ToString().ToUpperInvariant();

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss.fff}] [{DisplayLevel}] {Message}";
}

public enum RunLogStatus { None, Running, Succeeded, Failed, Stopped }

// The heading above one stretch of consecutive run logs from the same Plan Item, or from the environment.
public sealed record RunLogGroup(Guid? PlanItemId, string Label)
{
    public Guid? RunId { get; init; }
    public RunLogStatus Status { get; init; }

    public string StatusText => Status switch {
        RunLogStatus.Running => "进行中",
        RunLogStatus.Succeeded => "已完成",
        RunLogStatus.Failed => "失败",
        RunLogStatus.Stopped => "已停止",
        _ => string.Empty
    };

    public override string ToString() => Label;
}
