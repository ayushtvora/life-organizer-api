namespace LifeOrganizer.Api.Parsing;

/// <summary>Rules first; the LLM only when rules are not confident. Falls back to the rule result if the LLM fails.</summary>
public sealed class HybridParser(RuleBasedParser rules, ILlmParser llm, ILogger<HybridParser> logger)
{
    public async Task<ParseResult> ParseAsync(string input, CancellationToken ct)
    {
        var (forced, text) = CaptureText.SplitOverride(input);
        var ruleResult = rules.Parse(text, forced);

        if (ruleResult.Confidence == Confidence.High || !llm.Enabled)
            return ruleResult;

        logger.LogInformation("Rules not confident ({Reasons}); asking LLM", string.Join("; ", ruleResult.Reasons));
        try
        {
            var item = await llm.ParseAsync(text, forced, ct);
            if (item is not null) return new ParseResult(item, Confidence.High, ruleResult.Reasons);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "LLM parse failed; using rule result");
        }
        return ruleResult;
    }
}
