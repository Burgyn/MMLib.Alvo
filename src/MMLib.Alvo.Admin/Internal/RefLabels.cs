using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Turning the ids in a reference column into the names of the rows they point at.
/// </summary>
/// <remarks>
/// <para>
/// <b>One query per reference column per page, never one per cell.</b> The distinct ids on the page
/// go to the target in a single <c>in</c> filter, with a projection of the id and the label field
/// only — twenty-five rows of a grid cost one round trip per column rather than twenty-five, and the
/// read carries nothing the cell does not draw. The list is split at
/// <see cref="AlvoFilter.MaxInCandidates"/>, because a longer one is refused as a malformed query.
/// </para>
/// <para>
/// <b>It goes through the data port as the operator</b>, like every other read on the Data screen, so
/// a label is only ever a value this caller could have listed through <c>/api</c>. A row their rule
/// excludes, a label field masked from them, or a target they hold no tenant for all end the same
/// way: no label, and the cell shows the short id.
/// </para>
/// </remarks>
internal static class RefLabels
{
    /// <summary>How many characters of a uuid stand for it when there is no label.</summary>
    public const int ShortIdLength = 8;

    /// <summary>The first characters of <paramref name="id"/>.</summary>
    public static string ShortId(Guid id) => id.ToString("D", CultureInfo.InvariantCulture)[..ShortIdLength];

    /// <summary>A stored reference value as a uuid, when it is one.</summary>
    public static Guid? IdOf(object? value) => value switch
    {
        Guid id => id,
        string text when Guid.TryParse(text, out var id) => id,
        _ => null,
    };

    /// <summary>A label value as text; <see langword="null"/> when there is nothing to show.</summary>
    /// <remarks>
    /// A masked field comes back absent and an unset one comes back null; both mean "no label", and so
    /// does an empty or blank string, which would otherwise draw a link with nothing to click.
    /// </remarks>
    public static string? Label(object? value) => value switch
    {
        null => null,
        string text => string.IsNullOrWhiteSpace(text) ? null : text,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>The distinct ids among <paramref name="values"/>, in lists the data port accepts.</summary>
    public static IReadOnlyList<IReadOnlyList<Guid>> Batches(IEnumerable<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return [.. values
            .Select(IdOf)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .Chunk(AlvoFilter.MaxInCandidates)];
    }

    /// <summary>The read that fetches one batch's labels from <paramref name="entity"/>.</summary>
    public static AlvoQuery Query(string entity, string labelField, IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return new AlvoQuery
        {
            Entity = entity,
            Filter = new AlvoComparison(AlvoManagedColumns.Id, AlvoFilterOperator.In, ids),
            Select = [AlvoManagedColumns.Id, labelField],
            Limit = ids.Count,
        };
    }
}
