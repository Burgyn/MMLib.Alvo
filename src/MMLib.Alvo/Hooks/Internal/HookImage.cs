using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>A record as a hook's <typeparamref name="T"/>: itself for <see cref="AlvoRecord"/>, the host type's mapping otherwise.</summary>
/// <typeparam name="T">The hook's record type.</typeparam>
internal static class HookImage<T>
    where T : class
{
    internal static T? Of(AlvoRecord? record) => record switch
    {
        null => null,
        _ when typeof(T) == typeof(AlvoRecord) => (T)(object)record,
        _ => RecordShape.Of(typeof(T)).Materialize<T>(record),
    };
}
