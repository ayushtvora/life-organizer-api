using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LifeOrganizer.Api.Parsing;
using LifeOrganizer.Api.Sinks;
using Microsoft.Extensions.Options;

namespace LifeOrganizer.Api;

public static class CaptureEndpoint
{
    public const string ApiKeyHeader = "X-API-Key";
    private const int MaxTextLength = 500;

    public static void MapCapture(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/capture", HandleAsync).AddEndpointFilter<ApiKeyFilter>();
    }

    private static async Task<IResult> HandleAsync(
        CaptureRequest request,
        HybridParser parser,
        ICalendarSink calendar,
        ITaskSink tasks,
        ConfirmationFormatter formatter,
        IOptions<IntegrationOptions> integrations,
        ILogger<CaptureRequest> logger,
        CancellationToken ct)
    {
        var text = request.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return Results.BadRequest(CaptureResponse.Fail("Nothing to add — the text was empty."));
        if (text.Length > MaxTextLength)
            return Results.BadRequest(CaptureResponse.Fail($"That's too long (max {MaxTextLength} characters)."));

        var result = await parser.ParseAsync(text, ct);
        var item = result.Item;
        logger.LogInformation("Parsed {Text} as {Type} via {Parser} ({Confidence})", text, item.Type, item.Parser, result.Confidence);

        if (item.Type == ItemType.Event && item.Start is null)
            return Results.UnprocessableEntity(CaptureResponse.Fail($"Couldn't find a date or time for the event \"{item.Title}\"."));

        CreatedItem created;
        try
        {
            created = item.Type == ItemType.Event
                ? await calendar.CreateEventAsync(item, ct)
                : await tasks.CreateTaskAsync(item, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var app = item.Type == ItemType.Event ? "Google Calendar" : "Todoist";
            logger.LogError(ex, "Failed to create {Type} in {App}", item.Type, app);
            return Results.Json(CaptureResponse.Fail($"⚠️ Couldn't add to {app}: {ex.Message}"), statusCode: StatusCodes.Status502BadGateway);
        }

        var dryRun = integrations.Value.DryRun;
        return Results.Ok(new CaptureResponse
        {
            Success = true,
            Message = formatter.Format(item, dryRun),
            App = item.Type == ItemType.Event ? "Google Calendar" : "Todoist",
            Type = item.Type.ToString().ToLowerInvariant(),
            Title = item.Title,
            Start = FormatLocal(item.Start, item.AllDay),
            End = FormatLocal(item.End, item.AllDay),
            AllDay = item.AllDay,
            Recurrence = item.Recurrence,
            Priority = item.Priority,
            DurationMinutes = item.DurationMinutes,
            Link = created.Link,
            Parser = item.Parser,
            DryRun = dryRun ? true : null,
        });
    }

    private static string? FormatLocal(DateTime? dt, bool allDay) =>
        dt?.ToString(allDay ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
}

public sealed class ApiKeyFilter(IOptions<AppOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = options.Value.ApiKey;
        var provided = context.HttpContext.Request.Headers[CaptureEndpoint.ApiKeyHeader].ToString();

        if (string.IsNullOrEmpty(expected) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
        {
            return Results.Json(CaptureResponse.Fail("Unauthorized — check the API key in the Shortcut."),
                statusCode: StatusCodes.Status401Unauthorized);
        }
        return await next(context);
    }
}
