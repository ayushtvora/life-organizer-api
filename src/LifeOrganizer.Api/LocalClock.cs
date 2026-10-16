using Microsoft.Extensions.Options;

namespace LifeOrganizer.Api;

/// <summary>"Now" in the user's configured timezone. Wraps <see cref="TimeProvider"/> so tests can fix the time.</summary>
public sealed class LocalClock(TimeProvider time, IOptions<AppOptions> options)
{
    public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTime Now => TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone).DateTime;

    public DateTimeOffset ToUtc(DateTime local) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone), TimeSpan.Zero);
}
