namespace MMLib.Alvo.DocsGen.Limits;

internal static class LimitTexts
{
    internal static IReadOnlyDictionary<string, string> BySource { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AlvoApiOptions.DefaultPageSize"] = "A list request that sends no `limit` gets a page of this many rows.",
        ["AlvoApiOptions.MaxPageSize"] = "A larger `limit` is refused with `422 malformed-query` (`invalid-page-size`); it is never silently capped.",
        ["AlvoApiOptions.MaxRequestBodyBytes"] = "A larger body is refused before it is parsed: `422 validation` (`body-too-large`) on a write, `422 malformed-query` on a query sent as a body.",
        ["AlvoApiOptions.MaxPayloadDepth"] = "A body nested deeper is refused with `422` (`body-too-deep`).",
        ["AlvoApiOptions.MaxPayloadKeys"] = "A body carrying more property names, counted at every depth, is refused with `422` (`body-too-many-fields`). In a batch the count applies per row.",
        ["AlvoApiOptions.MaxBatchRows"] = "A batch with more rows is refused with `422 validation` (`batch-too-many-rows`) and nothing is written; split it into several batches.",
        ["AlvoApiOptions.MaxIdempotencyKeyBytes"] = "A longer `Idempotency-Key`, measured in UTF-8 bytes, is refused rather than shortened, so two different keys never collapse into one. It can only be lowered.",
        ["AlvoFilter.MaxDepth"] = "A filter nested deeper is refused with `422 malformed-query` (`filter-too-deep`).",
        ["AlvoFilter.MaxTerms"] = "A filter with more comparisons and connectives in total is refused with `422 malformed-query` (`filter-too-wide`).",
        ["AlvoFilter.MaxInCandidates"] = "An `in` list with more values is refused with `422 malformed-query` (`too-many-in-candidates`).",
        ["AlvoAuthOptionsValidator.MinimumSecretLength"] = "A dev API key whose secret is shorter stops the host at start. `openssl rand -hex 16` produces exactly this length.",
        ["CelParser.MaxDepth"] = "A CEL expression nested deeper (parentheses, ternaries, unary operators, function arguments) is refused when the descriptor is applied or the host boots with it, so it never runs.",
    };
}
