namespace MMLib.Alvo.Scheduling;

/// <summary>
/// Host code run once, registered with <c>AddDataMigration</c>: when the applied descriptor first reaches the revision it waits
/// for — once per tenant on a scoped schema — as a delivery that is retried, recorded and dead-lettered. Neither a restart, a
/// rollback nor a re-apply runs it again.
/// </summary>
public interface IAlvoDataMigration
{
    /// <summary>Runs the migration for one tenant, or for the whole project on a schema with no scoped entity.</summary>
    /// <param name="context">The revision it started at, the tenant, the attempt, and the data session to work through.</param>
    /// <param name="cancellationToken">Cancelled when the run's timeout passes or the host stops.</param>
    /// <returns>A task that completes when the run is done; a thrown exception fails the attempt.</returns>
    Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken);
}
