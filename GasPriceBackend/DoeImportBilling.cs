public static class DoeImportBilling
{
    public static AiChatTokenUsage? SumUsage(IReadOnlyList<DoeImportReportStatus> reports)
    {
        var billed = reports.Where(r => r.Model is not null || r.Usage is not null).ToArray();
        if (billed.Length == 0 || billed.Any(r => r.Usage is null)) return null;
        return new AiChatTokenUsage(
            billed.Sum(r => r.Usage!.InputTokens),
            billed.Sum(r => r.Usage!.CachedInputTokens),
            billed.Sum(r => r.Usage!.OutputTokens),
            billed.Sum(r => r.Usage!.TotalTokens));
    }

    public static OpenAiCostEstimate? SumCost(IReadOnlyList<DoeImportReportStatus> reports)
    {
        var billed = reports.Where(r => r.Model is not null || r.Usage is not null).ToArray();
        if (billed.Length == 0 || billed.Any(r => r.EstimatedCost is null)) return null;
        var costs = billed.Select(r => r.EstimatedCost!).ToArray();
        var first = costs[0];
        // The shared estimate contract has one rate set. Keep mixed-rate costs per PDF.
        if (costs.Any(c => c.Currency != first.Currency ||
            c.InputPricePerMillionTokens != first.InputPricePerMillionTokens ||
            c.CachedInputPricePerMillionTokens != first.CachedInputPricePerMillionTokens ||
            c.OutputPricePerMillionTokens != first.OutputPricePerMillionTokens)) return null;
        return first with
        {
            InputCost = costs.Sum(c => c.InputCost),
            OutputCost = costs.Sum(c => c.OutputCost),
            WebSearchCost = costs.Sum(c => c.WebSearchCost),
            TotalCost = costs.Sum(c => c.TotalCost),
            WebSearchCalls = costs.Sum(c => c.WebSearchCalls)
        };
    }
}
