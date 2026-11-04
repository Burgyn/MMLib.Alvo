using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>A record as a hook's <typeparamref name="T"/>: itself for <see cref="AlvoRecord"/>, the host type's mapping otherwise.</summary>
/// <typeparam name="T">The hook's record type.</typeparam>
/// <remarks>
/// The driver hands a create's row without <c>id</c> (minted per attempt — Ruling 13) and a replace's validated row without
/// the audit columns (Ruling 60), so those fields alone may be missing: their properties keep their defaults
/// (<see cref="Guid.Empty"/>, <see langword="null"/>). Any other missing field is refused, as a typed read refuses it.
/// </remarks>
internal static class HookImage<T>
    where T : class
{
    private static readonly HashSet<string> _notYetStored = new([AlvoManagedColumns.Id, .. AlvoManagedColumns.Audit], StringComparer.Ordinal);

    internal static T? Of(AlvoRecord? record) => record switch
    {
        null => null,
        _ when typeof(T) == typeof(AlvoRecord) => (T)(object)record,
        _ => RecordShape.Of(typeof(T)).Materialize<T>(record, _notYetStored),
    };
}
