using MMLib.Alvo.Schema;

using System.Collections.Immutable;

namespace MMLib.Alvo.Rules;

/// <summary>
/// The rows in a hook stage on this flow (Rulings 35, 43) — an <see cref="AsyncLocal{T}"/> set that flows into a hook's nested
/// calls: the row whose hooks are running (held by the core's runner) and every row of a batch while its hooks run (held by
/// the driver). A nested write to a held row is a self-write, however deep it is made.
/// </summary>
internal static class HeldRows
{
    private static readonly AsyncLocal<ImmutableHashSet<(string Entity, Guid Id)>?> _held = new();

    internal static bool Holds(string entity, Guid id) => _held.Value?.Contains((entity, id)) == true;

    internal static IDisposable Hold(string entity, Guid? id) => HoldAll(entity, id is { } row ? [row] : []);

    internal static IDisposable HoldAll(string entity, IEnumerable<Guid> ids)
    {
        var previous = _held.Value;
        var held = previous ?? [];
        foreach (var id in ids)
        {
            held = held.Add((entity, id));
        }

        _held.Value = held;
        return new Restore(previous);
    }

    internal static Guid? RowOf(WriteInvocation invocation) => (invocation.Saved ?? invocation.New ?? invocation.Old)?[AlvoManagedColumns.Id] as Guid?;

    private sealed class Restore(ImmutableHashSet<(string Entity, Guid Id)>? previous) : IDisposable
    {
        public void Dispose() => _held.Value = previous;
    }
}
