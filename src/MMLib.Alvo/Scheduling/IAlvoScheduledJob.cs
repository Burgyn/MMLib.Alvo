namespace MMLib.Alvo.Scheduling;

/// <summary>
/// Host code run on a cron schedule, registered with <c>AddSchedule</c>: built per run from the run's scope, and run as a
/// delivery — retried, recorded in the delivery history, dead-lettered, and runnable on demand.
/// </summary>
public interface IAlvoScheduledJob
{
    /// <summary>Runs one occurrence.</summary>
    /// <param name="context">Which occurrence this is, in which tenant, and the data session to work through.</param>
    /// <param name="cancellationToken">Cancelled when the run's timeout passes or the host stops.</param>
    /// <returns>A task that completes when the run is done; a thrown exception fails the attempt.</returns>
    Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken);
}
