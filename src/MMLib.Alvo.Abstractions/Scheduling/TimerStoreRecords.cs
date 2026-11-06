using MMLib.Alvo.Events;

namespace MMLib.Alvo.Scheduling;

/// <summary>One timer a claim leased, as <see cref="ITimerStore.ClaimTimersAsync"/> returns it.</summary>
/// <remarks>
/// Init-only properties rather than a positional constructor, here and on every record of this port: a provider builds
/// and reads these, and a member added later must not change a constructor or a <c>Deconstruct</c> it compiled against.
/// </remarks>
public sealed record ClaimedTimer
{
    /// <summary>Gets the tenant id text, or <c>""</c> for none.</summary>
    public required string Scope { get; init; }

    /// <summary>Gets the timer's key, unique within its scope.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the generation the timer was armed under; the fire names it.</summary>
    public required Guid Generation { get; init; }

    /// <summary>Gets when the timer was due, in UTC.</summary>
    public required DateTimeOffset DueAt { get; init; }

    /// <summary>Gets the custom event type the timer fires.</summary>
    public required string MessageType { get; init; }

    /// <summary>Gets the message as <see cref="TimerEntry.Payload"/> stored it.</summary>
    public required string Payload { get; init; }

    /// <summary>Gets the provenance as <see cref="TimerEntry.Provenance"/> stored it.</summary>
    public required string Provenance { get; init; }
}

/// <summary>How many timers are armed, and how many of them are due.</summary>
public sealed record TimerCounts
{
    /// <summary>Gets every armed timer, due or not.</summary>
    public required int Pending { get; init; }

    /// <summary>Gets the armed timers whose due time has come.</summary>
    public required int Due { get; init; }
}

/// <summary>One schedule this deployment registers, as <see cref="ITimerStore.SyncSchedulesAsync"/> stores it.</summary>
public sealed record ScheduleDefinition
{
    /// <summary>Gets the schedule's id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets its five-field cron expression.</summary>
    public required string Cron { get; init; }

    /// <summary>Gets the IANA time zone the expression is read in.</summary>
    public required string TimeZone { get; init; }

    /// <summary>Gets its next occurrence, in UTC — kept for an unchanged schedule, stored for a new or changed one.</summary>
    public required DateTimeOffset NextRunAt { get; init; }
}

/// <summary>One schedule occurrence a claim leased.</summary>
public sealed record ClaimedSchedule
{
    /// <summary>Gets the schedule's id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the occurrence the claim is for, in UTC; its advance names it.</summary>
    public required DateTimeOffset ScheduledFor { get; init; }
}

/// <summary>One claimed occurrence's run: who ran it, where the schedule moves next, and the events the run appends.</summary>
public sealed record ScheduleAdvance
{
    /// <summary>Gets the schedule's id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the claimant that leased the occurrence.</summary>
    public required string Claimant { get; init; }

    /// <summary>Gets the occurrence being run, as the claim returned it.</summary>
    public required DateTimeOffset ScheduledFor { get; init; }

    /// <summary>Gets the schedule's next occurrence, in UTC.</summary>
    public required DateTimeOffset NextRunAt { get; init; }

    /// <summary>Gets the run's events — one per tenant for a schedule that runs in each.</summary>
    public required IReadOnlyList<AlvoFrameworkEnvelope> Events { get; init; }
}

/// <summary>One stored schedule, as <see cref="ITimerStore.SchedulesAsync"/> reads it.</summary>
public sealed record StoredSchedule
{
    /// <summary>Gets the schedule's id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets its five-field cron expression.</summary>
    public required string Cron { get; init; }

    /// <summary>Gets the IANA time zone the expression is read in.</summary>
    public required string TimeZone { get; init; }

    /// <summary>Gets its next occurrence, in UTC.</summary>
    public required DateTimeOffset NextRunAt { get; init; }

    /// <summary>Gets the last occurrence that ran, in UTC, or <see langword="null"/> when none has.</summary>
    public DateTimeOffset? LastRunAt { get; init; }
}
