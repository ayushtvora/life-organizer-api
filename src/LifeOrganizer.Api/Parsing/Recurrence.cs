using System.Text.RegularExpressions;

namespace LifeOrganizer.Api.Parsing;

/// <summary>A recurrence phrase found in the input ("every weekday", "every other tuesday", "monthly").</summary>
/// <param name="Phrase">The phrase as typed, used as the start of the Todoist due string.</param>
/// <param name="Rule">RFC 5545 RRULE without the "RRULE:" prefix, used for calendar events.</param>
/// <param name="Days">Weekdays the rule is restricted to (empty = any day).</param>
public sealed partial record Recurrence(string Phrase, string Rule, IReadOnlyList<DayOfWeek> Days, int Index, int Length)
{
    private static readonly DayOfWeek[] Weekdays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    public static Recurrence? Find(string text)
    {
        var m = Pattern().Match(text);
        if (!m.Success) return null;

        var unit = (m.Groups["unit"].Success ? m.Groups["unit"].Value : m.Groups["adv"].Value).ToLowerInvariant();
        var interval = m.Groups["other"].Success ? 2 : m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value) : 1;

        IReadOnlyList<DayOfWeek> days = [];
        string freq;
        if (m.Groups["days"].Success)
        {
            freq = "WEEKLY";
            days = DayNames().Matches(m.Groups["days"].Value).Select(d => ToDay(d.Value)).Distinct().ToList();
        }
        else
        {
            (freq, days) = unit switch
            {
                "day" or "days" or "daily" => ("DAILY", []),
                "weekday" or "weekdays" => ("WEEKLY", Weekdays),
                "weekend" or "weekends" => ("WEEKLY", [DayOfWeek.Saturday, DayOfWeek.Sunday]),
                "week" or "weeks" or "weekly" => ("WEEKLY", []),
                "month" or "months" or "monthly" => ("MONTHLY", []),
                _ => ("YEARLY", (IReadOnlyList<DayOfWeek>)[]),
            };
        }

        var rule = $"FREQ={freq}";
        if (interval > 1) rule += $";INTERVAL={interval}";
        if (days.Count > 0) rule += ";BYDAY=" + string.Join(",", days.Select(Code));

        return new Recurrence(m.Value, rule, days, m.Index, m.Length);
    }

    private static DayOfWeek ToDay(string name) => name.ToLowerInvariant()[..3] switch
    {
        "mon" => DayOfWeek.Monday,
        "tue" => DayOfWeek.Tuesday,
        "wed" => DayOfWeek.Wednesday,
        "thu" => DayOfWeek.Thursday,
        "fri" => DayOfWeek.Friday,
        "sat" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };

    private static string Code(DayOfWeek d) => d.ToString()[..2].ToUpperInvariant();

    private const string Day = @"(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tues|tue|wed|thurs|thu|fri|sat|sun)s?";

    [GeneratedRegex(
        @"\b(?:every|each)\s+(?:(?<other>other)\s+|(?<n>\d+)\s+)?(?:(?<days>" + Day + @"(?:\s*(?:,\s*and|,|and|&)\s*" + Day + @")*)|(?<unit>days?|weekdays?|weekends?|weeks?|months?|years?))\b" +
        @"|\b(?<adv>daily|weekly|monthly|yearly|annually|weekdays)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    [GeneratedRegex(Day, RegexOptions.IgnoreCase)]
    private static partial Regex DayNames();
}
