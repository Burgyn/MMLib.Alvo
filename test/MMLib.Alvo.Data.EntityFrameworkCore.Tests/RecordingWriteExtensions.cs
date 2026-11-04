using MMLib.Alvo.Rules;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>A write-hook runner that records what the driver hands it, and answers what a fact sets.</summary>
internal sealed class RecordingWriteExtensions : IWriteExtensionRunner
{
    internal HashSet<(string Entity, AlvoWriteOperation Operation)> InTransaction { get; } = [];

    internal HashSet<(string Entity, AlvoWriteOperation Operation)> Validating { get; } = [];

    internal ConcurrentQueue<(string Stage, WriteInvocation Invocation, bool UnitOnFlow, bool? Autocommit)> Calls { get; } = new();

    /// <summary>Whether the world's connection holds no open transaction at the moment of a call (Ruling 44); set by the fact.</summary>
    internal Func<bool>? Autocommit { get; set; }

    internal Dictionary<string, object?> Patch { get; } = new(StringComparer.Ordinal);

    internal Func<WriteInvocation, CancellationToken, Task<string?>>? After { get; set; }

    internal Exception? RejectBefore { get; set; }

    /// <summary>Runs inside every before-write call, so a fact can look at what the flow carries while a hook runs.</summary>
    internal Action<WriteInvocation>? OnBefore { get; set; }

    public bool Validates(string entity, AlvoWriteOperation operation) => Validating.Contains((entity, operation));

    public bool RunsInTransaction(string entity, AlvoWriteOperation operation) => InTransaction.Contains((entity, operation));

    public Task ValidateAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        Calls.Enqueue(("validate", invocation, AlvoFlow.Unit is not null, Autocommit?.Invoke()));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, object?>> BeforeWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        Calls.Enqueue(("before", invocation, AlvoFlow.Unit is not null, Autocommit?.Invoke()));
        OnBefore?.Invoke(invocation);
        if (RejectBefore is { } rejection)
        {
            throw rejection;
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(invocation.Operation == AlvoWriteOperation.Delete ? new Dictionary<string, object?>() : Patch);
    }

    public Task<string?> AfterWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        Calls.Enqueue(("after", invocation, AlvoFlow.Unit is not null, Autocommit?.Invoke()));
        return After?.Invoke(invocation, cancellationToken) ?? Task.FromResult<string?>(null);
    }

    internal IReadOnlyList<string> Stages => [.. Calls.Select(call => call.Stage)];
}

/// <summary>A row-scoped refusal, as the core's rejection is one, without naming the core.</summary>
internal sealed class RowScopedRefusal() : Exception("refused by a hook"), IRowRefusal
{
    public AlvoRowRefusal At(int index) => new(index, "refused-by-hook", Message, null);
}
