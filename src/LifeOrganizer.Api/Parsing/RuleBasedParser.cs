using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace LifeOrganizer.Api.Parsing;

/// <summary>
/// Deterministic parser: <see cref="DatePieces"/> finds and normalizes date/time expressions, this class picks one
/// interpretation, classifies task vs event and builds the title. It aims to decide everything itself; Low confidence
/// (→ LLM) is reserved for input it genuinely cannot resolve.
/// </summary>
public sealed partial class RuleBasedParser(LocalClock clock, IOptions<AppOptions> options)
{
    private readonly int _defaultEventMinutes = options.Value.DefaultEventMinutes;

    public ParseResult Parse(string text, ItemType? forced = null)
    {
        var now = clock.Now;
        var reasons = new List<string>();
        var low = false;
        void Low(string reason) { low = true; reasons.Add(reason); }

        // Recurrence is matched by our own regex, then blanked out (same length, so offsets still line up)
        // before date recognition; Recognizers' "set" results don't resolve to usable dates.
        var recurrence = Recurrence.Find(text);
        var scan = recurrence is null
            ? text
            : text[..recurrence.Index] + new string(' ', recurrence.Length) + text[(recurrence.Index + recurrence.Length)..];

        // Spans to cut out of the title: the date/time pieces actually used, the recurrence phrase and the priority.
        var spans = new List<(int Start, int End)>();
        if (recurrence is not null) spans.Add((recurrence.Index, recurrence.Index + recurrence.Length - 1));

        int? priority = null;
        var pm = PriorityToken().Match(scan);
        if (pm.Success)
        {
            priority = 5 - int.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture);
            spans.Add((pm.Index, pm.Index + pm.Length - 1));
        }

        var (pieces, problems) = DatePieces.Find(scan, now);
        problems.ForEach(Low);

        // With several expressions, the first one wins; the rest stay in the title.
        var d = pieces.FirstOrDefault(p => p.IsDateLike);
        var t = d is null || d.Kind == PieceKind.Date ? pieces.FirstOrDefault(p => p.IsTimeLike) : null;
        var dur = pieces.FirstOrDefault(p => p.Kind == PieceKind.Duration);
        foreach (var p in new[] { d, t, dur }.OfType<Piece>()) spans.Add((p.Index, p.EndIndex));

        var deadline = (d?.Deadline ?? false) || (t?.Deadline ?? false);
        var hasRange = false;
        var hasDateRange = false;
        var candidates = new List<Slot>();

        switch (d?.Kind)
        {
            case PieceKind.Date:
                foreach (var date in d.Candidates)
                {
                    if (t is null)
                    {
                        candidates.Add(new Slot(date.Start.Date, null, AllDay: true, date.Roll));
                        continue;
                    }
                    foreach (var time in t.Candidates)
                    {
                        var start = date.Start.Date + time.Start.TimeOfDay;
                        if (time.End is not { } te) { candidates.Add(new Slot(start, null, false, date.Roll)); continue; }
                        hasRange = true;
                        var end = date.Start.Date + te.TimeOfDay;
                        candidates.Add(new Slot(start, end <= start ? end.AddDays(1) : end, false, date.Roll));
                    }
                }
                break;

            case PieceKind.DateTime:
                candidates.AddRange(d.Candidates.Select(c => new Slot(c.Start, null, false, c.Roll)));
                break;

            case PieceKind.DateTimeRange:
                hasRange = true;
                candidates.AddRange(d.Candidates.Select(c => new Slot(c.Start, c.End, false, c.Roll)));
                break;

            case PieceKind.DateRange:
                hasDateRange = true;
                candidates.AddRange(d.Candidates.Select(c => new Slot(c.Start, c.End, AllDay: true, c.Roll)));
                break;

            case null when t is not null:
                foreach (var time in t.Candidates)
                {
                    // Time only: the next occurrence of that time (later today, otherwise tomorrow).
                    var start = now.Date + time.Start.TimeOfDay;
                    if (start < now) start = start.AddDays(1);
                    if (time.End is not { } te) { candidates.Add(new Slot(start, null, false, Roll.None)); continue; }
                    hasRange = true;
                    var end = start.Date + te.TimeOfDay;
                    candidates.Add(new Slot(start, end <= start ? end.AddDays(1) : end, false, Roll.None));
                }
                break;
        }

        var slot = Choose(candidates, now, Low);

        // A duration extends a timed start into an event; without a start time it becomes the task's duration.
        int? durationMinutes = null;
        if (dur?.Duration is { } length)
        {
            if (slot is { AllDay: false, End: null })
            {
                hasRange = true;
                slot = slot with { End = slot.Start + length };
            }
            else if (slot is null || slot.AllDay)
                durationMinutes = (int)Math.Round(length.TotalMinutes);
        }

        if (recurrence is not null)
        {
            if (hasDateRange) Low("recurrence together with a date range");
            slot = FirstOccurrence(slot, recurrence, now);
        }

        var title = BuildTitle(text, spans);
        if (title.Length == 0)
        {
            Low("nothing left for a title");
            title = Capitalize(text.Trim());
        }

        // Classify.
        ItemType type;
        if (forced is { } f) type = f;
        else if (deadline || durationMinutes is not null) type = ItemType.Task;
        else if (hasRange || hasDateRange || EventKeyword().IsMatch(title)) type = ItemType.Event;
        else type = ItemType.Task;

        if (slot is null)
        {
            // No date: a forced event is all-day today; anything else is a task due today.
            if (type == ItemType.Event && forced != ItemType.Event) type = ItemType.Task;
            slot = new Slot(now.Date, null, AllDay: true, Roll.None);
        }

        DateTime? endTime = null;
        if (type == ItemType.Event)
            endTime = slot.AllDay ? slot.End ?? slot.Start : slot.End ?? slot.Start.AddMinutes(_defaultEventMinutes);
        else if (!slot.AllDay && slot.End is { } taskEnd)
            durationMinutes ??= (int)Math.Round((taskEnd - slot.Start).TotalMinutes); // e.g. "t review slides 4-5pm"

        var item = new ParsedItem
        {
            Type = type,
            Title = title,
            Start = slot.Start,
            End = endTime,
            AllDay = slot.AllDay,
            Recurrence = recurrence is null ? null
                : type == ItemType.Event ? recurrence.Rule
                : TodoistDueString(recurrence, slot, hasExplicitDate: d is not null),
            Priority = type == ItemType.Task ? priority : null,
            DurationMinutes = type == ItemType.Task ? durationMinutes : null,
            Parser = "rules",
        };
        return new ParseResult(item, low ? Confidence.Low : Confidence.High, reasons);
    }

    private sealed record Slot(DateTime Start, DateTime? End, bool AllDay, Roll Roll);

    /// <summary>
    /// Pick one interpretation. Keep upcoming ones; if none, roll the underspecified part forward ("oct 1" → next
    /// year, "before friday" → next week). Then take the earliest date per time of day, and among different times
    /// (am/pm) the earliest upcoming one between 8am and 10pm.
    /// </summary>
    private static Slot? Choose(List<Slot> candidates, DateTime now, Action<string> low)
    {
        if (candidates.Count == 0) return null;

        static bool Upcoming(Slot c, DateTime now) => c.AllDay ? (c.End ?? c.Start).Date >= now.Date : c.Start >= now;

        var future = candidates.Where(c => Upcoming(c, now)).ToList();
        if (future.Count == 0)
        {
            var latest = candidates.MaxBy(c => c.Start)!;
            if (latest.Roll == Roll.None)
            {
                low("date is in the past");
                return latest;
            }
            while (!Upcoming(latest, now)) latest = Advance(latest);
            return latest;
        }

        var perTime = future
            .GroupBy(c => (c.Start.TimeOfDay, c.AllDay))
            .Select(g => g.MinBy(c => c.Start)!)
            .OrderBy(c => c.Start)
            .ToList();
        return perTime.FirstOrDefault(c => c.AllDay || c.Start.Hour is >= 8 and < 22) ?? perTime[0];
    }

    private static Slot Advance(Slot s)
    {
        DateTime Step(DateTime x) => s.Roll switch
        {
            Roll.Week => x.AddDays(7),
            Roll.Month => x.AddMonths(1),
            _ => x.AddYears(1),
        };
        return s with { Start = Step(s.Start), End = s.End is { } e ? Step(e) : null };
    }

    /// <summary>Moves the start to the first day the rule actually fires (e.g. "every monday" → next Monday).</summary>
    private static Slot FirstOccurrence(Slot? slot, Recurrence rule, DateTime now)
    {
        slot ??= new Slot(now.Date, null, AllDay: true, Roll.None);
        var length = slot.End - slot.Start;

        var start = slot.Start;
        for (var i = 0; i < 8; i++)
        {
            var upcoming = slot.AllDay ? start.Date >= now.Date : start >= now;
            if (upcoming && (rule.Days.Count == 0 || rule.Days.Contains(start.DayOfWeek))) break;
            start = start.AddDays(1);
        }
        return slot with { Start = start, End = start + length };
    }

    /// <summary>Todoist parses natural language, so pass the phrase through, anchored with the start date/time.</summary>
    private static string TodoistDueString(Recurrence rule, Slot slot, bool hasExplicitDate)
    {
        var due = rule.Phrase.ToLowerInvariant();
        if (hasExplicitDate) due += " starting " + slot.Start.ToString("MMM d yyyy", CultureInfo.InvariantCulture);
        if (!slot.AllDay) due += " at " + slot.Start.ToString("h:mmtt", CultureInfo.InvariantCulture).ToLowerInvariant();
        return due;
    }

    private static string BuildTitle(string text, List<(int Start, int End)> spans)
    {
        var result = text;
        foreach (var (start, end) in spans.OrderByDescending(s => s.Start))
        {
            var cut = start;
            // Also drop a connector word right before the span ("dinner at 7" → "dinner").
            var m = TrailingConnector().Match(result[..cut]);
            if (m.Success) cut = m.Index;
            result = result[..cut] + " " + result[(end + 1)..];
        }

        result = Whitespace().Replace(result, " ").Trim();
        // Strip dangling connectors/punctuation at either end.
        string previous;
        do
        {
            previous = result;
            result = EdgeConnector().Replace(result, "").Trim();
        } while (result != previous);

        return Capitalize(result);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    [GeneratedRegex(@"(?<!\S)!?[pP]([1-4])(?!\S)")]
    private static partial Regex PriorityToken();

    [GeneratedRegex(
        @"\b(meeting|meet with|appointment|appt|dentist|doctor|dr\.?|interview|lunch|dinner|breakfast|brunch|coffee|drinks|party|flight|class|lecture|seminar|webinar|conference|concert|wedding|standup|stand-up|sync|1:1|one-on-one|call with|haircut|reservation|rehearsal|practice|game|movie|event|with\s+\w+)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex EventKeyword();

    [GeneratedRegex(@"\s*\b(at|on|by|for|from|due|until|till|before|this|next|starting|beginning)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingConnector();

    [GeneratedRegex(@"^(at|on|by|for|from|to|due|until|before|starting|beginning|and|@|-|,)\s+|\s+(at|on|by|for|from|to|due|until|before|starting|beginning|and|@|-)$|[\s,;:\-]+$|^[\s,;:\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex EdgeConnector();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
