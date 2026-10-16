using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOrganizer.Api.Parsing;
using LifeOrganizer.Api.Sinks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LifeOrganizer.Api.Tests;

public class CaptureEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class RecordingSinks : ICalendarSink, ITaskSink
    {
        public List<ParsedItem> Items { get; } = [];
        public Exception? Throw { get; set; }

        public Task<CreatedItem> CreateEventAsync(ParsedItem item, CancellationToken ct) => Record(item, "https://calendar.example/e");
        public Task<CreatedItem> CreateTaskAsync(ParsedItem item, CancellationToken ct) => Record(item, "https://todoist.example/t");

        private Task<CreatedItem> Record(ParsedItem item, string link)
        {
            if (Throw is not null) throw Throw;
            Items.Add(item);
            return Task.FromResult(new CreatedItem("1", link));
        }
    }

    private sealed class NoLlm : ILlmParser
    {
        public bool Enabled => false;
        public Task<ParsedItem?> ParseAsync(string text, ItemType? forced, CancellationToken ct) => throw new NotSupportedException();
    }

    private (HttpClient Client, RecordingSinks Sinks) Create()
    {
        var sinks = new RecordingSinks();
        var client = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("App:ApiKey", "test-key");
            b.UseSetting("App:TimeZone", "America/Toronto");
            b.UseSetting("Integrations:DryRun", "false");
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(TestClock.Time());
                s.RemoveAll<ILlmParser>();
                s.AddSingleton<ILlmParser, NoLlm>();
                s.RemoveAll<ICalendarSink>();
                s.RemoveAll<ITaskSink>();
                s.AddSingleton<ICalendarSink>(sinks);
                s.AddSingleton<ITaskSink>(sinks);
            });
        }).CreateClient();
        client.DefaultRequestHeaders.Add(CaptureEndpoint.ApiKeyHeader, "test-key");
        return (client, sinks);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>());

    [Fact]
    public async Task Health_needs_no_key()
    {
        var response = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_missing_or_wrong_key()
    {
        var (client, sinks) = Create();
        client.DefaultRequestHeaders.Remove(CaptureEndpoint.ApiKeyHeader);
        client.DefaultRequestHeaders.Add(CaptureEndpoint.ApiKeyHeader, "nope");

        var response = await client.PostAsJsonAsync("/api/capture", new { text = "call mom" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False((await Body(response)).GetProperty("success").GetBoolean());
        Assert.Empty(sinks.Items);
    }

    [Fact]
    public async Task Creates_event_and_returns_confirmation()
    {
        var (client, sinks) = Create();

        var response = await client.PostAsJsonAsync("/api/capture", new { text = "meeting 4-5pm wednesday" });
        var body = await Body(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Google Calendar", body.GetProperty("app").GetString());
        Assert.Equal("event", body.GetProperty("type").GetString());
        Assert.Equal("2026-10-21T16:00", body.GetProperty("start").GetString());
        Assert.Equal("https://calendar.example/e", body.GetProperty("link").GetString());
        Assert.StartsWith("📅 Added to Google Calendar", body.GetProperty("message").GetString());
        Assert.Single(sinks.Items);
    }

    [Fact]
    public async Task Creates_task()
    {
        var (client, sinks) = Create();

        var response = await client.PostAsJsonAsync("/api/capture", new { text = "send email tomorrow" });
        var body = await Body(response);

        Assert.Equal("Todoist", body.GetProperty("app").GetString());
        Assert.Equal("2026-10-17", body.GetProperty("start").GetString());
        Assert.Equal(ItemType.Task, Assert.Single(sinks.Items).Type);
    }

    [Fact]
    public async Task Empty_text_is_bad_request()
    {
        var (client, _) = Create();
        var response = await client.PostAsJsonAsync("/api/capture", new { text = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Forced_event_without_date_is_all_day_today()
    {
        var (client, sinks) = Create();
        var response = await client.PostAsJsonAsync("/api/capture", new { text = "e party" });
        var body = await Body(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("event", body.GetProperty("type").GetString());
        Assert.Equal("2026-10-16", body.GetProperty("start").GetString());
        Assert.True(Assert.Single(sinks.Items).AllDay);
    }

    [Fact]
    public async Task Upstream_failure_is_bad_gateway_with_message()
    {
        var (client, sinks) = Create();
        sinks.Throw = new HttpRequestException("Todoist returned 401");

        var response = await client.PostAsJsonAsync("/api/capture", new { text = "call mom" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("Todoist", (await Body(response)).GetProperty("message").GetString());
    }
}
