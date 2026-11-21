using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using MMLib.Alvo.Sweeping;

namespace MMLib.Alvo.InboundWebhooks.Internal;

/// <summary>
/// The framework sweeper's first job (spec 2026-10-14 §4.9; #154 retention): forgets inbound webhook ids received more
/// than seven days ago. Resolved per tick, so a host whose provider registers no ledger sweeps nothing here.
/// </summary>
/// <param name="services">The host's services.</param>
internal sealed class InboundWebhookLedgerSweepJob(IServiceProvider services) : ISweepJob
{
    /// <inheritdoc/>
    public string Name => "inbound webhook ids";

    /// <inheritdoc/>
    public Task<int> SweepAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        services.GetService<IInboundWebhookLedger>() is { } ledger
            ? ledger.SweepAsync(now - InboundWebhookLimits.Retention, limit, ct)
            : Task.FromResult(0);
}

/// <summary>
/// The framework sweeper's second job (Ruling 41): forgets command keys claimed more than a day ago, so a key replayed
/// after that runs as a new request.
/// </summary>
/// <param name="services">The host's services.</param>
internal sealed class CommandLedgerSweepJob(IServiceProvider services) : ISweepJob
{
    /// <inheritdoc/>
    public string Name => "command keys";

    /// <inheritdoc/>
    public Task<int> SweepAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        services.GetService<ICommandLedger>() is { } ledger
            ? ledger.SweepAsync(now - CommandLedgerLimits.Retention, limit, ct)
            : Task.FromResult(0);
}
