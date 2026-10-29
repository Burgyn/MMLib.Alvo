using MMLib.Alvo.Schema;

using System.Globalization;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// A typed record as the field values a write sends — the read direction of <see cref="RecordValue"/> reversed: an enum
/// member as its declared value, a whole number as a <see cref="long"/>, every other value as it is — and the fields Alvo
/// manages or the store maintains left out.
/// </summary>
internal static class RecordWriter
{
    /// <summary>
    /// Whether a write sends <paramref name="field"/>: never a column Alvo manages, except the one a caller may write — a
    /// create's <c>tenant_id</c> — by the port's own rule (<see cref="AlvoManagedColumns.IsCallerWritable"/>).
    /// </summary>
    internal static bool IsWritten(string field, bool isUpdate) =>
        !AlvoManagedColumns.All.Contains(field) || AlvoManagedColumns.IsCallerWritable(field, isUpdate);

    /// <summary>Throws when <paramref name="shape"/> has a mapping problem no descriptor can cure, before anything is sent.</summary>
    /// <exception cref="InvalidOperationException">The type cannot be written.</exception>
    internal static void EnsureWritable(RecordShape shape)
    {
        if (shape.Problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{shape.Name} cannot be written: {string.Join(" ", shape.Problems.Select(problem => problem.Message))}");
        }
    }

    /// <summary>The fields <paramref name="record"/> writes: a wrapper's record, or a plain type's mapped properties.</summary>
    /// <param name="record">The record.</param>
    /// <param name="isCreate">Whether the write is a create rather than a replace.</param>
    /// <param name="entity">The entity's applied schema, when the session can name it.</param>
    /// <remarks>
    /// <para>
    /// A create leaves out every field whose value is <see langword="null"/>, so the field's descriptor default applies —
    /// or, with none, the column stays empty — exactly as an untyped create that does not name the field. A replace sends
    /// every field, <see langword="null"/> included, because it replaces the whole row. A non-nullable property's CLR
    /// default (<c>0</c>, <see langword="false"/>, the first enum member) is a value, and is sent (Ruling 36) — except a
    /// create's <c>tenant_id</c> of <see cref="Guid.Empty"/>, the reserved "no tenant", which is refused (Ruling 53).
    /// </para>
    /// <para>
    /// A field the store maintains — <c>computed</c> or <c>rollup</c> in <paramref name="entity"/> — is never sent, like a
    /// managed column, so a read-model type that maps one stays writable (Ruling 52).
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The type has a mapping problem no descriptor can cure.</exception>
    /// <exception cref="ArgumentException">A create's <c>tenant_id</c> is <see cref="Guid.Empty"/>, or an enum member names no value.</exception>
    internal static Dictionary<string, object?> Values<T>(T record, bool isCreate, EntitySchema? entity)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(record);
        var shape = RecordShape.Of(typeof(T));
        EnsureWritable(shape);
        var maintained = Maintained(entity);
        var fields = record is IAlvoEntity wrapper
            ? wrapper.Record.Values.Select(pair => (Name: pair.Key, Value: Field(pair.Key, pair.Value)))
            : shape.Properties.Select(property => (Name: property.Field, Value: Field(property, property.Property.GetValue(record))));

        var values = fields
            .Where(field => IsWritten(field.Name, isUpdate: !isCreate) && !maintained.Contains(field.Name) && !(isCreate && field.Value is null))
            .ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);

        return isCreate ? WithTenant(values, shape, nameof(record)) : values;
    }

    /// <summary>The fields of <paramref name="entity"/> the store maintains itself: <c>computed</c> and <c>rollup</c>.</summary>
    internal static IReadOnlySet<string> Maintained(EntitySchema? entity) => entity is null
        ? new HashSet<string>(StringComparer.Ordinal)
        : entity.Fields.Where(field => field.ComputedExpression is not null || field.Rollup is not null)
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>One property's value as its field holds it.</summary>
    /// <exception cref="ArgumentException">An enum member names no declared value.</exception>
    internal static object? Field(MappedProperty property, object? value) => Field(property.Field, value);

    /// <summary>One value as field <paramref name="field"/> holds it — a mapped property's, or a wrapper's <c>Record["field"]</c>.</summary>
    /// <exception cref="ArgumentException">An enum member names no declared value.</exception>
    internal static object? Field(string field, object? value) => value switch
    {
        null => null,
        Enum member => EnumMap.Of(member.GetType()).ValueOf(member)
            ?? throw new ArgumentException($"{member.GetType().Name}.{member} names no value of field '{field}': name it with [AlvoEnumValue(\"…\")].", nameof(value)),
        sbyte or byte or short or ushort or int or uint => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        _ => value,
    };

    /// <summary>The stored record as <typeparamref name="T"/> — or <see langword="null"/> when the caller's read-back lacks a field <typeparamref name="T"/> maps.</summary>
    /// <remarks>
    /// A write answers the row as the caller's own <c>get</c> would — only its id when the rule does not return the row, and
    /// without a hidden field. The write has happened; answering <see langword="null"/> rather than throwing keeps it from
    /// looking failed, and inside a unit from being rolled back by an exception the host did not cause.
    /// </remarks>
    internal static T? Readable<T>(AlvoRecord stored)
        where T : class
    {
        var shape = RecordShape.Of(typeof(T));

        return shape.IsWrapper || shape.Properties.All(property => stored.Values.ContainsKey(property.Field)) ? stored.As<T>() : null;
    }

    /// <summary>Refuses a create's <c>tenant_id</c> of <see cref="Guid.Empty"/>: the reserved "no tenant", never a row's tenant (Ruling 53).</summary>
    private static Dictionary<string, object?> WithTenant(Dictionary<string, object?> values, RecordShape shape, string parameter) =>
        values.TryGetValue(AlvoManagedColumns.TenantId, out var tenant) && IsNoTenant(tenant)
            ? throw new ArgumentException(
                $"{shape.Name} sends tenant_id {Guid.Empty}, the reserved \"no tenant\", so the create is refused before it is sent. Fix: "
                + "set TenantId to the row's tenant (the caller's own), or declare it Guid? and leave it null to send none.",
                parameter)
            : values;

    private static bool IsNoTenant(object? tenant) => tenant switch
    {
        Guid id => id == Guid.Empty,
        string text => Guid.TryParse(text, out var id) && id == Guid.Empty,
        _ => false,
    };
}
