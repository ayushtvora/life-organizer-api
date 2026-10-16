using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LifeOrganizer.Api.Sinks;

public interface ITaskSink
{
    Task<CreatedItem> CreateTaskAsync(ParsedItem item, CancellationToken ct);
}

/// <summary>Todoist REST API v1 (<c>POST /api/v1/tasks</c>).</summary>
public sealed class TodoistSink(HttpClient http, IOptions<TodoistOptions> options, LocalClock clock) : ITaskSink
{
    public const string BaseAddress = "https://api.todoist.com/api/v1/";

    private readonly TodoistOptions _options = options.Value;

    public async Task<CreatedItem> CreateTaskAsync(ParsedItem item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiToken))
            throw new InvalidOperationException("Todoist is not configured (Todoist__ApiToken).");

        var body = BuildBody(item, _options.ProjectId, clock);
        var (status, json) = await PostAsync(body, ct);

        // Todoist may reject a duration on a task without a due time; keep the task and note the duration instead.
        if (status == HttpStatusCode.BadRequest && body.Remove("duration"))
        {
            body.Remove("duration_unit");
            var note = $"Duration: {FormatDuration(item.DurationMinutes!.Value)}";
            body["description"] = body.TryGetValue("description", out var d) ? $"{d}\n{note}" : note;
            (status, json) = await PostAsync(body, ct);
        }

        if ((int)status is < 200 or > 299)
            throw new HttpRequestException($"Todoist returned {(int)status}: {json}");

        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetString();
        return new CreatedItem(id, id is null ? null : $"https://app.todoist.com/app/task/{id}");
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(Dictionary<string, object> body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "tasks") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        using var response = await http.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    internal static string FormatDuration(int minutes) =>
        (minutes / 60, minutes % 60) switch
        {
            (0, var m) => $"{m}m",
            (var h, 0) => $"{h}h",
            (var h, var m) => $"{h}h {m}m",
        };

    internal static Dictionary<string, object> BuildBody(ParsedItem item, string? projectId, LocalClock clock)
    {
        var body = new Dictionary<string, object> { ["content"] = item.Title };
        if (item.Description is { } description) body["description"] = description;
        if (item.Priority is { } priority) body["priority"] = priority;
        if (!string.IsNullOrWhiteSpace(projectId)) body["project_id"] = projectId;
        if (item.DurationMinutes is > 0 and var minutes)
        {
            body["duration"] = minutes;
            body["duration_unit"] = "minute";
        }

        if (item.Recurrence is { } recurrence)
        {
            body["due_string"] = recurrence;
            body["due_lang"] = "en";
        }
        else if (item.Start is { } start)
        {
            if (item.AllDay)
                body["due_date"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            else
                body["due_datetime"] = clock.ToUtc(start).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
        return body;
    }
}
