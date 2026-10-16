using System.Globalization;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace LifeOrganizer.Api.Parsing;

public interface ILlmParser
{
    bool Enabled { get; }
    Task<ParsedItem?> ParseAsync(string text, ItemType? forced, CancellationToken ct);
}

/// <summary>Claude-backed parser with a strict JSON schema. Used when the rule parser is not confident.</summary>
public sealed class LlmParser(LocalClock clock, IOptions<AnthropicOptions> anthropic, IOptions<AppOptions> app,
    ILogger<LlmParser> logger) : ILlmParser
{
    private const string SystemPrompt = """
        You turn a short note typed on a phone into either a Todoist task or a Google Calendar event.

        Decide the type:
        - "event": something that occupies a block of time, especially with other people or at a place:
          a time range or duration ("4-5pm", "for 2 hours"), meetings, appointments, meals with someone, calls with
          someone, classes, flights, parties. A range of dates ("oct 20-22", "dec 20 to jan 3") is an all-day event
          spanning every day in the range.
        - "task": something the user has to do, with or without a deadline or reminder time
          ("do laundry 5am", "send email tomorrow", "pay rent oct 31", "finish report by 5pm"). Anything with a
          deadline ("by", "before", "eod") or a duration but no start time ("workout for 1 hour") is a task.
        - Both tasks and events can repeat.
        - If the input says which type it is, follow it. A forced event with no date is an all-day event today.

        Resolve dates relative to the current local date/time you are given:
        - A weekday name means its next occurrence (today only if the time is still ahead). "next <weekday>" means
          that weekday in next week.
        - A time with no date means its next occurrence (later today, otherwise tomorrow).
        - A date without a year means its next occurrence, even if that is next year.
        - Periods mean their first day: "next week" → next Monday, "this/next weekend" → Saturday, "next month" →
          the 1st. A period already under way ("this week") → today.
        - Deadlines: "by friday" → Friday, "before friday" → Thursday, "end of week"/"eow" → Friday,
          "end of month"/"eom" → last day of the month, "eod"/"end of day" → today with no time.
        - Parts of the day are fixed times: morning 09:00, lunch 12:00, afternoon 14:00, evening/after work 18:00,
          tonight/night 20:00.
        - For hours without am/pm, choose the next upcoming one between 08:00 and 22:00
          (at 11:00, "at 9" → 21:00 today; "at 7" → 19:00).
        - With several dates or times, use the first one.

        Output fields:
        - title: concise, capitalised, without the date/time words, priority markers, or filler ("remind me to").
        - start: local "YYYY-MM-DDTHH:MM" if a time is known, "YYYY-MM-DD" if only a date, null if no date.
          For tasks this is the due date/time (tasks with no date are made due today automatically).
        - end: events only. Local end time ("YYYY-MM-DDTHH:MM"), or for multi-day all-day events the inclusive
          last day ("YYYY-MM-DD"). null if not stated; tasks always null.
        - all_day: true when there is a date but no time.
        - recurrence: null unless the note repeats. For tasks, a Todoist due string including the time if any
          ("every monday at 9am", "every weekday"). For events, an RFC 5545 RRULE without the "RRULE:" prefix
          ("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"); start must then be the first occurrence. A period combined with a
          recurrence sets the first occurrence ("every monday next week" → every Monday, first one next week; for a
          task: "every monday starting Oct 19 2026").
        - location: a place if one is mentioned, else null.
        - description: extra details that don't belong in the title, else null.
        - priority: tasks only, Todoist API scale: 4 = urgent/P1, 3 = high/P2, 2 = medium/P3, null otherwise.
        - duration_minutes: tasks only, how long the task takes if stated, else null.
        """;

    private static readonly Dictionary<string, JsonElement> Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["type", "title", "start", "end", "all_day", "recurrence", "location", "description", "priority", "duration_minutes"],
          "properties": {
            "type": { "type": "string", "enum": ["task", "event"] },
            "title": { "type": "string" },
            "start": { "type": ["string", "null"] },
            "end": { "type": ["string", "null"] },
            "all_day": { "type": "boolean" },
            "recurrence": { "type": ["string", "null"] },
            "location": { "type": ["string", "null"] },
            "description": { "type": ["string", "null"] },
            "priority": { "type": ["integer", "null"] },
            "duration_minutes": { "type": ["integer", "null"] }
          }
        }
        """).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly AnthropicOptions _options = anthropic.Value;
    private readonly int _defaultEventMinutes = app.Value.DefaultEventMinutes;
    private readonly string _timeZone = app.Value.TimeZone;

    private AnthropicClient? _client;
    private AnthropicClient Client => _client ??= new AnthropicClient
    {
        ApiKey = _options.ApiKey,
        Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
        MaxRetries = 1,
    };

    public bool Enabled => _options.Enabled;

    public async Task<ParsedItem?> ParseAsync(string text, ItemType? forced, CancellationToken ct)
    {
        var now = clock.Now;
        var user = $"""
            Current local date/time: {now.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} ({_timeZone})
            {(forced is { } f ? $"The user explicitly marked this as a {f.ToString().ToLowerInvariant()}." : "")}

            <note>{text}</note>
            """;

        var response = await Client.Messages.Create(new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = 4096,
            System = SystemPrompt,
            OutputConfig = new OutputConfig
            {
                Effort = Effort.Low,
                Format = new JsonOutputFormat { Schema = Schema },
            },
            Messages = [new() { Role = Role.User, Content = user }],
        }, ct);

        if (response.StopReason == StopReason.Refusal)
        {
            logger.LogWarning("LLM refused to parse input");
            return null;
        }

        var json = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(b => b.Text));
        var output = JsonSerializer.Deserialize<LlmOutput>(json, JsonOptions)
            ?? throw new InvalidOperationException("LLM returned empty output");

        return ToItem(output, forced);
    }

    internal ParsedItem ToItem(LlmOutput o, ItemType? forced)
    {
        var type = forced ?? (o.Type == "event" ? ItemType.Event : ItemType.Task);
        var start = ParseLocal(o.Start);
        var end = ParseLocal(o.End);
        var allDay = o.AllDay || (o.Start is { Length: 10 });

        // No date: a forced event is all-day today; anything else is a task due today (same as the rules).
        if (start is null)
        {
            if (type == ItemType.Event && forced != ItemType.Event) type = ItemType.Task;
            start = clock.Now.Date;
            allDay = true;
        }

        if (type == ItemType.Event)
            end ??= allDay ? start : start.Value.AddMinutes(_defaultEventMinutes);
        else
            end = null;

        return new ParsedItem
        {
            Type = type,
            Title = string.IsNullOrWhiteSpace(o.Title) ? "Untitled" : o.Title.Trim(),
            Start = start,
            End = end,
            AllDay = start is not null && allDay,
            Recurrence = string.IsNullOrWhiteSpace(o.Recurrence) ? null : o.Recurrence.Trim(),
            Location = string.IsNullOrWhiteSpace(o.Location) ? null : o.Location.Trim(),
            Description = string.IsNullOrWhiteSpace(o.Description) ? null : o.Description.Trim(),
            Priority = type == ItemType.Task && o.Priority is >= 1 and <= 4 ? o.Priority : null,
            DurationMinutes = type == ItemType.Task && o.DurationMinutes is > 0 ? o.DurationMinutes : null,
            Parser = "llm",
        };
    }

    private static DateTime? ParseLocal(string? s) =>
        DateTime.TryParseExact(s, ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    internal sealed record LlmOutput(
        string Type, string Title, string? Start, string? End, bool AllDay,
        string? Recurrence, string? Location, string? Description, int? Priority, int? DurationMinutes = null);
}
