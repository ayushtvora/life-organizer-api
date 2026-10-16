namespace LifeOrganizer.Api.Sinks;

public sealed class DryRunCalendarSink : ICalendarSink
{
    public Task<CreatedItem> CreateEventAsync(ParsedItem item, CancellationToken ct) => Task.FromResult(new CreatedItem(null, null));
}

public sealed class DryRunTaskSink : ITaskSink
{
    public Task<CreatedItem> CreateTaskAsync(ParsedItem item, CancellationToken ct) => Task.FromResult(new CreatedItem(null, null));
}
