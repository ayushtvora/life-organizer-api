using System.Globalization;
using System.Text;

namespace LifeOrganizer.Api;

/// <summary>Builds the text the Shortcut shows after a capture.</summary>
public sealed class ConfirmationFormatter(LocalClock clock)
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    public string Format(ParsedItem item, bool dryRun)
    {
        var sb = new StringBuilder();
        if (dryRun) sb.Append("[dry run] ");

        if (item.Type == ItemType.Event)
        {
            sb.AppendLine("📅 Added to Google Calendar");
            sb.AppendLine(item.Title);
            sb.AppendLine("🕒 " + EventWhen(item));
            if (item.Recurrence is { } r) sb.AppendLine("🔁 " + DescribeRule(r));
            if (item.Location is { } loc) sb.AppendLine("📍 " + loc);
        }
        else
        {
            sb.AppendLine("✅ Added to Todoist");
            sb.AppendLine(item.Title);
            if (item.Recurrence is { } r) sb.AppendLine("🔁 " + r);
            else sb.AppendLine("🗓 " + (item.Start is { } due ? "Due " + Day(due) + (item.AllDay ? "" : " · " + Time(due)) : "No due date"));
            if (item.DurationMinutes is { } minutes) sb.AppendLine("⏱ " + Sinks.TodoistSink.FormatDuration(minutes));
            if (item.Priority is { } p and > 1) sb.AppendLine($"🚩 P{5 - p}");
        }

        if (item.Description is { } desc) sb.AppendLine("📝 " + desc);
        return sb.ToString().TrimEnd();
    }

    private string EventWhen(ParsedItem item)
    {
        var start = item.Start!.Value;
        var end = item.End ?? start;
        if (item.AllDay)
            return end.Date > start.Date ? $"{Day(start)} – {Day(end)} (all day)" : $"{Day(start)} (all day)";
        if (end.Date == start.Date)
            return $"{Day(start)} · {Time(start)} – {Time(end)}";
        return $"{Day(start)} {Time(start)} – {Day(end)} {Time(end)}";
    }

    /// <summary>"Today", "Tomorrow", or e.g. "Wed, Oct 21" (with the year if it isn't this year).</summary>
    private string Day(DateTime dt)
    {
        var today = clock.Now.Date;
        var label = dt.ToString(dt.Year == today.Year ? "ddd, MMM d" : "ddd, MMM d, yyyy", En);
        if (dt.Date == today) return $"Today ({label})";
        if (dt.Date == today.AddDays(1)) return $"Tomorrow ({label})";
        return label;
    }

    private static string Time(DateTime dt) => dt.ToString("h:mm tt", En);

    private static string DescribeRule(string rule)
    {
        var r = rule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase) ? rule[6..] : rule;
        var parts = r.Split(';').Select(p => p.Split('=')).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1].ToUpperInvariant());
        var freq = parts.GetValueOrDefault("FREQ") switch
        {
            "DAILY" => "Daily",
            "WEEKLY" => "Weekly",
            "MONTHLY" => "Monthly",
            "YEARLY" => "Yearly",
            _ => null,
        };
        if (freq is null) return "Repeats: " + r;
        return parts.TryGetValue("BYDAY", out var days) ? $"{freq} on {days.Replace(",", ", ")}" : freq;
    }
}
