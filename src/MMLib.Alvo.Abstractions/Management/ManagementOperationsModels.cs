namespace MMLib.Alvo.Management;

/// <summary>One handler delivery, as an operator reads it: where it is and how its attempts went — never its payload.</summary>
public sealed record ManagementDelivery
{
    /// <summary>Gets the delivery's id; ids order by the time the delivery was fanned out.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the event it delivers.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Gets the handler that runs it.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets its lane: the record or key its events are ordered by.</summary>
    public required string PartitionKey { get; init; }

    /// <summary>Gets the event's tenant, or <see langword="null"/>.</summary>
    public string? TenantId { get; init; }

    /// <summary>Gets where it is: <c>pending</c>, <c>running</c>, <c>succeeded</c>, <c>retrying</c>, <c>dead</c> or <c>cancelled</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Gets how many times it was attempted since it was last put back to pending.</summary>
    public required int Attempts { get; init; }

    /// <summary>Gets the earliest instant it runs.</summary>
    public required DateTimeOffset NotBefore { get; init; }

    /// <summary>Gets when it ended, or <see langword="null"/>.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Gets the type name or fixed reason of its last failure — never an exception message.</summary>
    public string? LastErrorType { get; init; }

    /// <summary>Gets its attempts, oldest first; filled only when one delivery is read.</summary>
    public IReadOnlyList<ManagementDeliveryAttempt> History { get; init; } = [];
}

/// <summary>One attempt of a delivery.</summary>
public sealed record ManagementDeliveryAttempt
{
    /// <summary>Gets its number in the delivery's whole history, from 1.</summary>
    public required int Attempt { get; init; }

    /// <summary>Gets when it started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Gets how long it ran.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Gets how it ended: <c>succeeded</c>, <c>failed</c> or <c>dead</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Gets the type name or fixed reason it failed with, or <see langword="null"/>.</summary>
    public string? ErrorType { get; init; }
}

/// <summary>Which deliveries a page lists.</summary>
public sealed record ManagementDeliveryQuery
{
    /// <summary>Gets the one handler to list, or <see langword="null"/> for all.</summary>
    public string? Subscriber { get; init; }

    /// <summary>Gets the one status to list, or <see langword="null"/> for all.</summary>
    public string? Status { get; init; }

    /// <summary>Gets the earliest event time included, or <see langword="null"/>.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Gets the event time the window ends before, or <see langword="null"/>.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Gets the delivery id the previous page ended with — its <see cref="ManagementDeliveryPage.Next"/> — or <see langword="null"/>.</summary>
    public Guid? After { get; init; }

    /// <summary>Gets the page size, from 1 to 200; 50 unless set.</summary>
    public int Limit { get; init; } = 50;
}

/// <summary>One page of deliveries, newest first.</summary>
public sealed record ManagementDeliveryPage
{
    /// <summary>Gets the deliveries.</summary>
    public required IReadOnlyList<ManagementDelivery> Items { get; init; }

    /// <summary>Gets the cursor of the next page, or <see langword="null"/> when this page was the last.</summary>
    public Guid? Next { get; init; }
}

/// <summary>Which finished deliveries of one handler to run again.</summary>
public sealed record ManagementReplayRequest
{
    /// <summary>Gets the handler whose deliveries are replayed.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets which finished deliveries: <c>dead</c> (unless set), <c>succeeded</c> or <c>cancelled</c>.</summary>
    public string Status { get; init; } = "dead";

    /// <summary>Gets the earliest event time included, or <see langword="null"/>.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Gets the event time the window ends before, or <see langword="null"/>.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Gets whether to count and list without changing anything.</summary>
    public bool DryRun { get; init; }

    /// <summary>Gets why, in the operator's words — kept in the audit; at most 500 characters.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets the caller's idempotency key for a real replay, or <see langword="null"/>; refused on a dry run.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>What a replay selected — counted on a dry run, reset on a real one.</summary>
public sealed record ManagementReplayPlan
{
    /// <summary>Gets whether nothing was changed.</summary>
    public required bool DryRun { get; init; }

    /// <summary>Gets how many deliveries were selected (a dry run) or reset (a real run).</summary>
    public required int Count { get; init; }

    /// <summary>Gets the first 20 of them, in the order they run.</summary>
    public required IReadOnlyList<Guid> Ids { get; init; }

    /// <summary>Gets whether this answers an earlier request made under the same idempotency key, resetting nothing now.</summary>
    public bool Replayed { get; init; }
}

/// <summary>How this instance's extensions ran over the last day.</summary>
public sealed record ManagementExtensionStats
{
    /// <summary>Gets what the numbers cover: <c>this-instance</c> — this process, not the deployment.</summary>
    public required string Scope { get; init; }

    /// <summary>Gets the window the numbers cover: 24 hours, in hourly steps.</summary>
    public required TimeSpan Window { get; init; }

    /// <summary>Gets the earliest instant counted — later than a day ago when this process started since.</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>Gets one entry per registered extension, in catalog order.</summary>
    public required IReadOnlyList<ManagementExtensionStat> Items { get; init; }
}

/// <summary>How one extension ran.</summary>
public sealed record ManagementExtensionStat
{
    /// <summary>Gets the extension id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets how many times it ran.</summary>
    public long Invocations { get; init; }

    /// <summary>Gets how many runs failed.</summary>
    public long Failures { get; init; }

    /// <summary>Gets how many runs refused a write; an event handler refuses none.</summary>
    public long Rejections { get; init; }

    /// <summary>Gets the median run time, within about a fifth, or <see langword="null"/> before any run.</summary>
    public TimeSpan? P50 { get; init; }

    /// <summary>Gets the 95th-percentile run time, within about a fifth, or <see langword="null"/> before any run.</summary>
    public TimeSpan? P95 { get; init; }
}
