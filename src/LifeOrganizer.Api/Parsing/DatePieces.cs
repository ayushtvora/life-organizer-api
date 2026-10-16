using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;

namespace LifeOrganizer.Api.Parsing;

public enum PieceKind { Date, DateTime, Time, TimeRange, DateTimeRange, DateRange, Duration }

/// <summary>How to move a past candidate forward to its next occurrence (how the user left the date underspecified).</summary>
public enum Roll { None, Week, Month, Year }

/// <summary>
/// One interpretation of a date/time piece. Times-of-day (for <see cref="PieceKind.Time"/>/<see cref="PieceKind.TimeRange"/>)
/// are stored on <see cref="DateTime.MinValue"/>'s date. Date-range ends are inclusive.
/// </summary>
public sealed record Candidate(DateTime Start, DateTime? End = null, Roll Roll = Roll.None);

/// <summary>A date/time expression found in the text, normalized from Recognizers output or our own patterns.</summary>
public sealed record Piece(PieceKind Kind, int Index, int Length, IReadOnlyList<Candidate> Candidates,
    bool Deadline = false, TimeSpan? Duration = null)
{
    public int EndIndex => Index + Length - 1;
    public bool IsDateLike => Kind is PieceKind.Date or PieceKind.DateTime or PieceKind.DateTimeRange or PieceKind.DateRange;
    public bool IsTimeLike => Kind is PieceKind.Time or PieceKind.TimeRange;
}

/// <summary>Turns text into <see cref="Piece"/>s, encoding the house rules for vague expressions.</summary>
public static partial class DatePieces
{
    // Fixed times for parts of the day.
    private static readonly Dictionary<string, TimeOnly> DayParts = new()
    {
        ["MO"] = new(9, 0),   // morning
        ["DT"] = new(9, 0),   // daytime
        ["MI"] = new(12, 0),  // midday
        ["MEL"] = new(12, 0), // lunch
        ["AF"] = new(14, 0),  // afternoon
        ["EV"] = new(18, 0),  // evening
        ["NI"] = new(20, 0),  // night / tonight
        ["BH"] = new(9, 0),   // business hours
    };

    public static (List<Piece> Pieces, List<string> Problems) Find(string text, DateTime now)
    {
        var pieces = new List<Piece>();
        var problems = new List<string>();

        foreach (var e in DateTimeRecognizer.RecognizeDateTime(text, Culture.English, DateTimeOptions.None, now))
        {
            var piece = Convert(e, now, problems);
            if (piece is not null) pieces.Add(piece);
        }

        bool Free(int index, int length) =>
            pieces.All(p => index + length - 1 < p.Index || index > p.EndIndex);

        foreach (Match m in EndOfPeriodWord().Matches(text))
        {
            if (!Free(m.Index, m.Length)) continue;
            var date = m.Value.ToLowerInvariant() switch
            {
                "eow" => FridayOf(now.Date),
                "eom" => LastDayOfMonth(now.Date),
                _ => new DateTime(now.Year, 12, 31),
            };
            pieces.Add(new Piece(PieceKind.Date, m.Index, m.Length, [new(date)], Deadline: true));
        }

        foreach (Match m in AfterWork().Matches(text))
        {
            if (!Free(m.Index, m.Length)) continue;
            pieces.Add(new Piece(PieceKind.Time, m.Index, m.Length, [new(At(DayParts["EV"]))]));
        }

        foreach (Match m in Ordinal().Matches(text))
        {
            if (!Free(m.Index, m.Length)) continue;
            var day = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
            if (day is < 1 or > 31) continue;
            var candidates = new List<Candidate>();
            for (var month = new DateTime(now.Year, now.Month, 1); candidates.Count < 2; month = month.AddMonths(1))
                if (day <= DateTime.DaysInMonth(month.Year, month.Month))
                    candidates.Add(new(new DateTime(month.Year, month.Month, day), Roll: Roll.Month));
            pieces.Add(new Piece(PieceKind.Date, m.Index, m.Length, candidates));
        }

        foreach (Match m in BareHourRange().Matches(text))
        {
            if (!Free(m.Index, m.Length)) continue;
            var candidates = BareRangeCandidates(m).ToList();
            if (candidates.Count > 0) pieces.Add(new Piece(PieceKind.TimeRange, m.Index, m.Length, candidates));
        }

        pieces.Sort((a, b) => a.Index.CompareTo(b.Index));
        return (pieces, problems);
    }

    private static Piece? Convert(ModelResult e, DateTime now, List<string> problems)
    {
        var kind = e.TypeName.Replace("datetimeV2.", "");
        var values = Values(e);
        var startsWithBefore = e.Text.TrimStart().StartsWith("before", StringComparison.OrdinalIgnoreCase);
        Piece Make(PieceKind k, IEnumerable<Candidate> c, bool deadline = false, TimeSpan? duration = null) =>
            new(k, e.Start, e.End - e.Start + 1, c.ToList(), deadline, duration);

        switch (kind)
        {
            case "date":
                return Make(PieceKind.Date, values.Select(v => new Candidate(ParseDate(v["value"]), Roll: RollOf(v))));

            case "datetime":
            {
                // "eod" / "end of day" come back as 23:59:59: a date-only deadline.
                var dts = values.Select(v => (Dt: ParseDateTime(v["value"]), Roll: RollOf(v))).ToList();
                if (dts.All(x => x.Dt.TimeOfDay == new TimeSpan(23, 59, 59)))
                    return Make(PieceKind.Date, dts.Select(x => new Candidate(x.Dt.Date, Roll: x.Roll)), deadline: true);
                return Make(PieceKind.DateTime, dts.Select(x => new Candidate(x.Dt, Roll: x.Roll)));
            }

            case "datetimerange":
            {
                var mod = Mod(values);
                if (mod is "before" or "until" or "before-end")
                {
                    var ends = values.Where(v => v.ContainsKey("end")).Select(v => (Dt: ParseDateTime(v["end"]), Roll: RollOf(v))).ToList();
                    if (ends.All(x => x.Dt.TimeOfDay == new TimeSpan(23, 59, 59)))
                        return Make(PieceKind.Date, ends.Select(x => new Candidate(x.Dt.Date, Roll: x.Roll)), deadline: true);
                    return Make(PieceKind.DateTime, ends.Select(x => new Candidate(x.Dt, Roll: x.Roll)), deadline: true);
                }
                if (mod is "after" or "since")
                    return Make(PieceKind.DateTime, values.Where(v => v.ContainsKey("start"))
                        .Select(v => new Candidate(ParseDateTime(v["start"]), Roll: RollOf(v))));

                // "tonight", "tomorrow morning", "friday night" → that day at the fixed time.
                if (DayPart(values) is { } part)
                    return Make(PieceKind.DateTime, values.Where(v => v.ContainsKey("start"))
                        .Select(v => new Candidate(ParseDateTime(v["start"]).Date + part.ToTimeSpan(), Roll: RollOf(v))));

                return Make(PieceKind.DateTimeRange, values.Where(v => v.ContainsKey("start") && v.ContainsKey("end"))
                    .Select(v => new Candidate(ParseDateTime(v["start"]), ParseDateTime(v["end"]), RollOf(v))));
            }

            case "daterange":
            {
                var mod = Mod(values);
                if (mod is "before" or "until" or "before-end" or "end")
                {
                    // "by friday" → Friday, "before friday" → Thursday, "end of week" → Friday,
                    // "end of month" / "by end of month" → last day, "end of year" → Dec 31.
                    var deadlines = values.Select(v =>
                    {
                        var timex = v["timex"];
                        DateTime date;
                        if (mod.Contains("end")) date = EndOfPeriod(timex) ?? ParseDate(v["end"]).AddDays(-1);
                        else
                        {
                            date = ParseDate(v["end"]);
                            if (startsWithBefore) date = date.AddDays(-1);
                        }
                        return new Candidate(date, Roll: RollOf(v));
                    });
                    return Make(PieceKind.Date, deadlines, deadline: true);
                }
                if (mod is "after" or "since" or "start")
                    return Make(PieceKind.Date, values.Where(v => v.ContainsKey("start"))
                        .Select(v => new Candidate(ParseDate(v["start"]), Roll: RollOf(v))));

                var ranges = values.Where(v => v.ContainsKey("start") && v.ContainsKey("end")).ToList();
                if (ranges.Count == 0)
                {
                    problems.Add($"open-ended date range '{e.Text}'");
                    return null;
                }
                // Explicit ranges ("oct 20-22", "dec 20 to jan 3") → all-day span through the last day mentioned.
                if (ranges.All(v => v["timex"].StartsWith('(')))
                    return Make(PieceKind.DateRange, ranges.Select(v =>
                    {
                        var start = ParseDate(v["start"]);
                        var last = ParseDate(v["end"]);
                        return new Candidate(start, last < start ? start : last, RollOf(v));
                    }));
                // Implied periods → their first day: "next week" → Monday, "this weekend" → Saturday,
                // "next month" → the 1st. Periods already under way ("this week") → today.
                return Make(PieceKind.Date, ranges.Select(v =>
                {
                    var start = ParseDate(v["start"]);
                    return new Candidate(start < now.Date ? now.Date : start, Roll: RollOf(v));
                }));
            }

            case "time":
                return Make(PieceKind.Time, values.Select(v => new Candidate(At(ParseTime(v["value"])))));

            case "timerange":
            {
                var mod = Mod(values);
                if (mod is "before" or "until")
                    return Make(PieceKind.Time, values.Where(v => v.ContainsKey("end"))
                        .Select(v => new Candidate(At(ParseTime(v["end"])))), deadline: true);
                if (mod is "after" or "since")
                    return Make(PieceKind.Time, values.Where(v => v.ContainsKey("start"))
                        .Select(v => new Candidate(At(ParseTime(v["start"])))));
                if (DayPart(values) is { } part)
                    return Make(PieceKind.Time, [new Candidate(At(part))]);
                return Make(PieceKind.TimeRange, values.Where(v => v.ContainsKey("start") && v.ContainsKey("end"))
                    .Select(v => new Candidate(At(ParseTime(v["start"])), At(ParseTime(v["end"])))));
            }

            case "duration":
            {
                var seconds = double.Parse(values[0]["value"], CultureInfo.InvariantCulture);
                return Make(PieceKind.Duration, [], duration: TimeSpan.FromSeconds(seconds));
            }

            default:
                problems.Add($"unsupported date expression '{e.Text}' ({e.TypeName})");
                return null;
        }
    }

    /// <summary>"9-5" → 9am–5pm, "2-4" → 2pm–4pm, "8-9" → both 8–9am and 8–9pm (the parser picks the next one).</summary>
    private static IEnumerable<Candidate> BareRangeCandidates(Match m)
    {
        int H(string g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        int M(string g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
        var (h1, m1, h2, m2) = (H("h1"), M("m1"), H("h2"), M("m2"));
        if (h1 is < 1 or > 12 || h2 is < 1 or > 12 || m1 > 59 || m2 > 59) yield break;

        foreach (var s in new[] { h1 % 12, h1 % 12 + 12 })
        foreach (var en in new[] { h2 % 12, h2 % 12 + 12 })
        {
            var start = new TimeSpan(s, m1, 0);
            var end = new TimeSpan(en, m2, 0);
            if (end <= start || end - start > TimeSpan.FromHours(12)) continue;
            if (s < 8 || s >= 22) continue;
            yield return new Candidate(DateTime.MinValue + start, DateTime.MinValue + end);
        }
    }

    private static DateTime At(TimeOnly t) => DateTime.MinValue + t.ToTimeSpan();

    private static TimeOnly? DayPart(List<IDictionary<string, string>> values)
    {
        var timex = Get(values.FirstOrDefault(), "timex") ?? "";
        var m = DayPartTimex().Match(timex);
        return m.Success && DayParts.TryGetValue(m.Groups[1].Value, out var t) ? t : null;
    }

    private static DateTime? EndOfPeriod(string timex)
    {
        var week = IsoWeek().Match(timex);
        if (week.Success)
            return ISOWeek.ToDateTime(int.Parse(week.Groups[1].Value), int.Parse(week.Groups[2].Value), DayOfWeek.Friday);
        var month = YearMonth().Match(timex);
        if (month.Success)
            return LastDayOfMonth(new DateTime(int.Parse(month.Groups[1].Value), int.Parse(month.Groups[2].Value), 1));
        if (YearOnly().IsMatch(timex)) return new DateTime(int.Parse(timex), 12, 31);
        return null;
    }

    private static DateTime FridayOf(DateTime d)
    {
        var friday = d.AddDays(((int)DayOfWeek.Friday - (int)d.DayOfWeek + 7) % 7);
        return friday;
    }

    private static DateTime LastDayOfMonth(DateTime d) => new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    /// <summary>Which part of the date the user left out, from the TIMEX (XXXX-WXX-5 = "friday", XXXX-10-01 = "oct 1").</summary>
    private static Roll RollOf(IDictionary<string, string> v)
    {
        var timex = Get(v, "timex")?.TrimStart('(') ?? "";
        if (timex.StartsWith("XXXX-WXX", StringComparison.Ordinal)) return Roll.Week;
        if (timex.StartsWith("XXXX-XX", StringComparison.Ordinal)) return Roll.Month;
        if (timex.StartsWith("XXXX", StringComparison.Ordinal)) return Roll.Year;
        return Roll.None;
    }

    private static string Mod(List<IDictionary<string, string>> values) =>
        Get(values.FirstOrDefault(), "Mod") ?? "";

    private static string? Get(IDictionary<string, string>? v, string key) => v is not null && v.TryGetValue(key, out var s) ? s : null;

    private static List<IDictionary<string, string>> Values(ModelResult e) =>
        e.Resolution.TryGetValue("values", out var raw) && raw is IEnumerable list
            ? list.OfType<IDictionary<string, string>>().ToList()
            : [];

    private static DateTime ParseDateTime(string s) =>
        DateTime.ParseExact(s, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static DateTime ParseDate(string s) => DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TimeOnly ParseTime(string s) => TimeOnly.ParseExact(s, "HH:mm:ss", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"T(MEL|MO|MI|AF|EV|NI|DT|BH)$")]
    private static partial Regex DayPartTimex();

    [GeneratedRegex(@"^(\d{4})-W(\d{2})$")]
    private static partial Regex IsoWeek();

    [GeneratedRegex(@"^(\d{4})-(\d{2})$")]
    private static partial Regex YearMonth();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex YearOnly();

    [GeneratedRegex(@"\b(eow|eom|eoy)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EndOfPeriodWord();

    [GeneratedRegex(@"\bafter work\b", RegexOptions.IgnoreCase)]
    private static partial Regex AfterWork();

    [GeneratedRegex(@"\b(?:the\s+)?(?<day>\d{1,2})(?:st|nd|rd|th)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Ordinal();

    // "9-5", "10:30-11", "2 to 4" with no am/pm (Recognizers skips these).
    [GeneratedRegex(@"(?<![\d/:.-])(?<h1>\d{1,2})(?::(?<m1>\d{2}))?\s*(?:-|–|to)\s*(?<h2>\d{1,2})(?::(?<m2>\d{2}))?(?![\d/:.-])(?!\s*(?:am|pm|a\.m|p\.m)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex BareHourRange();
}
