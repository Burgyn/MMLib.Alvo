using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.InboundWebhooks.Internal;

/// <summary>
/// Once an hour, forgets inbound webhook ids received more than seven days ago (spec 2026-10-14 §4.9; #154 retention) and
/// command keys claimed more than a day ago (Ruling 41): a key replayed after that runs as a new request. One sweep for
/// both framework ledgers, so a host runs one timer, not two.
/// </summary>
internal sealed class LedgerSweeper(IServiceProvider services, TimeProvider time, ILogger<LedgerSweeper> logger) : BackgroundService
{
    /// <summary>Runs one sweep; answers how many ids and keys it forgot.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed sweep must not stop the host: it is logged and retried an hour later.")]
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = time.GetUtcNow();
            var webhooks = services.GetService<IInboundWebhookLedger>() is { } inbound
                ? await inbound.SweepAsync(now - InboundWebhookLimits.Retention, cancellationToken).ConfigureAwait(false)
                : 0;
            var commands = services.GetService<ICommandLedger>() is { } keys
                ? await keys.SweepAsync(now - CommandLedgerLimits.Retention, cancellationToken).ConfigureAwait(false)
                : 0;
            WebhookLog.Swept(logger, webhooks, commands);
            return webhooks + commands;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            WebhookLog.SweepFailed(logger, failure);
            return 0;
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(InboundWebhookLimits.SweepInterval, time);
        do
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
