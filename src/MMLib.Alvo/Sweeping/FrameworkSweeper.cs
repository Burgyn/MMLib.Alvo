using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Sweeping;

/// <summary>
/// The one framework sweeper (#527): once an hour it runs every registered <see cref="ISweepJob"/>, each in batches of
/// <see cref="SweepLimits.BatchSize"/> while a batch comes back full, at most <see cref="SweepLimits.MaxBatches"/> times.
/// It began as the ledger sweep (spec 2026-10-14 §4.9, Ruling 41), whose two ledgers are now its first jobs.
/// </summary>
/// <remarks>
/// <para>
/// Never fatal: a job that fails — resolving it included, a provider timeout surfaced as a cancellation, a deadlock that
/// survived its one retry — is logged and retried next tick, and the other jobs still run. Jobs are registered as
/// <see cref="ISweepJobRegistration"/>s and resolved one at a time inside each job's own guard, so a job whose
/// dependencies cannot be built stops nobody else. Only the host's own stop ends a sweep.
/// </para>
/// <para>
/// <b>One retry on a deadlock</b> (hardening design §5.3): two replicas deleting overlapping batches can deadlock on
/// PostgreSQL (<c>40P01</c>); the loser's batch is retried once, logged at Debug, since the deletes are idempotent.
/// </para>
/// </remarks>
internal sealed partial class FrameworkSweeper(IServiceProvider services, TimeProvider time, ILogger<FrameworkSweeper> logger) : BackgroundService
{
    private const string DeadlockDetected = "40P01";

    /// <summary>Runs every job once; answers how many rows they deleted together.</summary>
    /// <param name="cancellationToken">The host's stopping token.</param>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var total = 0;
        foreach (var registration in services.GetServices<ISweepJobRegistration>())
        {
            total += await JobAsync(registration, now, cancellationToken).ConfigureAwait(false);
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed job — resolving it, a provider timeout surfaced as a cancellation — must not stop the host or the other jobs: it is logged and retried an hour later. Only the host's own stop ends it.")]
    private async Task<int> JobAsync(ISweepJobRegistration registration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deleted = 0;
        var name = registration.Name;
        try
        {
            var job = registration.Resolve(services);
            name = job.Name;
            for (var batch = 0; batch < SweepLimits.MaxBatches; batch++)
            {
                var swept = await BatchAsync(job, now, cancellationToken).ConfigureAwait(false);
                deleted += swept;
                if (swept < SweepLimits.BatchSize)
                {
                    break;
                }
            }
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            JobFailed(logger, name, failure);
        }

        if (deleted > 0)
        {
            Swept(logger, deleted, name);
        }

        return deleted;
    }

    /// <summary>One batch, retried once when the database broke a deadlock with another replica's sweep.</summary>
    private async Task<int> BatchAsync(ISweepJob job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await job.SweepAsync(now, SweepLimits.BatchSize, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException failure) when (failure.SqlState == DeadlockDetected)
        {
            DeadlockRetried(logger, job.Name);
            return await job.SweepAsync(now, SweepLimits.BatchSize, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The framework sweep forgot {Count} rows of {Job}.")]
    private static partial void Swept(ILogger logger, int count, string job);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The framework sweep found nothing to forget.")]
    private static partial void NothingSwept(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A batch of the framework sweep of {Job} lost a deadlock to another sweep; retrying it once.")]
    private static partial void DeadlockRetried(ILogger logger, string job);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The framework sweep of {Job} failed; it runs again in an hour.")]
    private static partial void JobFailed(ILogger logger, string job, Exception failure);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The framework sweep failed while the host was stopping; the next start sweeps again.")]
    private static partial void SweeperStopped(ILogger logger, Exception failure);
}

/// <summary>
/// One job's registration with the framework sweeper: cheap to build, so enumerating every registration never fails, and
/// resolving the job itself happens inside that job's own guard.
/// </summary>
internal interface ISweepJobRegistration
{
    /// <summary>Gets the job's name, for the log line of a job that could not even be resolved.</summary>
    string Name { get; }

    /// <summary>Resolves the job.</summary>
    /// <param name="services">The host's services.</param>
    ISweepJob Resolve(IServiceProvider services);
}

/// <summary>The registration of <typeparamref name="TJob"/>, resolved from the container as a singleton.</summary>
/// <typeparam name="TJob">The job.</typeparam>
internal sealed class SweepJobRegistration<TJob> : ISweepJobRegistration
    where TJob : class, ISweepJob
{
    /// <inheritdoc/>
    public string Name => typeof(TJob).Name;

    /// <inheritdoc/>
    public ISweepJob Resolve(IServiceProvider services) => services.GetRequiredService<TJob>();
}

/// <summary>Registers the one framework sweeper and its jobs; every package that owns a swept table calls it.</summary>
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

    /// <summary>Adds <typeparamref name="TJob"/> to the framework sweep, once, and the sweeper with it.</summary>
    /// <typeparam name="TJob">The job.</typeparam>
    /// <param name="services">The host's services.</param>
    internal static IServiceCollection AddAlvoSweepJob<TJob>(this IServiceCollection services)
        where TJob : class, ISweepJob
    {
        services.TryAddSingleton<TJob>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISweepJobRegistration, SweepJobRegistration<TJob>>());
        return services.AddAlvoFrameworkSweeper();
    }
}
