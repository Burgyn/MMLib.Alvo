using MMLib.Alvo.Events;

using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The capped half of the claim: a capped subscriber's delivery is claimed only together with a free slot of its key, in the
/// claim's transaction (<see cref="ConcurrencySlotTable"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lock order: every slot before any delivery.</b> The claim first takes slots — keys ascending, slots ascending — for at
/// most the room its batch left, and only then claims the deliveries it took slots for, ids ascending, giving back the slot
/// of one it could not claim. An answer frees its slot before it updates its delivery. So no two writers wait on each other
/// in opposite orders over the same rows, and claimants racing one key cannot deadlock.
/// </para>
/// <para>
/// <b>The candidate read is a read in a writing transaction.</b> On SQLite the transaction is begun by a writer that holds the
/// database's write gate, so nothing can write between the read and the claim's own writes; on PostgreSQL the read is only a
/// hint, and the slot rows and the claim's repeated outer condition are the enforcement.
/// </para>
/// </remarks>
internal sealed partial class EfCoreDeliveryStore
{
    private const int CandidatesPerRoom = 4;
    private const int MaxCandidates = 200;

    private static Dictionary<string, DeliveryLimit> Caps(DeliveryClaim claim) =>
        claim.Limits
            .Where(limit => claim.Ordered.Contains(limit.Subscriber) || claim.Unordered.Contains(limit.Subscriber))
            .DistinctBy(limit => limit.Subscriber)
            .ToDictionary(limit => limit.Subscriber, StringComparer.Ordinal);

    private static (List<string> Ordered, List<string> Unordered) Uncapped(DeliveryClaim claim, Dictionary<string, DeliveryLimit> caps) =>
        ([.. claim.Ordered.Where(subscriber => !caps.ContainsKey(subscriber))], [.. claim.Unordered.Where(subscriber => !caps.ContainsKey(subscriber))]);

    /// <summary>Refuses a cap no slot set can represent before anything is read or written.</summary>
    private static void EnsureLimits(DeliveryClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim.Limits);
        foreach (var limit in claim.Limits)
        {
            ArgumentNullException.ThrowIfNull(limit);
            if (limit.Key is not (ConcurrencySlotTable.TenantKey or ConcurrencySlotTable.RecordKey))
            {
                throw new ArgumentException($"'{limit.Key}' is not a concurrency key; a cap counts per \"tenant\" or \"record\".", nameof(claim));
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(limit.Limit, 1, nameof(claim));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(limit.Limit, ConcurrencySlotTable.MaxLimit, nameof(claim));
        }
    }

    /// <summary>Refuses a request whose subscriber or tenant — the slot and lane keys' inner parts — holds a control character.</summary>
    private static void EnsureKeyParts(IReadOnlyList<DeliveryRequest> requests)
    {
        foreach (var request in requests)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Subscriber.Any(char.IsControl) || (request.TenantId?.Any(char.IsControl) ?? false))
            {
                throw new ArgumentException(
                    $"The delivery of event {request.EventId} names a subscriber or tenant with a control character, which no delivery key may hold.",
                    nameof(requests));
            }
        }
    }

    private async Task<List<ClaimedDelivery>> ClaimCappedAsync(
        DbConnection connection, DbTransaction transaction, DeliveryClaim claim, Dictionary<string, DeliveryLimit> caps, int room, CancellationToken ct)
    {
        var candidates = await CandidatesAsync(connection, transaction, claim, caps, Math.Min(room * CandidatesPerRoom, MaxCandidates), ct).ConfigureAwait(false);
        var taken = await TakeSlotsAsync(connection, transaction, claim, candidates, room, ct).ConfigureAwait(false);

        return await ClaimTakenAsync(connection, transaction, claim, taken, ct).ConfigureAwait(false);
    }

    private async Task<List<Candidate>> CandidatesAsync(
        DbConnection connection, DbTransaction transaction, DeliveryClaim claim, Dictionary<string, DeliveryLimit> caps, int candidates, CancellationToken ct)
    {
        var all = new List<Candidate>();
        foreach (var cap in caps.Values.OrderBy(cap => cap.Subscriber, StringComparer.Ordinal))
        {
            var ordered = claim.Ordered.Contains(cap.Subscriber);
            all.AddRange(await RelationalSqlBatch.ReadRowsAsync(
                connection,
                transaction,
                DeliveryTable.CandidatesSql(_table, _outbox, _controls, ordered, cap.Key == ConcurrencySlotTable.RecordKey),
                command => BindCandidates(command, claim, cap, candidates),
                reader => ReadCandidate(reader, cap),
                ct).ConfigureAwait(false));
        }

        return [.. all.OrderBy(candidate => candidate.SlotKey, StringComparer.Ordinal).ThenBy(candidate => candidate.EventId.ToString(), StringComparer.Ordinal)];
    }

    /// <summary>Takes one free slot for each candidate in lock order, until <paramref name="room"/> candidates hold one.</summary>
    private async Task<List<TakenSlot>> TakeSlotsAsync(
        DbConnection connection, DbTransaction transaction, DeliveryClaim claim, List<Candidate> candidates, int room, CancellationToken ct)
    {
        var taken = new List<TakenSlot>();
        var ensured = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (taken.Count == room)
            {
                break;
            }

            if (ensured.Add(candidate.SlotKey))
            {
                await ExecuteInAsync(connection, transaction, ConcurrencySlotTable.EnsureSlotsSql(_slots, candidate.Limit), command => BindSlotKey(command, candidate.SlotKey), ct)
                    .ConfigureAwait(false);
            }

            if (await TakeSlotAsync(connection, transaction, claim, candidate, ct).ConfigureAwait(false) is { } slot)
            {
                taken.Add(new TakenSlot(candidate, slot));
            }
        }

        return taken;
    }

    private async Task<int?> TakeSlotAsync(DbConnection connection, DbTransaction transaction, DeliveryClaim claim, Candidate candidate, CancellationToken ct)
    {
        for (var slot = 0; slot < candidate.Limit; slot++)
        {
            var index = slot;
            if (await ExecuteInAsync(connection, transaction, ConcurrencySlotTable.TakeSql(_slots), command => BindTake(command, claim, candidate, index), ct).ConfigureAwait(false) == 1)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>Claims each delivery a slot was taken for, ids ascending, and gives back the slot of one no longer claimable.</summary>
    private async Task<List<ClaimedDelivery>> ClaimTakenAsync(
        DbConnection connection, DbTransaction transaction, DeliveryClaim claim, List<TakenSlot> taken, CancellationToken ct)
    {
        var claimed = new List<ClaimedDelivery>();
        foreach (var held in taken.OrderBy(held => held.Candidate.Id.ToString(), StringComparer.Ordinal))
        {
            if (await ClaimOneAsync(connection, transaction, claim, held.Candidate, ct).ConfigureAwait(false) is { } won)
            {
                claimed.Add(won);
            }
            else
            {
                await ExecuteInAsync(connection, transaction, ConcurrencySlotTable.GiveBackSql(_slots), command => BindSlot(command, held), ct).ConfigureAwait(false);
            }
        }

        return claimed;
    }

    private async Task<ClaimedDelivery?> ClaimOneAsync(DbConnection connection, DbTransaction transaction, DeliveryClaim claim, Candidate candidate, CancellationToken ct)
    {
        var won = await RelationalSqlBatch.ReadRowsAsync(
            connection,
            transaction,
            DeliveryTable.ClaimOneSql(_table, _outbox, _controls, claim.Ordered.Contains(candidate.Subscriber)),
            command =>
            {
                BindClaim(command, claim, [candidate.Subscriber]);
                RelationalSqlBatch.AddParameter(command, "@id", candidate.Id.ToString());
            },
            ReadClaimed,
            ct).ConfigureAwait(false);

        return won.SingleOrDefault();
    }

    private void BindCandidates(DbCommand command, DeliveryClaim claim, DeliveryLimit cap, int candidates)
    {
        BindClaim(command, claim, [cap.Subscriber]);
        RelationalSqlBatch.AddParameter(command, "@limit", cap.Limit);
        RelationalSqlBatch.AddParameter(command, "@candidates", candidates);
    }

    private void BindTake(DbCommand command, DeliveryClaim claim, Candidate candidate, int slot)
    {
        var now = _time.GetUtcNow();
        BindSlotKey(command, candidate.SlotKey);
        RelationalSqlBatch.AddParameter(command, "@slot", slot);
        RelationalSqlBatch.AddParameter(command, "@delivery_id", candidate.Id.ToString());
        RelationalSqlBatch.AddParameter(command, "@lease_until", StoredInstant.Text(now + claim.Lease));
        RelationalSqlBatch.AddParameter(command, "@now", StoredInstant.Text(now));
    }

    private static void BindSlot(DbCommand command, TakenSlot held)
    {
        BindSlotKey(command, held.Candidate.SlotKey);
        RelationalSqlBatch.AddParameter(command, "@slot", held.Slot);
        RelationalSqlBatch.AddParameter(command, "@delivery_id", held.Candidate.Id.ToString());
    }

    private static void BindSlotKey(DbCommand command, string slotKey) => RelationalSqlBatch.AddParameter(command, "@slot_key", slotKey);

    private static Candidate ReadCandidate(DbDataReader reader, DeliveryLimit cap)
    {
        var subscriber = reader.GetString(2);
        var tenant = reader.IsDBNull(4) ? null : reader.GetString(4);

        return new Candidate(
            Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
            Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            subscriber,
            ConcurrencySlotTable.SlotKey(subscriber, cap.Key, tenant, reader.GetString(3)),
            cap.Limit);
    }

    /// <summary>A capped delivery the claim may take, with the slot key it counts under and that key's cap.</summary>
    private sealed record Candidate(Guid Id, Guid EventId, string Subscriber, string SlotKey, int Limit);

    /// <summary>A slot this claim took for a candidate.</summary>
    private sealed record TakenSlot(Candidate Candidate, int Slot);
}
