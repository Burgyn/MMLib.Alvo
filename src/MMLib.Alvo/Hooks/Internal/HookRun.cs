using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// One invocation of one hook: what it was handed, and what it recorded — rejections, <c>Set</c>s, its calls through the
/// transaction, and a failure its transaction raised into it (budget, self-write) that catching cannot erase.
/// </summary>
internal sealed class HookRun(
    string id, WriteInvocation invocation, IReadOnlySet<string> pointable, IAlvoTransaction? transaction, IAlvoSession? data, CancellationToken token)
{
    private readonly Dictionary<string, object?> _sets = new(StringComparer.Ordinal);
    private int _operations;

    internal string Id { get; } = id;

    internal WriteInvocation Invocation { get; } = invocation;

    internal IAlvoTransaction? Transaction { get; } = transaction;

    internal IAlvoSession? Data { get; } = data;

    internal CancellationToken Token { get; } = token;

    internal HookRejections Rejections { get; } = new(id, pointable);

    internal IReadOnlyDictionary<string, object?> Sets => _sets;

    internal HookFailedException? Failure { get; private set; }

    internal int Operations => Volatile.Read(ref _operations);

    internal bool Wrote { get; private set; }

    internal void Set(string field, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        _sets[field] = RecordWriter.Field(field, value);
    }

    internal void Fail(HookFailedException failure) => Failure ??= failure;

    /// <summary>Replaces the recorded <c>Set</c>s with their checked, widened form (Ruling 47).</summary>
    internal void Replace(IReadOnlyDictionary<string, object?> sets)
    {
        _sets.Clear();
        foreach (var (field, value) in sets)
        {
            _sets[field] = value;
        }
    }

    internal int Count() => Interlocked.Increment(ref _operations);

    internal void MarkWrote() => Wrote = true;
}
