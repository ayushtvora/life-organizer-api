using LifeOrganizer.Api.Parsing;
using LifeOrganizer.Api.Sinks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LifeOrganizer.Api.Tests;

/// <summary>LLM output → ParsedItem, ParsedItem → Google/Todoist payloads, and the confirmation text.</summary>
public class MappingTests
{
    private static readonly LocalClock Clock = TestClock.Create();

    private static LlmParser Llm() => new(Clock, Options.Create(new AnthropicOptions()), Options.Create(TestClock.App),
        NullLogger<LlmParser>.Instance);

    [Fact]
    public void Llm_event_without_end_gets_default_duration()
    {
        var item = Llm().ToItem(new LlmParser.LlmOutput("event", "Dinner", "2026-10-16T19:00", null, false, null, "Luigi's", null, 3), null);

        Assert.Equal(ItemType.Event, item.Type);
        Assert.Equal(new DateTime(2026, 10, 16, 20, 0, 0), item.End);
        Assert.Null(item.Priority); // priority is task-only
        Assert.Equal("Luigi's", item.Location);
        Assert.Equal("llm", item.Parser);
    }

    [Fact]
    public void Llm_date_only_start_is_all_day()
    {
        var item = Llm().ToItem(new LlmParser.LlmOutput("task", "Pay rent", "2026-10-31", null, false, null, null, null, null), null);

        Assert.True(item.AllDay);
        Assert.Null(item.End);
    }

    [Fact]
    public void Llm_task_without_date_is_due_today()
    {
        var item = Llm().ToItem(new LlmParser.LlmOutput("task", "Call mom", null, null, false, null, null, null, null), null);

        Assert.Equal(new DateTime(2026, 10, 16), item.Start);
        Assert.True(item.AllDay);
    }

    [Fact]
    public void Llm_unforced_event_without_date_becomes_task_today()
    {
        var item = Llm().ToItem(new LlmParser.LlmOutput("event", "Meeting with bob", null, null, false, null, null, null, null), null);
        Assert.Equal(ItemType.Task, item.Type);
        Assert.Equal(new DateTime(2026, 10, 16), item.Start);
    }

    [Fact]
    public void Llm_forced_event_without_date_is_all_day_today()
    {
        var item = Llm().ToItem(new LlmParser.LlmOutput("event", "Party", null, null, false, null, null, null, null), ItemType.Event);
        Assert.Equal(ItemType.Event, item.Type);
        Assert.True(item.AllDay);
        Assert.Equal(new DateTime(2026, 10, 16), item.End);
    }

    [Fact]
    public void Task_duration_is_sent_to_todoist_and_shown()
    {
        var item = new ParsedItem
        {
            Type = ItemType.Task, Title = "Workout", Start = new DateTime(2026, 10, 16), AllDay = true, DurationMinutes = 90,
        };
        var body = TodoistSink.BuildBody(item, projectId: null, Clock);

        Assert.Equal(90, body["duration"]);
        Assert.Equal("minute", body["duration_unit"]);
        Assert.Contains("⏱ 1h 30m", new ConfirmationFormatter(Clock).Format(item, dryRun: false));
    }

    [Fact]
    public void Forced_type_overrides_llm()
    {
        var item = Llm().ToItem(new LlmParser.LlmOutput("event", "Party", "2026-10-17T20:00", null, false, null, null, null, null), ItemType.Task);
        Assert.Equal(ItemType.Task, item.Type);
    }

    [Fact]
    public void Timed_event_maps_to_google_with_timezone()
    {
        var ev = GoogleCalendarSink.BuildEvent(new ParsedItem
        {
            Type = ItemType.Event, Title = "Meeting",
            Start = new DateTime(2026, 10, 21, 16, 0, 0), End = new DateTime(2026, 10, 21, 17, 0, 0),
            Recurrence = "FREQ=WEEKLY;BYDAY=WE",
        }, "America/Toronto");

        Assert.Equal("2026-10-21T16:00:00", ev.Start.DateTimeRaw);
        Assert.Equal("America/Toronto", ev.Start.TimeZone);
        Assert.Equal("2026-10-21T17:00:00", ev.End.DateTimeRaw);
        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=WE"], ev.Recurrence);
    }

    [Fact]
    public void All_day_event_end_is_exclusive_in_google()
    {
        var ev = GoogleCalendarSink.BuildEvent(new ParsedItem
        {
            Type = ItemType.Event, Title = "Offsite", AllDay = true,
            Start = new DateTime(2026, 10, 20), End = new DateTime(2026, 10, 22),
        }, "America/Toronto");

        Assert.Equal("2026-10-20", ev.Start.Date);
        Assert.Equal("2026-10-23", ev.End.Date);
    }

    [Fact]
    public void Timed_task_sends_utc_due_datetime()
    {
        var body = TodoistSink.BuildBody(new ParsedItem
        {
            Type = ItemType.Task, Title = "Do laundry", Start = new DateTime(2026, 10, 17, 5, 0, 0), Priority = 4,
        }, projectId: null, Clock);

        Assert.Equal("Do laundry", body["content"]);
        Assert.Equal("2026-10-17T09:00:00Z", body["due_datetime"]);
        Assert.Equal(4, body["priority"]);
        Assert.False(body.ContainsKey("project_id"));
    }

    [Fact]
    public void Recurring_task_sends_due_string()
    {
        var body = TodoistSink.BuildBody(new ParsedItem
        {
            Type = ItemType.Task, Title = "Water plants", Start = new DateTime(2026, 10, 17, 9, 0, 0), Recurrence = "every day at 9am",
        }, projectId: "123", Clock);

        Assert.Equal("every day at 9am", body["due_string"]);
        Assert.False(body.ContainsKey("due_datetime"));
        Assert.Equal("123", body["project_id"]);
    }

    [Fact]
    public void Formats_event_confirmation()
    {
        var text = new ConfirmationFormatter(Clock).Format(new ParsedItem
        {
            Type = ItemType.Event, Title = "Meeting",
            Start = new DateTime(2026, 10, 21, 16, 0, 0), End = new DateTime(2026, 10, 21, 17, 0, 0),
        }, dryRun: false);

        Assert.Equal("📅 Added to Google Calendar\nMeeting\n🕒 Wed, Oct 21 · 4:00 PM – 5:00 PM", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Formats_task_confirmation_with_relative_day_and_priority()
    {
        var text = new ConfirmationFormatter(Clock).Format(new ParsedItem
        {
            Type = ItemType.Task, Title = "Do laundry", Start = new DateTime(2026, 10, 17, 5, 0, 0), Priority = 4,
        }, dryRun: false);

        Assert.Equal("✅ Added to Todoist\nDo laundry\n🗓 Due Tomorrow (Sat, Oct 17) · 5:00 AM\n🚩 P1", text.ReplaceLineEndings("\n"));
    }
}
