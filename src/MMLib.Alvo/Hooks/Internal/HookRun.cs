using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// One invocation of one hook: what it was handed, and what it recorded — rejections, <c>Set</c>s, its calls through the
/// transaction, and a failure its transaction raised into it (budget, self-write) that catching cannot erase.
/// </summary>
/// <remarks>
/// A hook may record from tasks it starts in parallel, so every recording member is safe to call concurrently; <see cref="Sets"/>
/// hands a snapshot taken under the lock, so a <c>Set</c> a hook's stray task makes later never changes what the runner read.
/// </remarks>
internal sealed class HookRun(
    string id, WriteInvocation invocation, IReadOnlySet<string> pointable, IAlvoTransaction? transaction, IAlvoSession? data, CancellationToken token)
{
    private readonly Dictionary<string, object?> _sets = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private HookFailedException? _failure;
    private bool _wrote;
    private int _operations;

    internal string Id { get; } = id;

    internal WriteInvocation Invocation { get; } = invocation;

    internal IAlvoTransaction? Transaction { get; } = transaction;

    internal IAlvoSession? Data { get; } = data;

    internal CancellationToken Token { get; } = token;

    /// <summary>
    /// The entity's <c>computed</c> and <c>rollup</c> fields: the store fills them, so an image of a row not yet stored lacks
    /// them even when the descriptor declares them required.
    /// </summary>
    internal IReadOnlySet<string> Derived { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    internal HookRejections Rejections { get; } = new(id, pointable);

    internal IReadOnlyDictionary<string, object?> Sets
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, object?>(_sets, StringComparer.Ordinal);
            }
        }
    }

    internal HookFailedException? Failure => Volatile.Read(ref _failure);

    internal int Operations => Volatile.Read(ref _operations);

    internal bool Wrote => Volatile.Read(ref _wrote);

    internal void Set(string field, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        var normalised = RecordWriter.Field(field, value);
        lock (_gate)
        {
            _sets[field] = normalised;
        }
    }

    internal void Fail(HookFailedException failure) => Interlocked.CompareExchange(ref _failure, failure, null);

    /// <summary>Replaces the recorded <c>Set</c>s with their checked, widened form (Ruling 47).</summary>
    internal void Replace(IReadOnlyDictionary<string, object?> sets)
    {
        lock (_gate)
        {
            _sets.Clear();
            foreach (var (field, value) in sets)
            {
                _sets[field] = value;
            }
        }
    }

    internal int Count() => Interlocked.Increment(ref _operations);

    internal void MarkWrote() => Volatile.Write(ref _wrote, true);
}
