using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The delivery store in memory, held to <see cref="Testing.Events.DeliveryStoreContractTests"/> by
/// <see cref="FakeDeliveryStoreTests"/>, so the core's dispatcher facts run against a store with the real semantics.
/// </summary>
internal sealed class FakeDeliveryStore(TimeProvider time) : IDeliveryStore
{
    private static readonly string[] _unfinished = ["pending", "running", "retrying"];
    private static readonly string[] _blocking = ["pending", "running", "retrying", "dead"];

    private readonly Lock _gate = new();
    private readonly List<Row> _rows = [];
    private readonly Dictionary<Guid, string> _payloads = [];
    private readonly Dictionary<Guid, (string Key, bool Dispatched, int Attempts, DateTimeOffset? ClaimedAt)> _outbox = [];
    private readonly List<(Guid Delivery, int Attempt, string Outcome)> _attempts = [];

    internal int FanOutFailures { get; set; }

    internal IReadOnlyList<Row> Rows
    {
        get
        {
            lock (_gate)
            {
                return [.. _rows.Select(row => row with { })];
            }
        }
    }

    internal IReadOnlyList<(Guid Delivery, int Attempt, string Outcome)> Attempts
    {
        get
        {
            lock (_gate)
            {
                return [.. _attempts];
            }
        }
    }

    internal void Publish(AlvoEvent @event, bool dispatched = true, int attempts = 0, DateTimeOffset? claimedAt = null)
    {
        lock (_gate)
        {
            _payloads[@event.Id] = AlvoEventJson.Write(@event);
            _outbox[@event.Id] = (@event.PartitionKey, dispatched, attempts, claimedAt);
        }
    }

    internal void MarkDispatched(Guid eventId)
    {
        lock (_gate)
        {
            _outbox[eventId] = _outbox[eventId] with { Dispatched = true };
        }
    }

    public Task EnsureAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest> requests, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (FanOutFailures > 0)
            {
                FanOutFailures--;
                throw new InvalidOperationException("The fake delivery store refused this fan-out.");
            }

            var fresh = requests.DistinctBy(request => (request.EventId, request.Subscriber))
                .Where(request => !_rows.Exists(row => row.EventId == request.EventId && row.Subscriber == request.Subscriber))
                .ToList();
            _rows.AddRange(fresh.Select(NewRow));

            return Task.FromResult(fresh.Count);
        }
    }

    public Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(DeliveryClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(claim.BatchSize);

        lock (_gate)
        {
            var now = time.GetUtcNow();
            var heads = _rows.Where(row => Claimable(row, claim, now))
                .OrderBy(row => row.EventId.ToString(), StringComparer.Ordinal)
                .ThenBy(row => row.Subscriber, StringComparer.Ordinal)
                .Take(claim.BatchSize)
                .ToList();

            return Task.FromResult<IReadOnlyList<ClaimedDelivery>>([.. heads.Select(row => Claim(row, claim, now))]);
        }
    }

    public Task<bool> CompleteAsync(DeliveryAttempt attempt, CancellationToken cancellationToken = default) =>
        Task.FromResult(Answer(attempt, Succeed, "succeeded"));

    public Task<bool> FailAsync(DeliveryAttempt attempt, DeliveryFailure failure, CancellationToken cancellationToken = default)
    {
        var dead = failure.RetryAfter is null || attempt.Delivery.Attempts >= failure.MaxAttempts;

        return Task.FromResult(Answer(attempt, row => Fail(row, failure, dead), dead ? "dead" : "failed"));
    }

    public Task<IReadOnlyList<string>> PendingSubscribersAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>(
                [.. _rows.Where(row => _unfinished.Contains(row.Status)).Select(row => row.Subscriber).Distinct().Order(StringComparer.Ordinal)]);
        }
    }

    public Task<int> DeadLetterPendingAsync(string subscriber, string reason, CancellationToken cancellationToken = default) =>
        Task.FromResult(End(subscriber, "dead", reason));

    public Task<int> CancelPendingAsync(string subscriber, CancellationToken cancellationToken = default) =>
        Task.FromResult(End(subscriber, "cancelled", "retired"));

    private Row NewRow(DeliveryRequest request) => new()
    {
        Id = AlvoEventId.Create(time.GetUtcNow()),
        EventId = request.EventId,
        Subscriber = request.Subscriber,
        PartitionKey = request.PartitionKey,
        TenantId = request.TenantId,
        Status = request.DeadReason is null ? "pending" : "dead",
        LastErrorType = request.DeadReason,
        NotBefore = time.GetUtcNow(),
        CompletedAt = request.DeadReason is null ? null : time.GetUtcNow(),
    };

    private bool Claimable(Row row, DeliveryClaim claim, DateTimeOffset now) =>
        (claim.Ordered.Contains(row.Subscriber) || claim.Unordered.Contains(row.Subscriber))
        && Due(row, now)
        && (claim.Unordered.Contains(row.Subscriber) || !Blocked(row, claim, now));

    private static bool Due(Row row, DateTimeOffset now) =>
        (row.Status is "pending" or "retrying" && row.NotBefore <= now)
        || (row.Status == "running" && row.LeaseUntil < now);

    private bool Blocked(Row row, DeliveryClaim claim, DateTimeOffset now) =>
        _rows.Exists(earlier =>
            earlier.Subscriber == row.Subscriber
            && earlier.PartitionKey == row.PartitionKey
            && Before(earlier.EventId, row.EventId)
            && _blocking.Contains(earlier.Status)
            && earlier.LastErrorType != DeliveryRequest.UnboundReason)
        || _outbox.Any(entry =>
            entry.Value.Key == row.PartitionKey
            && Before(entry.Key, row.EventId)
            && !entry.Value.Dispatched
            && (entry.Value.Attempts < claim.OutboxMaxAttempts || entry.Value.ClaimedAt >= now - claim.OutboxClaimLease));

    private static bool Before(Guid earlier, Guid later) => string.CompareOrdinal(earlier.ToString(), later.ToString()) < 0;

    private ClaimedDelivery Claim(Row row, DeliveryClaim claim, DateTimeOffset now)
    {
        row.Status = "running";
        row.ClaimedBy = claim.Claimant;
        row.Attempts++;
        row.LeaseUntil = now + claim.Lease;

        return new ClaimedDelivery
        {
            Id = row.Id,
            EventId = row.EventId,
            Subscriber = row.Subscriber,
            PartitionKey = row.PartitionKey,
            Attempts = row.Attempts,
            Payload = _payloads.GetValueOrDefault(row.EventId),
        };
    }

    private bool Answer(DeliveryAttempt attempt, Action<Row> change, string outcome)
    {
        lock (_gate)
        {
            var row = _rows.SingleOrDefault(candidate => candidate.Id == attempt.Delivery.Id
                && candidate.ClaimedBy == attempt.Claimant
                && candidate.Attempts == attempt.Delivery.Attempts
                && candidate.Status == "running");
            if (row is null)
            {
                return false;
            }

            change(row);
            row.LeaseUntil = null;
            _attempts.Add((row.Id, row.Attempts, outcome));

            return true;
        }
    }

    private void Succeed(Row row)
    {
        row.Status = "succeeded";
        row.LastErrorType = null;
        row.CompletedAt = time.GetUtcNow();
    }

    private void Fail(Row row, DeliveryFailure failure, bool dead)
    {
        row.Status = dead ? "dead" : "retrying";
        row.LastErrorType = failure.ErrorType;
        row.NotBefore = time.GetUtcNow() + (failure.RetryAfter ?? TimeSpan.Zero);
        row.CompletedAt = dead ? time.GetUtcNow() : null;
    }

    private int End(string subscriber, string status, string reason)
    {
        lock (_gate)
        {
            var ended = _rows.Where(row => row.Subscriber == subscriber && _unfinished.Contains(row.Status)).ToList();
            foreach (var row in ended)
            {
                row.Status = status;
                row.LastErrorType = reason;
                row.LeaseUntil = null;
                row.CompletedAt = time.GetUtcNow();
            }

            return ended.Count;
        }
    }

    internal sealed record Row
    {
        public Guid Id { get; init; }

        public Guid EventId { get; init; }

        public string Subscriber { get; init; } = string.Empty;

        public string PartitionKey { get; init; } = string.Empty;

        public string? TenantId { get; init; }

        public string Status { get; set; } = "pending";

        public int Attempts { get; set; }

        public DateTimeOffset NotBefore { get; set; }

        public DateTimeOffset? LeaseUntil { get; set; }

        public string? ClaimedBy { get; set; }

        public string? LastErrorType { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }
    }
}

/// <summary>A clock a fact moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    internal void Advance(TimeSpan duration) => _now += duration;
}
