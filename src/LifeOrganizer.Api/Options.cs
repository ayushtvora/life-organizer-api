namespace LifeOrganizer.Api;

public sealed class AppOptions
{
    public const string Section = "App";
    public string ApiKey { get; set; } = "";
    public string TimeZone { get; set; } = "America/Toronto";
    public int DefaultEventMinutes { get; set; } = 60;
}

public sealed class AnthropicOptions
{
    public const string Section = "Anthropic";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-haiku-5-5";
    public int TimeoutSeconds { get; set; } = 25;
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class GoogleOptions
{
    public const string Section = "Google";
    public string ServiceAccountJsonBase64 { get; set; } = "";
    public string CalendarId { get; set; } = "";
}

public sealed class TodoistOptions
{
    public const string Section = "Todoist";
    public string ApiToken { get; set; } = "";
    public string? ProjectId { get; set; }
}

public sealed class IntegrationOptions
{
    public const string Section = "Integrations";
    /// <summary>Parse and format only; don't create anything in Google/Todoist.</summary>
    public bool DryRun { get; set; }
}
