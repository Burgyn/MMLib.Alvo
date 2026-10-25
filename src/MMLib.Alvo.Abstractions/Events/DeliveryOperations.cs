namespace MMLib.Alvo.Events;

/// <summary>One delivery as the store holds it, for an operator to read.</summary>
public sealed record StoredDelivery
{
    /// <summary>Gets the delivery's id; ids order by the time the delivery was fanned out.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the outbox entry it delivers.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Gets the registration that runs it.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets its lane.</summary>
    public required string PartitionKey { get; init; }

    /// <summary>Gets the event's tenant, or <see langword="null"/>.</summary>
    public string? TenantId { get; init; }

    /// <summary>Gets what runs it: <c>handler</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets where it is: <c>pending</c>, <c>running</c>, <c>succeeded</c>, <c>retrying</c>, <c>dead</c> or <c>cancelled</c>.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>Gets how many times it was claimed since it was last put back to pending.</summary>
    public required int Attempts { get; init; }

    /// <summary>Gets the earliest instant it may be claimed.</summary>
    public required DateTimeOffset NotBefore { get; init; }

    /// <summary>Gets when it ended — succeeded, dead or cancelled — or <see langword="null"/> while it has not.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Gets the type name or fixed reason of its last failure — never an exception message.</summary>
    public string? LastErrorType { get; init; }

    /// <summary>Gets the stored envelope; filled only by <see cref="IDeliveryStore.FindAsync"/>, and <see langword="null"/> once the outbox no longer holds it.</summary>
    public string? Payload { get; init; }
}

/// <summary>One recorded attempt of a delivery.</summary>
public sealed record StoredDeliveryAttempt
{
    /// <summary>Gets its number in the delivery's whole history, from 1 — it keeps counting across a redeliver.</summary>
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

/// <summary>Which deliveries an operator's read or reset is about.</summary>
public sealed record DeliveryFilter
{
    /// <summary>Gets the one subscriber to read, or <see langword="null"/> for all.</summary>
    public string? Subscriber { get; init; }

    /// <summary>Gets the one lane — partition key — to read, or <see langword="null"/> for all.</summary>
    public string? PartitionKey { get; init; }

    /// <summary>Gets the one status to read, or <see langword="null"/> for all.</summary>
    public string? Status { get; init; }

    /// <summary>Gets the smallest event id included, compared in text order as lanes are, or <see langword="null"/>.</summary>
    public Guid? FromEventId { get; init; }

    /// <summary>Gets the event id the window ends before — excluded — or <see langword="null"/>.</summary>
    public Guid? ToEventId { get; init; }

    /// <summary>Gets the delivery id a page continues after: only strictly older deliveries are listed; ignored by a selection.</summary>
    public Guid? After { get; init; }

    /// <summary>
    /// Gets a page's size, or a selection's and a replay's sample size; a store refuses zero or less, and a page or sample of
    /// more than 1 000, with <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public required int Limit { get; init; }

    /// <summary>
    /// Gets whether to keep only what a replay may reset: a finished delivery — succeeded, dead or cancelled — that is not
    /// dead with <c>chain-depth-exceeded</c>, the record of a loop the chain-depth limit stopped.
    /// </summary>
    public bool Replayable { get; init; }
}

/// <summary>What a filter selects, counted exactly, with the first ids it would reset.</summary>
public sealed record DeliverySelection
{
    /// <summary>Gets how many deliveries match.</summary>
    public required int Count { get; init; }

    /// <summary>Gets the first <see cref="DeliveryFilter.Limit"/> matching ids, in event order — the order they would run.</summary>
    public required IReadOnlyList<Guid> Sample { get; init; }

    /// <summary>
    /// Gets how many of them have a delivery of the same subscriber and partition key running now — deliveries a reset
    /// would put in flight beside it.
    /// </summary>
    public int Busy { get; init; }
}
