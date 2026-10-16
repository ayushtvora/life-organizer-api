using System.Text.RegularExpressions;

namespace LifeOrganizer.Api.Parsing;

public static partial class CaptureText
{
    /// <summary>
    /// Splits an optional leading type word off the input: e / event / c / cal / calendar → event,
    /// t / task / todo / todoist → task. The word must be followed by a space; a colon before it is optional
    /// ("t buy milk", "event: party"), so "eat lunch" or "c:party" are left alone.
    /// </summary>
    public static (ItemType? Forced, string Text) SplitOverride(string input)
    {
        var m = OverridePrefix().Match(input);
        if (!m.Success) return (null, input.Trim());

        var forced = m.Groups["kind"].Value.ToLowerInvariant() switch
        {
            "t" or "task" or "todo" or "todoist" => ItemType.Task,
            _ => ItemType.Event,
        };
        return (forced, input[m.Length..].Trim());
    }

    // Word, optional colon, then at least one space and more text.
    [GeneratedRegex(@"^\s*(?<kind>e|event|c|cal|calendar|t|task|todo|todoist):?\s+(?=\S)", RegexOptions.IgnoreCase)]
    private static partial Regex OverridePrefix();
}
