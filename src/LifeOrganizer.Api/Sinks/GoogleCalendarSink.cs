using System.Globalization;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Options;

namespace LifeOrganizer.Api.Sinks;

public interface ICalendarSink
{
    Task<CreatedItem> CreateEventAsync(ParsedItem item, CancellationToken ct);
}

/// <summary>Creates events as a service account that the user's calendar has been shared with.</summary>
public sealed class GoogleCalendarSink(IOptions<GoogleOptions> options, IOptions<AppOptions> app) : ICalendarSink, IDisposable
{
    private readonly GoogleOptions _options = options.Value;
    private readonly string _timeZone = app.Value.TimeZone;
    private CalendarService? _service;

    private CalendarService Service => _service ??= CreateService();

    private CalendarService CreateService()
    {
        if (string.IsNullOrWhiteSpace(_options.ServiceAccountJsonBase64) || string.IsNullOrWhiteSpace(_options.CalendarId))
            throw new InvalidOperationException("Google Calendar is not configured (Google__ServiceAccountJsonBase64 / Google__CalendarId).");

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(_options.ServiceAccountJsonBase64));
        var credential = CredentialFactory.FromJson<ServiceAccountCredential>(json)
            .ToGoogleCredential()
            .CreateScoped(CalendarService.Scope.CalendarEvents);

        return new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "life-organizer-api",
        });
    }

    public async Task<CreatedItem> CreateEventAsync(ParsedItem item, CancellationToken ct)
    {
        var ev = BuildEvent(item, _timeZone);
        var created = await Service.Events.Insert(ev, _options.CalendarId).ExecuteAsync(ct);
        return new CreatedItem(created.Id, created.HtmlLink);
    }

    internal static Event BuildEvent(ParsedItem item, string timeZone)
    {
        if (item.Start is not { } start)
            throw new ArgumentException("An event needs a start date.", nameof(item));
        var end = item.End ?? start;

        EventDateTime Point(DateTime dt, bool exclusiveEndDate = false) => item.AllDay
            ? new EventDateTime { Date = (exclusiveEndDate ? dt.Date.AddDays(1) : dt.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            : new EventDateTime { DateTimeRaw = dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), TimeZone = timeZone };

        return new Event
        {
            Summary = item.Title,
            Location = item.Location,
            Description = item.Description,
            Start = Point(start),
            // Google's all-day end date is exclusive; ParsedItem stores the inclusive last day.
            End = Point(end, exclusiveEndDate: true),
            Recurrence = item.Recurrence is { } rule
                ? [rule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase) ? rule : "RRULE:" + rule]
                : null,
        };
    }

    public void Dispose() => _service?.Dispose();
}
