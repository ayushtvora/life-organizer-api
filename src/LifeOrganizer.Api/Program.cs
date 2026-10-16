using System.Text.Json;
using LifeOrganizer.Api;
using LifeOrganizer.Api.Parsing;
using LifeOrganizer.Api.Sinks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.Section));
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection(AnthropicOptions.Section));
builder.Services.Configure<GoogleOptions>(builder.Configuration.GetSection(GoogleOptions.Section));
builder.Services.Configure<TodoistOptions>(builder.Configuration.GetSection(TodoistOptions.Section));
builder.Services.Configure<IntegrationOptions>(builder.Configuration.GetSection(IntegrationOptions.Section));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    // Keep emoji readable in the response instead of \uXXXX escapes.
    o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LocalClock>();
builder.Services.AddSingleton<RuleBasedParser>();
builder.Services.AddSingleton<ILlmParser, LlmParser>();
builder.Services.AddSingleton<HybridParser>();
builder.Services.AddSingleton<ConfirmationFormatter>();

if (builder.Configuration.GetValue<bool>($"{IntegrationOptions.Section}:DryRun"))
{
    builder.Services.AddSingleton<ICalendarSink, DryRunCalendarSink>();
    builder.Services.AddSingleton<ITaskSink, DryRunTaskSink>();
}
else
{
    builder.Services.AddSingleton<ICalendarSink, GoogleCalendarSink>();
    builder.Services.AddHttpClient<ITaskSink, TodoistSink>(c =>
    {
        c.BaseAddress = new Uri(TodoistSink.BaseAddress);
        c.Timeout = TimeSpan.FromSeconds(15);
    });
}

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapCapture();

app.Run();

public partial class Program;
