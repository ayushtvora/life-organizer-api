using LifeOrganizer.Api.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LifeOrganizer.Api.Tests;

public class HybridParserTests
{
    private sealed class FakeLlm(bool enabled, Func<ParsedItem?>? respond = null) : ILlmParser
    {
        public int Calls { get; private set; }
        public ItemType? LastForced { get; private set; }
        public bool Enabled => enabled;

        public Task<ParsedItem?> ParseAsync(string text, ItemType? forced, CancellationToken ct)
        {
            Calls++;
            LastForced = forced;
            return Task.FromResult(respond?.Invoke());
        }
    }

    private static readonly ParsedItem LlmItem = new()
    {
        Type = ItemType.Event, Title = "Dinner", Start = new DateTime(2026, 10, 16, 19, 0, 0),
        End = new DateTime(2026, 10, 16, 20, 0, 0), Parser = "llm",
    };

    private static HybridParser Create(FakeLlm llm) =>
        new(new RuleBasedParser(TestClock.Create(), Options.Create(TestClock.App)), llm, NullLogger<HybridParser>.Instance);

    [Fact]
    public async Task Confident_rules_skip_the_llm()
    {
        var llm = new FakeLlm(enabled: true, () => LlmItem);
        var result = await Create(llm).ParseAsync("do laundry 5am", default);

        Assert.Equal(0, llm.Calls);
        Assert.Equal("rules", result.Item.Parser);
    }

    [Fact]
    public async Task Low_confidence_uses_the_llm()
    {
        var llm = new FakeLlm(enabled: true, () => LlmItem);
        var result = await Create(llm).ParseAsync("pay bill oct 1 2025", default);

        Assert.Equal(1, llm.Calls);
        Assert.Equal("llm", result.Item.Parser);
    }

    [Fact]
    public async Task Override_prefix_is_passed_to_the_llm()
    {
        var llm = new FakeLlm(enabled: true, () => LlmItem);
        await Create(llm).ParseAsync("event: party oct 1 2025", default);

        Assert.Equal(ItemType.Event, llm.LastForced);
    }

    [Fact]
    public async Task Llm_failure_falls_back_to_rules()
    {
        var llm = new FakeLlm(enabled: true, () => throw new HttpRequestException("boom"));
        var result = await Create(llm).ParseAsync("pay bill oct 1 2025", default);

        Assert.Equal("rules", result.Item.Parser);
        Assert.Equal("Pay bill", result.Item.Title);
    }

    [Fact]
    public async Task Llm_refusal_falls_back_to_rules()
    {
        var llm = new FakeLlm(enabled: true, () => null);
        var result = await Create(llm).ParseAsync("pay bill oct 1 2025", default);

        Assert.Equal("rules", result.Item.Parser);
    }

    [Fact]
    public async Task Disabled_llm_is_never_called()
    {
        var llm = new FakeLlm(enabled: false, () => LlmItem);
        var result = await Create(llm).ParseAsync("pay bill oct 1 2025", default);

        Assert.Equal(0, llm.Calls);
        Assert.Equal(Confidence.Low, result.Confidence);
    }
}
