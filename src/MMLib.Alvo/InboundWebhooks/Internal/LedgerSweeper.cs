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
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed sweep — a provider timeout surfaced as a cancellation included — must not stop the host: it is logged and retried an hour later. Only the host's own stop ends it.")]
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
            Log(webhooks, commands);
            return webhooks + commands;
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            WebhookLog.SweepFailed(logger, failure);
            return 0;
        }
    }

    private void Log(int webhooks, int commands)
    {
        if (webhooks + commands == 0)
        {
            WebhookLog.NothingSwept(logger);
            return;
        }

        WebhookLog.Swept(logger, webhooks, commands);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing escapes: the host stopping ends the loop quietly — a host disposed without <c>StopAsync</c> included, which
    /// would otherwise report this service as failed and stop the application — and a provider failure raised while it
    /// stops is logged.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An exception escaping ExecuteAsync stops the host; a sweep that fails while the host stops is logged instead.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SweepUntilStoppedAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            WebhookLog.SweeperStopped(logger, failure);
        }
    }

    private async Task SweepUntilStoppedAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(InboundWebhookLimits.SweepInterval, time);
        do
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
