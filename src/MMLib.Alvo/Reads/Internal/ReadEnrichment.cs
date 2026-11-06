using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

using System.Runtime.ExceptionServices;

namespace MMLib.Alvo.Reads.Internal;

/// <summary>
/// The enrichment of one read: every bound enricher of the entity, in registration order, over each returned row — each
/// handed a fresh view of what the previous one left. A class enricher is built once, at its first row, from the read's own
/// scope; the scope and every instance Alvo built end with the enrichment.
/// </summary>
/// <remarks>
/// It fails closed: a move an enricher may not make fails the read with its reason, and anything else it throws — but a
/// cancellation — fails it with <c>exception</c>, logged with the id and the exception type only.
/// </remarks>
internal sealed class ReadEnrichment : IReadEnrichment
{
    private readonly IReadOnlyList<ReadExtensionRegistration> _enrichers;
    private readonly EnricherCall?[] _calls;
    private readonly EntitySchema _schema;
    private readonly AlvoContext _caller;
    private readonly DataOperation _operation;
    private readonly AsyncServiceScope? _scope;
    private readonly ILogger _logger;
    private FormatCatalog? _formats;

    internal ReadEnrichment(
        IReadOnlyList<ReadExtensionRegistration> enrichers, EntitySchema schema, AlvoContext caller, DataOperation operation,
        AsyncServiceScope? scope, ILogger logger)
    {
        _enrichers = enrichers;
        _calls = new EnricherCall?[enrichers.Count];
        _schema = schema;
        _caller = caller;
        _operation = operation;
        _scope = scope;
        _logger = logger;
    }

    public AlvoRecord Enrich(AlvoRecord row)
    {
        var shaping = new RowShaping(_schema, row, Formats);
        for (var index = 0; index < _enrichers.Count; index++)
        {
            Run(index, shaping);
        }

        return shaping.ToRecord();
    }

    /// <summary>
    /// Disposes every instance Alvo built and then the read's scope — each attempted even when an earlier one throws, so a
    /// failing disposal never leaks the scope. A failure is the class enrichers' failure, as a narrowing's is.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var owned = await DisposeOwnedAsync().ConfigureAwait(false);
        var scope = await DisposeScopeAsync().ConfigureAwait(false);
        switch (owned ?? scope)
        {
            case null:
                return;
            case OperationCanceledException cancelled:
                ExceptionDispatchInfo.Throw(cancelled);
                return;
            case { } cause:
                throw ReadFailures.Failed(_logger, [.. _enrichers.Where(read => read.Invoker.IsClass).Select(read => read.Id)], HookFailedException.Exception, cause);
        }
    }

    private async ValueTask<Exception?> DisposeOwnedAsync()
    {
        Exception? first = null;
        foreach (var owned in _calls.Select(call => call?.Owned).OfType<object>())
        {
            var failure = await GuardedAsync(() => DisposeOneAsync(owned)).ConfigureAwait(false);
            first ??= failure;
        }

        return first;
    }

    private async ValueTask<Exception?> DisposeScopeAsync() =>
        _scope is { } scope ? await GuardedAsync(scope.DisposeAsync).ConfigureAwait(false) : null;

    private static async ValueTask<Exception?> GuardedAsync(Func<ValueTask> dispose)
    {
        try
        {
            await dispose().ConfigureAwait(false);
            return null;
        }
        catch (Exception cause)
        {
            return cause;
        }
    }

    private FormatCatalog Formats() => _formats ??= FormatCatalog.Build([_schema]);

    private IServiceProvider Services => _scope?.ServiceProvider ?? EmptyServices.Instance;

    private void Run(int index, RowShaping shaping)
    {
        var enricher = _enrichers[index];
        try
        {
            var call = _calls[index] ??= enricher.Invoker.Enricher!(Services);
            call.Call(shaping, _caller, _operation);
            if (shaping.Refusal is { } swallowed)
            {
                throw new ReadShapingRefusal(swallowed);
            }
        }
        catch (ReadShapingRefusal refusal)
        {
            throw ReadFailures.Failed(_logger, enricher.Id, refusal.Reason, cause: null);
        }
        catch (Exception cause) when (cause is not OperationCanceledException)
        {
            throw ReadFailures.Failed(_logger, enricher.Id, HookFailedException.Exception, cause);
        }
    }

    private static async ValueTask DisposeOneAsync(object owned)
    {
        if (owned is IAsyncDisposable asynchronous)
        {
            await asynchronous.DisposeAsync().ConfigureAwait(false);
        }
        else if (owned is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
