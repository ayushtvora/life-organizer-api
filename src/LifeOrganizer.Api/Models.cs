namespace LifeOrganizer.Api;

public enum ItemType { Task, Event }

public enum Confidence { Low, High }

/// <summary>
/// A parsed capture. All date/times are wall-clock times in the configured <c>App:TimeZone</c>.
/// For tasks, <see cref="Start"/> is the due date/time and <see cref="End"/> is unused.
/// For all-day items only the date part of <see cref="Start"/>/<see cref="End"/> is meaningful,
/// and an all-day event's <see cref="End"/> is the inclusive last day.
/// </summary>
public sealed record ParsedItem
{
    public required ItemType Type { get; init; }
    public required string Title { get; init; }
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public bool AllDay { get; init; }
    /// <summary>Todoist due string (tasks, e.g. "every monday at 9am") or RRULE (events, e.g. "FREQ=WEEKLY;BYDAY=MO").</summary>
    public string? Recurrence { get; init; }
    public string? Location { get; init; }
    public string? Description { get; init; }
    /// <summary>Todoist API priority: 4 = P1 (urgent) … 1 = P4 (normal).</summary>
    public int? Priority { get; init; }
    /// <summary>Tasks only: how long the task takes ("workout for 1 hour"), sent as the Todoist task duration.</summary>
    public int? DurationMinutes { get; init; }
    /// <summary>"rules" or "llm".</summary>
    public string Parser { get; init; } = "rules";
}

public sealed record ParseResult(ParsedItem Item, Confidence Confidence, IReadOnlyList<string> Reasons);

public sealed record CaptureRequest(string? Text);

public sealed record CreatedItem(string? Id, string? Link);

public sealed record CaptureResponse
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    /// <summary>"Google Calendar" or "Todoist".</summary>
    public string? App { get; init; }
    public string? Type { get; init; }
    public string? Title { get; init; }
    public string? Start { get; init; }
    public string? End { get; init; }
    public bool? AllDay { get; init; }
    public string? Recurrence { get; init; }
    public int? Priority { get; init; }
    public int? DurationMinutes { get; init; }
    public string? Link { get; init; }
    public string? Parser { get; init; }
    public bool? DryRun { get; init; }

    public static CaptureResponse Fail(string message) => new() { Success = false, Message = message };
}
