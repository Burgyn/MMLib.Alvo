namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The two facts an idempotent write's own bookkeeping rests on: the table its event is written to exists
/// before the transaction opens, and a record that names no row is raised rather than read past.
/// </summary>
public class EfAlvoDataReplayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// An idempotent update ensures the outbox table itself, and does not inherit it from some earlier write.
    /// </summary>
    /// <remarks>
    /// The memo behind that ensure is per-process, so on every path but the first one to run it the call looks
    /// redundant — and is not: this world's row is seeded through EF directly, so the idempotent update is the
    /// first write the port performs, exactly as it is in a host whose first request is a retryable
    /// <c>PATCH</c>. Without the ensure the event's own insert fails inside the write transaction, where the
    /// contended-write loop retries it ten times and surfaces it as an unattributable failure.
    /// </remarks>
    [Fact]
    public async Task An_idempotent_update_ensures_the_outbox_table_it_is_about_to_write_to()
    {
        await using var world = await StartAsync();
        var id = await world.SeedAsync("seed");

        var updated = await world.Data.UpdateAsync(
            WritePathFixture.Entity, id, WritePathFixture.Payload("changed"), world.Caller, null, Token(), Ct);

        updated["title"].ShouldBe("changed");
        (await world.ScalarAsync("SELECT COUNT(*) FROM alvo_outbox")).ShouldBe(1L);
    }

    /// <summary>
    /// A record naming no row at all is an invariant violation of this port, raised loudly — never answered as
    /// a miss and never read past into an index that does not exist.
    /// </summary>
    /// <remarks>
    /// Reachable only by writing a record this port did not write, which is the case the refusal is about: the
    /// caller has already been told their create succeeded, so answering the replay with anything other than a
    /// raised invariant either re-executes the write or hands back a row id nothing ever stored.
    /// </remarks>
    [Fact]
    public async Task A_replay_whose_record_names_no_row_is_raised_rather_than_indexed()
    {
        await using var world = await StartAsync();
        var token = Token();
        await world.Data.CreateAsync(
            WritePathFixture.Entity, WritePathFixture.CreatePayload("recorded"), world.Caller, token, Ct);
        await world.ExecuteAsync("UPDATE alvo_idempotency SET row_id = '[]'");

        await Should.ThrowAsync<InvalidOperationException>(() => world.Data.CreateAsync(
            WritePathFixture.Entity, WritePathFixture.CreatePayload("recorded"), world.Caller, token, Ct));
    }

    /// <summary>
    /// A record the <b>management</b> path wrote under this caller's key is refused as the conflict it is,
    /// rather than crashing the reader that cannot interpret its <c>row_id</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>{prefix}_idempotency</c> is shared: <c>EfCoreManagementIdempotencyStore</c> files a descriptor
    /// apply under the same <c>(idempotency_key, scope)</c> primary key and the same
    /// <see cref="AlvoIdempotency.IdentityOf"/> scope, with the <b>revision</b> in <c>row_id</c> — the
    /// literal <c>"2"</c> this fact writes. One caller spending one key on a create and on an apply is
    /// therefore a key reused for a different request, which is a 409 and nothing else.
    /// </para>
    /// <para>
    /// <b>It was a <see cref="FormatException"/>.</b> The reader decoded <c>row_id</c> into row ids
    /// <em>before</em> the fingerprint was compared, so <c>Guid.Parse("2")</c> threw inside the write
    /// transaction — where, per <c>IdempotencyTable.Decode</c>'s own remarks, the contended-write loop
    /// retries it ten times and surfaces it as an unattributable 5xx. Self-inflicted rather than an
    /// isolation failure, because the scope carries the acting user; wrong either way, because the
    /// management store's own contract promises this caller is <em>told</em>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_record_whose_row_id_is_not_a_row_id_is_the_conflict_it_is_rather_than_a_crash()
    {
        await using var world = await StartAsync();
        var token = Token();
        await world.Data.CreateAsync(
            WritePathFixture.Entity, WritePathFixture.CreatePayload("recorded"), world.Caller, token, Ct);
        await world.ExecuteAsync(
            "UPDATE alvo_idempotency SET row_id = '2', fingerprint = 'a-descriptor-apply'");

        await Should.ThrowAsync<AlvoIdempotencyConflictException>(() => world.Data.CreateAsync(
            WritePathFixture.Entity, WritePathFixture.CreatePayload("recorded"), world.Caller, token, Ct));
    }

    private static AlvoIdempotency Token() => new(Guid.NewGuid().ToString(), "fingerprint");

    private static Task<WritePathWorld> StartAsync() =>
        WritePathWorld.StartAsync(WritePathFixture.Descriptor(), WritePathFixture.Schema());
}
