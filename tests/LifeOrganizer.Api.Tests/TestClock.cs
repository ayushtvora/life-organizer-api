using LifeOrganizer.Api;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LifeOrganizer.Api.Tests;

public static class TestClock
{
    /// <summary>Friday 2026-10-16 11:00 in America/Toronto (EDT, UTC-4).</summary>
    public static readonly DateTimeOffset NowUtc = new(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);

    public static readonly AppOptions App = new() { ApiKey = "test-key", TimeZone = "America/Toronto", DefaultEventMinutes = 60 };

    public static FakeTimeProvider Time() => new(NowUtc);

    public static LocalClock Create() => new(Time(), Options.Create(App));
}
