using Microsoft.EntityFrameworkCore.Metadata;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Turns a field/value payload into the property bag EF's change tracker inserts — the one place an
/// <c>INSERT</c>'s values are prepared, whether they came from <see cref="IAlvoData.CreateAsync"/> or from
/// the test-only seeding seam.
/// </summary>
/// <remarks>
/// Shared by both insert paths on purpose. They already made the same <see langword="null"/> decision twice,
/// and the timestamp normalisation would have been a third and fourth copy of a rule whose whole point is
/// that there is exactly one of it: a fixture that stored an instant differently from production is a fixture
/// that cannot reproduce production, which is how a suite comes to be green about the wrong thing.
/// </remarks>
internal static class WritePropertyBag
{
    /// <summary>
    /// The bag for <paramref name="values"/>, with every value in the representation
    /// <paramref name="rows"/>' own columns hold.
    /// </summary>
    /// <param name="rows">The read model's entity type, the authority for a field's CLR type.</param>
    /// <param name="values">The payload.</param>
    /// <remarks>
    /// A <see langword="null"/> is dropped rather than written: the bag's value type is non-nullable, and an
    /// absent key already means "leave the column at its database default", which for a nullable column is
    /// <c>NULL</c>. On an insert that is indistinguishable from an omitted key and correct for both. On an
    /// update it would not be, which is why that path uses <c>ExecuteUpdate</c> setters instead, where a
    /// <see langword="null"/> setter value is a real <c>SET col = NULL</c>.
    /// </remarks>
    internal static Dictionary<string, object> For(
        IEntityType rows, IEnumerable<KeyValuePair<string, object?>> values)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(values);

        var bag = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (field, value) in values.Where(pair => pair.Value is not null))
        {
            bag[field] = Stored(rows, field, value!);
        }

        return bag;
    }

    /// <summary>
    /// A value as its own column holds it. A field this read model does not map is passed through untouched,
    /// so EF raises its own error for it rather than this method inventing one.
    /// </summary>
    private static object Stored(IEntityType rows, string field, object value) =>
        rows.FindProperty(field) is { } column ? StoredInstant.Stored(column.ClrType, value)! : value;
}
