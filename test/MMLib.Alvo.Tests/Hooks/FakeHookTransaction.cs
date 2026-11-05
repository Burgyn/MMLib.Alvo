using MMLib.Alvo.Data;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>A transaction double that records every call and answers a fixed record; a hook's tests never touch a database.</summary>
internal sealed class FakeHookTransaction(AlvoContext context) : IAlvoTransaction
{
    internal ConcurrentQueue<string> Calls { get; } = new();

    internal CancellationToken LastToken { get; private set; }

    /// <summary>The system view <see cref="AsSystem"/> handed out, kept so a fact can read what reached it.</summary>
    internal FakeHookTransaction? System { get; private set; }

    public AlvoContext Context => context;

    public Task<AlvoPage> QueryAsync(AlvoQuery query, CancellationToken cancellationToken = default) => Answer("query", AlvoPage.Empty, cancellationToken);

    public Task<AlvoRecord?> GetAsync(string entity, Guid id, CancellationToken cancellationToken = default) => Answer<AlvoRecord?>($"get {entity}/{id}", null, cancellationToken);

    public Task<AlvoRecord> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        Answer($"create {entity}", new AlvoRecord(values), cancellationToken);

    public Task<AlvoRecord> UpdateAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        Answer($"update {entity}/{id}", new AlvoRecord(values), cancellationToken);

    public Task<AlvoReplaceResult> ReplaceAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        Answer($"replace {entity}/{id}", AlvoReplaceResult.ReplacedRow(new AlvoRecord(values)), cancellationToken);

    public Task DeleteAsync(string entity, Guid id, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        Answer($"delete {entity}/{id}", true, cancellationToken);

    public Task<AlvoBatchResult> CreateManyAsync(string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) =>
        Answer($"create-many {entity}", AlvoBatchResult.Wrote([], rows.Count), cancellationToken);

    public Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, CancellationToken cancellationToken = default) =>
        Answer($"update-many {entity}", AlvoBatchResult.Wrote([], rows.Count), cancellationToken);

    public Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) =>
        Answer($"delete-many {entity}", AlvoBatchResult.Wrote([], ids.Count), cancellationToken);

    public Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken cancellationToken = default) =>
        Answer($"publish {type}", true, cancellationToken);

    public IAlvoTransaction AsSystem() => System ??= new FakeHookTransaction(AlvoContext.System(context.Tenant) with { Provenance = context.Provenance });

    private Task<T> Answer<T>(string call, T answer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LastToken = token;
        Calls.Enqueue(call);
        return Task.FromResult(answer);
    }
}
