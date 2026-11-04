using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>A record as a hook's <typeparamref name="T"/>: itself for <see cref="AlvoRecord"/>, the host type's mapping otherwise.</summary>
/// <typeparam name="T">The hook's record type.</typeparam>
/// <remarks>
/// An image of a row not yet stored lacks what the store has not written: a create's <c>id</c> (minted per attempt — Ruling 13),
/// the audit columns and <c>deleted_at</c>, a replace's <c>tenant_id</c> (Ruling 60), and every nullable field the caller omitted
/// or sent as <see langword="null"/> (the property bag drops nulls). Those properties keep their defaults (<see cref="Guid.Empty"/>,
/// <see langword="null"/>). A hook image is never masked, so a missing field never means "hidden from the caller"; a missing
/// field whose property cannot hold <see langword="null"/> and that Alvo does not manage is still refused, as a typed read refuses it.
/// </remarks>
internal static class HookImage<T>
    where T : class
{
    private static readonly HashSet<string> _notYetStored = new(
        [AlvoManagedColumns.Id, AlvoManagedColumns.TenantId, AlvoManagedColumns.DeletedAt, .. AlvoManagedColumns.Audit], StringComparer.Ordinal);

    private static bool MayBeAbsent(MappedProperty property) => property.AcceptsNull || _notYetStored.Contains(property.Field);

    internal static T? Of(AlvoRecord? record) => record switch
    {
        null => null,
        _ when typeof(T) == typeof(AlvoRecord) => (T)(object)record,
        _ => RecordShape.Of(typeof(T)).Materialize<T>(record, MayBeAbsent),
    };
}
