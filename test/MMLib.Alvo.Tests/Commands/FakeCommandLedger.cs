using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>An in-memory <see cref="ICommandLedger"/> that records what the runner asked of it; it grants a token per claim.</summary>
internal sealed class FakeCommandLedger : ICommandLedger
{
    private readonly ConcurrentDictionary<(string, string), (string Fingerprint, bool Committed, CommandAnswer? Answer)> _rows = new();

    internal List<string> Calls { get; } = [];

    internal List<AlvoEvent> Completions { get; } = [];

    internal Exception? ReleaseFailure { get; set; }

    internal Exception? AnswerFailure { get; set; }

    public Task<CommandClaim> ClaimAsync(CommandKey key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Calls.Add("claim");
        if (!_rows.TryGetValue((key.Scope, key.Key), out var row))
        {
            _rows[(key.Scope, key.Key)] = (key.Fingerprint, false, null);
            return Task.FromResult(CommandClaim.Granted(Guid.NewGuid().ToString("N")));
        }

        return Task.FromResult(row.Fingerprint != key.Fingerprint ? CommandClaim.Mismatch
            : row.Answer is { } answer ? CommandClaim.Answered(answer)
            : row.Committed ? CommandClaim.Unanswered
            : CommandClaim.InFlight);
    }

    public Task AnswerAsync(CommandKey key, CommandAnswer answer, CancellationToken cancellationToken)
    {
        Calls.Add("answer");
        if (AnswerFailure is { } failure)
        {
            throw failure;
        }

        _rows[(key.Scope, key.Key)] = _rows[(key.Scope, key.Key)] with { Answer = answer };
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(CommandKey key, CancellationToken cancellationToken)
    {
        Calls.Add("release");
        if (ReleaseFailure is { } failure)
        {
            throw failure;
        }

        _rows.TryRemove((key.Scope, key.Key), out _);
        return Task.CompletedTask;
    }

    public Task CompleteAsync(AlvoEvent completion, CommandKey? key, CommandAnswer? answer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Calls.Add("complete");
        Completions.Add(completion);
        if (key is not null)
        {
            _rows[(key.Scope, key.Key)] = (key.Fingerprint, true, answer);
        }

        return Task.CompletedTask;
    }

    public Task<int> SweepAsync(DateTimeOffset claimedBefore, CancellationToken cancellationToken) => Task.FromResult(0);

    internal void Commit(CommandKey key) => _rows[(key.Scope, key.Key)] = _rows[(key.Scope, key.Key)] with { Committed = true };
}

/// <summary>
/// A unit that only records the command's own writes into it. It implements every member of <see cref="IAlvoTransaction"/>
/// and <see cref="IAlvoSession"/> — <c>For(AlvoContext)</c> belongs to <c>IAlvoSessions</c>, not to a session (Ruling 49).
/// </summary>
internal sealed class FakeCommandUnit(FakeCommandLedger ledger, AlvoContext context) : IAlvoTransaction, IAlvoCommandUnit
{
    internal List<AlvoEvent> Published { get; } = [];

    public AlvoContext Context => context;

    public Task PublishFrameworkAsync(AlvoEvent @event, CancellationToken cancellationToken)
    {
        Published.Add(@event);
        return Task.CompletedTask;
    }

    public Task CommitCommandAsync(CommandKey key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        key.Token.ShouldNotBeNull("a run commits only under the claim it was granted");
        ledger.Commit(key);
        return Task.CompletedTask;
    }

    public Task<AlvoPage> QueryAsync(AlvoQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlvoRecord?> GetAsync(string entity, Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlvoRecord> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AlvoRecord(new Dictionary<string, object?>(values) { ["id"] = Guid.NewGuid() }));

    public Task<AlvoRecord> UpdateAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlvoReplaceResult> ReplaceAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeleteAsync(string entity, Guid id, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlvoBatchResult> CreateManyAsync(string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public IAlvoTransaction AsSystem() => throw new NotSupportedException();
}

/// <summary>Runs every delegate on one <see cref="FakeCommandUnit"/>; "commits" when it returns, or rolls back when told to.</summary>
internal sealed class FakeTransactions(FakeCommandUnit unit) : IAlvoTransactions
{
    internal bool RollBack { get; set; }

    public async Task<T> TransactAsync<T>(
        AlvoContext context, Func<IAlvoTransaction, CancellationToken, Task<T>> work, AlvoTransactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await work(unit, cancellationToken);
        return RollBack ? throw new InvalidOperationException("the unit rolled back") : result;
    }
}
