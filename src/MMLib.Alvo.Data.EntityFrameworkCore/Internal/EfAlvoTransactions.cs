using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// <see cref="IAlvoTransactions"/> over the EF data port: open a unit, run the delegate once on its flow through
/// <see cref="UnitRun"/>, commit — or roll back and rethrow as the unit recorded it — and record the unit's hook rejections
/// once it has ended (Ruling 8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Never retried</b> (spec 2026-10-14 §4.5): the delegate runs once. SQLite's writer contention is absorbed before it
/// runs — at the unit's single <c>BEGIN</c>, by this process's write gate and the provider's busy timeout — and any other
/// failure surfaces to the caller, so a deterministic violation never re-runs host code.
/// </para>
/// <para>
/// The delegate runs inside <see cref="WriteUnit.EnterFlow"/>, the one way a unit gets onto the flow (Ruling 48): every
/// call through the unit joins it only from that flow.
/// </para>
/// <para>
/// A unit that suppresses host hooks writes one operations-log row in its own transaction and, once committed, one
/// <c>ManagementAudit</c> warning — the core's template and event name, which the driver cannot call (Ruling 30).
/// </para>
/// </remarks>
/// <param name="data">The data port the unit's calls run through.</param>
/// <param name="scopes">Where each unit's DI scope comes from.</param>
/// <param name="logger">Where a suppressing unit's audit line goes.</param>
internal sealed partial class EfAlvoTransactions(EfAlvoData data, IServiceScopeFactory scopes, ILogger<EfAlvoTransactions>? logger = null) : IAlvoTransactions
{
    private readonly ILogger _logger = logger ?? NullLogger<EfAlvoTransactions>.Instance;

    /// <inheritdoc/>
    public async Task<T> TransactAsync<T>(
        AlvoContext context,
        Func<IAlvoTransaction, CancellationToken, Task<T>> work,
        AlvoTransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);
        AlvoFlow.RefuseInsideUnit("IAlvoTransactions.TransactAsync");
        var settings = AlvoTransactionOptions.Validated(options, context);

        var unit = await data.OpenUnitAsync(context, settings, scopes, cancellationToken).ConfigureAwait(false);
        try
        {
            var (result, audit) = await UnitRun.CommittedAsync(unit, () => AuditedWorkAsync(unit, settings, work, context)).ConfigureAwait(false);
            Audited(audit);

            return result;
        }
        finally
        {
            await unit.DisposeAsync().ConfigureAwait(false);
            await data.FlushRejectionsAsync(unit).ConfigureAwait(false);
        }
    }

    /// <summary>The delegate's run, after the unit's suppression audit row when it suppresses host hooks (Ruling 30).</summary>
    private async Task<(T Result, DeliveryOperationEntry? Audit)> AuditedWorkAsync<T>(
        WriteUnit unit, AlvoTransactionOptions settings, Func<IAlvoTransaction, CancellationToken, Task<T>> work, AlvoContext context)
    {
        var audit = settings.SuppressHostHooks
            ? await data.AuditSuppressionAsync(unit, settings.Reason!, unit.Token).ConfigureAwait(false)
            : null;

        return (await work(new EfAlvoTransaction(data, unit, context), unit.Token).ConfigureAwait(false), audit);
    }

    private void Audited(DeliveryOperationEntry? entry)
    {
        if (entry is not null)
        {
            ManagementAudit(_logger, entry.Actor, entry.Operation, entry.Target, entry.Affected ?? 0, entry.Reason ?? string.Empty);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Management audit: {Actor} made '{Operation}' on '{Target}' ({Affected} changed). Reason: {Reason}")]
    private static partial void ManagementAudit(ILogger logger, string actor, string operation, string target, int affected, string reason);
}
