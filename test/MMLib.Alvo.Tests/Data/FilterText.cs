using MMLib.Alvo.Data;

using System.Collections;
using System.Globalization;

namespace MMLib.Alvo.Tests.Data;

/// <summary>A filter tree as one line of text, so a translation is asserted against a readable expectation.</summary>
internal static class FilterText
{
    internal static string Of(AlvoFilter filter) => filter switch
    {
        AlvoComparison comparison => $"{comparison.Field} {comparison.Operator.ToString().ToLowerInvariant()} {Value(comparison.Value)}",
        AlvoAnd and => $"and({string.Join(", ", and.Filters.Select(Of))})",
        AlvoOr or => $"or({string.Join(", ", or.Filters.Select(Of))})",
        AlvoNot not => $"not({Of(not.Filter)})",
        _ => throw new ArgumentException("An unknown filter case.", nameof(filter)),
    };

    private static string Value(object? value) => value switch
    {
        null => "null",
        string text => $"'{text}'",
        bool flag => flag ? "true" : "false",
        DateTimeOffset instant => instant.ToString("O", CultureInfo.InvariantCulture),
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IEnumerable items => $"[{string.Join(", ", items.Cast<object?>().Select(Value))}]",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
