using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

namespace MMLib.Alvo.Scheduling;

/// <summary>One run of a scheduled job: which occurrence, in which tenant, which attempt, and the data session to work through.</summary>
public sealed class AlvoJobContext
{
    private AlvoJobContext(string id, DateTimeOffset scheduledFor, TenantId? tenant, IAlvoSession data, int attempt, int? revision, IAlvoSteps steps)
    {
        Steps = steps;
        Id = id;
        ScheduledFor = scheduledFor;
        Tenant = tenant;
        Data = data;
        Attempt = attempt;
        Revision = revision;
    }

    /// <summary>Gets the id the job was registered under.</summary>
    public string Id { get; }

    /// <summary>Gets the occurrence this run is for, in UTC; for a run started on demand, the moment it was started.</summary>
    public DateTimeOffset ScheduledFor { get; }

    /// <summary>Gets the tenant this run is for, or <see langword="null"/> for a run across the whole project.</summary>
    public TenantId? Tenant { get; }

    /// <summary>Gets the data session the run works through, as the system in <see cref="Tenant"/>.</summary>
    public IAlvoSession Data { get; }

    /// <summary>Gets which attempt this is; 1 for the first.</summary>
    public int Attempt { get; }

    /// <summary>Gets the schema revision a data migration started at; <see langword="null"/> for a schedule.</summary>
    public int? Revision { get; }

    /// <summary>
    /// Gets the run's steps: work split into named steps, each run until it completes once, so a retry resumes after the last
    /// completed step. A context from <see cref="Create"/> runs every step's work every time and never sleeps.
    /// </summary>
    public IAlvoSteps Steps { get; }

    /// <summary>Creates a context, so a job can be run directly in a test.</summary>
    /// <param name="id">The job's id.</param>
    /// <param name="scheduledFor">The occurrence the run is for.</param>
    /// <param name="tenant">The tenant it runs for, or <see langword="null"/>.</param>
    /// <param name="data">The data session it works through.</param>
    /// <param name="attempt">Which attempt this is; at least 1.</param>
    /// <param name="revision">The revision a data migration started at, or <see langword="null"/>.</param>
    /// <returns>The context.</returns>
    public static AlvoJobContext Create(
        string id, DateTimeOffset scheduledFor, TenantId? tenant, IAlvoSession data, int attempt = 1, int? revision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        return new(id, scheduledFor, tenant, data, attempt, revision, PassThroughSteps.Instance);
    }

    /// <summary>The context of one delivered run, carrying its delivery's steps.</summary>
    internal static AlvoJobContext ForRun(
        string id, DateTimeOffset scheduledFor, TenantId? tenant, IAlvoSession data, int attempt, int? revision, IAlvoSteps steps) =>
        new(id, scheduledFor, tenant, data, attempt, revision, steps);
}
