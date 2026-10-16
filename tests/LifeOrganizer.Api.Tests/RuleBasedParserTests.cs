using LifeOrganizer.Api.Parsing;
using Microsoft.Extensions.Options;

namespace LifeOrganizer.Api.Tests;

/// <summary>Reference time: Friday 2026-10-16 11:00 local. Add a case for every phrasing fixed.</summary>
public class RuleBasedParserTests
{
    private readonly RuleBasedParser _parser = new(TestClock.Create(), Options.Create(TestClock.App));

    private ParseResult Parse(string input)
    {
        var (forced, text) = CaptureText.SplitOverride(input);
        return _parser.Parse(text, forced);
    }

    public static TheoryData<string, ItemType, string, string, string?, bool> Confident => new()
    {
        // input, type, title, start, end, allDay
        { "do laundry 5am", ItemType.Task, "Do laundry", "2026-10-17 05:00", null, false },
        { "send email tomorrow", ItemType.Task, "Send email", "2026-10-17 00:00", null, true },
        { "meeting 4-5pm wednesday", ItemType.Event, "Meeting", "2026-10-21 16:00", "2026-10-21 17:00", false },
        { "dentist appointment next tuesday at 3pm", ItemType.Event, "Dentist appointment", "2026-10-20 15:00", "2026-10-20 16:00", false },
        { "lunch with sam friday 12:30 for 1 hour", ItemType.Event, "Lunch with sam", "2026-10-16 12:30", "2026-10-16 13:30", false },
        { "pay rent oct 31", ItemType.Task, "Pay rent", "2026-10-31 00:00", null, true },
        { "gym 6-7am tomorrow", ItemType.Event, "Gym", "2026-10-17 06:00", "2026-10-17 07:00", false },
        { "dinner with alex saturday", ItemType.Event, "Dinner with alex", "2026-10-17 00:00", "2026-10-17 00:00", true },
        { "lunch next friday", ItemType.Event, "Lunch", "2026-10-23 00:00", "2026-10-23 00:00", true },

        // No date → task due today; forced event → all-day event today
        { "call mom", ItemType.Task, "Call mom", "2026-10-16 00:00", null, true },
        { "meeting with bob", ItemType.Task, "Meeting with bob", "2026-10-16 00:00", null, true },
        { "e party", ItemType.Event, "Party", "2026-10-16 00:00", "2026-10-16 00:00", true },

        // Am/pm: the next upcoming time between 8am and 10pm
        { "dinner at 7", ItemType.Event, "Dinner", "2026-10-16 19:00", "2026-10-16 20:00", false },
        { "gym at 9", ItemType.Task, "Gym", "2026-10-16 21:00", null, false },
        { "call bob at 8:30", ItemType.Task, "Call bob", "2026-10-16 20:30", null, false },
        { "call bob at 10:30", ItemType.Task, "Call bob", "2026-10-17 10:30", null, false },

        // Bare hour ranges
        { "work shift 9-5 tomorrow", ItemType.Event, "Work shift", "2026-10-17 09:00", "2026-10-17 17:00", false },
        { "work shift 9-5", ItemType.Event, "Work shift", "2026-10-17 09:00", "2026-10-17 17:00", false },
        { "team review 2-4", ItemType.Event, "Team review", "2026-10-16 14:00", "2026-10-16 16:00", false },

        // Parts of the day are fixed times
        { "call mom tonight", ItemType.Task, "Call mom", "2026-10-16 20:00", null, false },
        { "dinner with sam tomorrow evening", ItemType.Event, "Dinner with sam", "2026-10-17 18:00", "2026-10-17 19:00", false },
        { "stretch in the morning", ItemType.Task, "Stretch", "2026-10-17 09:00", null, false },
        { "water lawn saturday afternoon", ItemType.Task, "Water lawn", "2026-10-17 14:00", null, false },
        { "pick up kids after work", ItemType.Task, "Pick up kids", "2026-10-16 18:00", null, false },
        { "movie friday night", ItemType.Event, "Movie", "2026-10-16 20:00", "2026-10-16 21:00", false },

        // Periods → their first day
        { "do taxes next week", ItemType.Task, "Do taxes", "2026-10-19 00:00", null, true },
        { "camping this weekend", ItemType.Task, "Camping", "2026-10-17 00:00", null, true },
        { "hike next weekend", ItemType.Task, "Hike", "2026-10-24 00:00", null, true },
        { "plan budget next month", ItemType.Task, "Plan budget", "2026-11-01 00:00", null, true },
        { "clean garage this week", ItemType.Task, "Clean garage", "2026-10-16 00:00", null, true },

        // Deadlines
        { "finish report by 5pm today", ItemType.Task, "Finish report", "2026-10-16 17:00", null, false },
        { "submit form before 5pm", ItemType.Task, "Submit form", "2026-10-16 17:00", null, false },
        { "call landlord after 5pm", ItemType.Task, "Call landlord", "2026-10-16 17:00", null, false },
        { "submit report eod", ItemType.Task, "Submit report", "2026-10-16 00:00", null, true },
        { "submit report by end of day", ItemType.Task, "Submit report", "2026-10-16 00:00", null, true },
        { "send invoice by friday", ItemType.Task, "Send invoice", "2026-10-16 00:00", null, true },
        { "send invoice before friday", ItemType.Task, "Send invoice", "2026-10-22 00:00", null, true },
        { "renew passport before the weekend", ItemType.Task, "Renew passport", "2026-10-16 00:00", null, true },
        { "file expenses end of week", ItemType.Task, "File expenses", "2026-10-16 00:00", null, true },
        { "file expenses eow", ItemType.Task, "File expenses", "2026-10-16 00:00", null, true },
        { "pay bills end of month", ItemType.Task, "Pay bills", "2026-10-31 00:00", null, true },
        { "pay bills eom", ItemType.Task, "Pay bills", "2026-10-31 00:00", null, true },

        // Day-of-month ordinals
        { "pay rent the 20th", ItemType.Task, "Pay rent", "2026-10-20 00:00", null, true },
        { "pay card on the 5th", ItemType.Task, "Pay card", "2026-11-05 00:00", null, true },
        { "dentist the 20th at 3", ItemType.Event, "Dentist", "2026-10-20 15:00", "2026-10-20 16:00", false },

        // Year-less dates that already passed roll to their next occurrence
        { "pay bill oct 1", ItemType.Task, "Pay bill", "2027-10-01 00:00", null, true },

        // Explicit date ranges → all-day event through the last day
        { "trip oct 20-22", ItemType.Event, "Trip", "2026-10-20 00:00", "2026-10-22 00:00", true },
        { "vacation dec 20 to jan 3", ItemType.Event, "Vacation", "2026-12-20 00:00", "2027-01-03 00:00", true },
        { "conference nov 3 through nov 5", ItemType.Event, "Conference", "2026-11-03 00:00", "2026-11-05 00:00", true },

        // Several expressions → the first wins, the rest stays in the title
        { "call 10am or 2pm", ItemType.Task, "Call or 2pm", "2026-10-17 10:00", null, false },
        { "dinner tomorrow at 7pm then movie at 9pm", ItemType.Event, "Dinner then movie at 9pm", "2026-10-17 19:00", "2026-10-17 20:00", false },
    };

    [Theory]
    [MemberData(nameof(Confident))]
    public void Parses_phrasings_confidently(string input, ItemType type, string title, string start, string? end, bool allDay)
    {
        var result = Parse(input);

        Assert.True(result.Confidence == Confidence.High, string.Join("; ", result.Reasons));
        Assert.Equal(type, result.Item.Type);
        Assert.Equal(title, result.Item.Title);
        Assert.Equal(start, result.Item.Start?.ToString("yyyy-MM-dd HH:mm"));
        Assert.Equal(end, result.Item.End?.ToString("yyyy-MM-dd HH:mm"));
        Assert.Equal(allDay, result.Item.AllDay);
        Assert.Null(result.Item.Recurrence);
    }

    public static TheoryData<string, ItemType, string, string, string?, string> Recurring => new()
    {
        // input, type, title, first start, first end, recurrence (Todoist due string for tasks, RRULE for events)
        { "standup every weekday 9am", ItemType.Event, "Standup", "2026-10-19 09:00", "2026-10-19 10:00", "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR" },
        { "team sync every other tuesday 2-3pm", ItemType.Event, "Team sync", "2026-10-20 14:00", "2026-10-20 15:00", "FREQ=WEEKLY;INTERVAL=2;BYDAY=TU" },
        { "yoga class every saturday", ItemType.Event, "Yoga class", "2026-10-17 00:00", "2026-10-17 00:00", "FREQ=WEEKLY;BYDAY=SA" },
        { "water plants daily", ItemType.Task, "Water plants", "2026-10-16 00:00", null, "daily" },
        { "take out trash every monday", ItemType.Task, "Take out trash", "2026-10-19 00:00", null, "every monday" },
        { "gym every mon, wed and fri 6pm", ItemType.Task, "Gym", "2026-10-16 18:00", null, "every mon, wed and fri at 6:00pm" },
        { "pay rent every month on the 1st", ItemType.Task, "Pay rent", "2026-11-01 00:00", null, "every month starting Nov 1 2026" },
        { "e water plants every 3 days", ItemType.Event, "Water plants", "2026-10-16 00:00", "2026-10-16 00:00", "FREQ=DAILY;INTERVAL=3" },

        // Recurrence + a period: repeats on the rule, first occurrence inside that period
        { "take out trash every monday next week", ItemType.Task, "Take out trash", "2026-10-19 00:00", null, "every monday starting Oct 19 2026" },
        { "review budget every friday next week", ItemType.Task, "Review budget", "2026-10-23 00:00", null, "every friday starting Oct 23 2026" },
        { "yoga class every tuesday next week 6pm", ItemType.Event, "Yoga class", "2026-10-20 18:00", "2026-10-20 19:00", "FREQ=WEEKLY;BYDAY=TU" },
        { "water plants every saturday next weekend", ItemType.Task, "Water plants", "2026-10-24 00:00", null, "every saturday starting Oct 24 2026" },
        { "call grandma every sunday starting next month", ItemType.Task, "Call grandma", "2026-11-01 00:00", null, "every sunday starting Nov 1 2026" },
        { "standup every weekday next week 9am", ItemType.Event, "Standup", "2026-10-19 09:00", "2026-10-19 10:00", "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR" },
    };

    [Theory]
    [MemberData(nameof(Recurring))]
    public void Parses_recurring_tasks_and_events(string input, ItemType type, string title, string start, string? end, string recurrence)
    {
        var result = Parse(input);

        Assert.True(result.Confidence == Confidence.High, string.Join("; ", result.Reasons));
        Assert.Equal(type, result.Item.Type);
        Assert.Equal(title, result.Item.Title);
        Assert.Equal(start, result.Item.Start?.ToString("yyyy-MM-dd HH:mm"));
        Assert.Equal(end, result.Item.End?.ToString("yyyy-MM-dd HH:mm"));
        Assert.Equal(recurrence, result.Item.Recurrence);
    }

    [Theory]
    [InlineData("workout for 1 hour", "Workout", "2026-10-16", 60)]
    [InlineData("study 2 hours tomorrow", "Study", "2026-10-17", 120)]
    [InlineData("read for 30 minutes", "Read", "2026-10-16", 30)]
    public void Duration_without_start_is_a_task_with_duration(string input, string title, string due, int minutes)
    {
        var result = Parse(input);

        Assert.Equal(Confidence.High, result.Confidence);
        Assert.Equal(ItemType.Task, result.Item.Type);
        Assert.Equal(title, result.Item.Title);
        Assert.Equal(due, result.Item.Start?.ToString("yyyy-MM-dd"));
        Assert.Equal(minutes, result.Item.DurationMinutes);
    }

    [Fact]
    public void Forced_task_with_a_time_range_keeps_the_duration()
    {
        var item = Parse("t review slides 4-5pm").Item;
        Assert.Equal(ItemType.Task, item.Type);
        Assert.Equal(new DateTime(2026, 10, 16, 16, 0, 0), item.Start);
        Assert.Equal(60, item.DurationMinutes);
    }

    [Theory]
    [InlineData("pay bill oct 1 2025")] // explicit year in the past
    [InlineData("tomorrow 5pm")] // nothing left for a title
    public void Defers_only_unresolvable_input(string input)
    {
        Assert.Equal(Confidence.Low, Parse(input).Confidence);
    }

    [Theory]
    [InlineData("buy milk p1", 4)]
    [InlineData("buy milk !p3", 2)]
    public void Extracts_priority_token(string input, int priority)
    {
        var result = Parse(input);
        Assert.Equal("Buy milk", result.Item.Title);
        Assert.Equal(priority, result.Item.Priority);
    }

    [Theory]
    [InlineData("e party saturday 8pm", ItemType.Event, "party saturday 8pm")]
    [InlineData("event: party", ItemType.Event, "party")]
    [InlineData("c dentist 3pm", ItemType.Event, "dentist 3pm")]
    [InlineData("cal: pick up sam 3pm", ItemType.Event, "pick up sam 3pm")]
    [InlineData("Calendar lunch tomorrow", ItemType.Event, "lunch tomorrow")]
    [InlineData("t review slides 4-5pm", ItemType.Task, "review slides 4-5pm")]
    [InlineData("todo call mom", ItemType.Task, "call mom")]
    [InlineData("task: meeting notes", ItemType.Task, "meeting notes")]
    [InlineData("todoist book flights", ItemType.Task, "book flights")]
    public void Override_words_force_the_type(string input, ItemType forced, string rest)
    {
        Assert.Equal((forced, rest), CaptureText.SplitOverride(input));
    }

    [Theory]
    [InlineData("c++ homework")]
    [InlineData("eat lunch")]
    [InlineData("tasks for today")]
    [InlineData("todoist")]
    [InlineData("c:party")]
    [InlineData("e")]
    public void Words_that_only_look_like_overrides_are_left_alone(string input)
    {
        Assert.Null(CaptureText.SplitOverride(input).Forced);
    }
}
