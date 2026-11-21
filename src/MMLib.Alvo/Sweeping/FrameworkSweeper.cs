using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Sweeping;

/// <summary>
/// The one framework sweeper (#527): once an hour it runs every registered <see cref="ISweepJob"/>, each in batches of
/// <see cref="SweepLimits.BatchSize"/> while a batch comes back full, at most <see cref="SweepLimits.MaxBatches"/> times.
/// It began as the ledger sweep (spec 2026-10-14 §4.9, Ruling 41), whose two ledgers are now its first jobs.
/// </summary>
/// <remarks>
/// Never fatal: a job that fails — a provider timeout surfaced as a cancellation included, or a deadlock between two
/// replicas deleting overlapping batches — is logged and retried next tick, and the other jobs still run. Only the host's
/// own stop ends a sweep.
/// </remarks>
internal sealed partial class FrameworkSweeper(IServiceProvider services, TimeProvider time, ILogger<FrameworkSweeper> logger) : BackgroundService
{
    /// <summary>Runs every job once; answers how many rows they deleted together.</summary>
    /// <param name="cancellationToken">The host's stopping token.</param>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var total = 0;
        foreach (var job in services.GetServices<ISweepJob>())
        {
            total += await JobAsync(job, now, cancellationToken).ConfigureAwait(false);
        }

        if (total == 0)
        {
            NothingSwept(logger);
        }

        return total;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing escapes: the host stopping ends the loop quietly — a host disposed without <c>StopAsync</c> included, which
    /// would otherwise report this service as failed and stop the application — and a failure raised while it stops is
    /// logged.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An exception escaping ExecuteAsync stops the host; a sweep that fails while the host stops is logged instead.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(SweepLimits.Interval, time);
            do
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            SweeperStopped(logger, failure);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed job — a provider timeout surfaced as a cancellation included — must not stop the host or the other jobs: it is logged and retried an hour later. Only the host's own stop ends it.")]
    private async Task<int> JobAsync(ISweepJob job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deleted = 0;
        try
        {
            for (var batch = 0; batch < SweepLimits.MaxBatches; batch++)
            {
                var swept = await job.SweepAsync(now, SweepLimits.BatchSize, cancellationToken).ConfigureAwait(false);
                deleted += swept;
                if (swept < SweepLimits.BatchSize)
                {
                    break;
                }
            }
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            JobFailed(logger, job.Name, failure);
        }

        if (deleted > 0)
        {
            Swept(logger, deleted, job.Name);
        }

        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The framework sweep forgot {Count} rows of {Job}.")]
    private static partial void Swept(ILogger logger, int count, string job);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The framework sweep found nothing to forget.")]
    private static partial void NothingSwept(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The framework sweep of {Job} failed; it runs again in an hour.")]
    private static partial void JobFailed(ILogger logger, string job, Exception failure);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The framework sweep failed while the host was stopping; the next start sweeps again.")]
    private static partial void SweeperStopped(ILogger logger, Exception failure);
}

/// <summary>Registers the one framework sweeper; every package that owns a swept table calls it beside its jobs.</summary>
internal static class SweepingSetup
{
    /// <summary>Adds <see cref="FrameworkSweeper"/> once, however many packages ask.</summary>
    /// <param name="services">The host's services.</param>
    internal static IServiceCollection AddAlvoFrameworkSweeper(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FrameworkSweeper>(provider => new FrameworkSweeper(
            provider,
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<ILogger<FrameworkSweeper>>())));
        return services;
    }
}
