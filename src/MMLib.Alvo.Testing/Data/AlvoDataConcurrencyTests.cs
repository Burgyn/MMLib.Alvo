using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The write path's two concurrency channels as rules of the <b>port</b>, proved over every
/// <see cref="IAlvoData"/> implementation this suite runs against — the in-memory reference included: an
/// <see cref="AlvoPrecondition"/> that no longer matches the stored row refuses the write, an entity with no
/// version column refuses a precondition rather than ignoring it, and an <see cref="AlvoIdempotency"/> key
/// replayed with the same request returns the first row rather than creating a second one.
/// </summary>
/// <remarks>
/// <para>
/// A suite of its own rather than a section of <see cref="AlvoDataAdversarialTests"/>, on the same reasoning
/// that separated <see cref="AlvoDataPagingTests"/>: these facts are about what happens when <em>two</em>
/// writes meet, so several of them need a second write, a second caller, a second tenant or a second entity
/// before they can ask their question at all — and one of them needs two calls genuinely in flight at once.
/// The adversarial suite's shape is "one caller, one act, what may they not do"; nothing here fits it.
/// </para>
/// <para>
/// <b>Every fact is written to be able to fail for the reason its name claims</b>, which for several of them
/// takes deliberate construction:
/// </para>
/// <list type="bullet">
///   <item>
///   Every staleness fact <em>advances</em> the version with a real second write and asserts that it moved,
///   so "refused" cannot pass merely because the version never changed.
///   </item>
///   <item>
///   <see cref="The_version_a_write_returns_is_the_one_a_following_precondition_accepts"/> chains a create
///   into two updates, each precondition minted from the record the <em>previous</em> call returned. An
///   implementation comparing against its own clock instead of the stored pre-image passes on a store that
///   keeps 100-nanosecond ticks and fails on PostgreSQL, which keeps microseconds — which is exactly the
///   engine divergence this suite exists to surface.
///   </item>
///   <item>
///   <see cref="A_stale_precondition_is_refused_before_the_policy_check_reveals_anything"/> uses one stale
///   version against two rows — one visible to the caller, one not — so the two exception types are the only
///   thing that distinguishes a correct check order from an inverted one.
///   </item>
///   <item>
///   <see cref="A_replay_by_a_second_user_in_the_same_tenant_never_returns_the_first_users_row"/> is the
///   row-level authorization fact this suite was missing in its first round, and it needs <b>both</b> halves
///   of the fix to pass: a replay read under the <c>create</c> decision returns another user's row (that
///   decision has no <c>USING</c> predicate at all), and a record identity that omits the acting user lets the
///   second caller reach the first caller's record in the first place.
///   </item>
///   <item>
///   <see cref="Two_concurrent_creates_with_one_idempotency_key_produce_exactly_one_row"/> starts both calls
///   before awaiting either, so they are genuinely in flight together on any backend that awaits I/O.
///   </item>
/// </list>
/// </remarks>
public abstract class AlvoDataConcurrencyTests
{
    /// <summary>
    /// Builds a fresh <see cref="IAlvoData"/> over <paramref name="descriptor"/>/<paramref name="schema"/>,
    /// seeded out of band with <paramref name="seed"/>'s rows — the same seam
    /// <see cref="AlvoDataAdversarialTests.CreateAsync"/> defines, so an engine's subclass is the fixture it
    /// already has plus nothing.
    /// </summary>
    /// <remarks>
    /// Every fact here seeds nothing and writes its rows through the port. That is not incidental: a version
    /// is only meaningful if the framework's own audit stamp wrote it, and a row inserted out of band carries
    /// whatever instant the seeding seam chose. Per-fact isolation is still required, exactly as the
    /// adversarial suite requires it — several facts assert an exact row count over an entity with no
    /// row-scoping predicate.
    /// </remarks>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    /// <param name="seed">The initial rows to insert, keyed by entity name.</param>
    protected abstract Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed);

    /// <summary>
    /// The happy path, and the counterweight every refusal below needs: a precondition carrying the version
    /// the row actually holds is accepted, so none of the refusals can be satisfied by refusing every
    /// precondition.
    /// </summary>
    [Fact]
    public async Task An_update_whose_precondition_matches_the_stored_version_succeeds()
    {
        var world = await AuditedWorldAsync();
        var created = await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, cancellationToken: Ct);

        var updated = await world.Data.UpdateAsync(
            Orders, IdOf(created), Payload("second"), world.Caller, new AlvoPrecondition(VersionOf(created)), cancellationToken: Ct);

        updated["title"].ShouldBe("second");
    }

    /// <summary>
    /// The lost update this channel exists to prevent: a second writer already advanced the row, so the
    /// first writer's version no longer describes it and their write must not land. The stored title is
    /// asserted afterwards because an implementation that threw <em>after</em> writing would satisfy the
    /// exception assertion alone.
    /// </summary>
    [Fact]
    public async Task An_update_whose_precondition_is_stale_is_refused_and_changes_nothing()
    {
        var world = await AuditedWorldAsync();
        var created = await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, cancellationToken: Ct);
        var stale = VersionOf(created);
        var advanced = await world.Data.UpdateAsync(
            Orders, IdOf(created), Payload("second"), world.Caller, cancellationToken: Ct);
        VersionOf(advanced).ShouldNotBe(stale, "a write must advance the version, or nothing below discriminates");

        await Should.ThrowAsync<AlvoPreconditionFailedException>(() => world.Data.UpdateAsync(
            Orders, IdOf(created), Payload("third"), world.Caller, new AlvoPrecondition(stale), cancellationToken: Ct));

        var stored = await world.Data.GetAsync(Orders, IdOf(created), world.Caller, Ct);
        stored.ShouldNotBeNull();
        stored!["title"].ShouldBe("second", "the refused write must not have landed");
    }

    /// <summary>
    /// The same rule on the delete path, where the cost of getting it wrong is not an overwritten field but
    /// a row that is gone. Carries its own counterweight in the same act — the current version does delete
    /// the row — so this cannot be satisfied by refusing every delete that carries a precondition.
    /// </summary>
    [Fact]
    public async Task A_delete_whose_precondition_is_stale_is_refused_and_the_row_survives()
    {
        var world = await AuditedWorldAsync();
        var created = await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, cancellationToken: Ct);
        var stale = VersionOf(created);
        var advanced = await world.Data.UpdateAsync(
            Orders, IdOf(created), Payload("second"), world.Caller, cancellationToken: Ct);
        VersionOf(advanced).ShouldNotBe(stale, "a write must advance the version, or nothing below discriminates");

        await Should.ThrowAsync<AlvoPreconditionFailedException>(() => world.Data.DeleteAsync(
            Orders, IdOf(created), world.Caller, new AlvoPrecondition(stale), cancellationToken: Ct));
        (await world.Data.GetAsync(Orders, IdOf(created), world.Caller, Ct)).ShouldNotBeNull();

        await world.Data.DeleteAsync(
            Orders, IdOf(created), world.Caller, new AlvoPrecondition(VersionOf(advanced)), cancellationToken: Ct);
        (await world.Data.GetAsync(Orders, IdOf(created), world.Caller, Ct)).ShouldBeNull();
    }

    /// <summary>
    /// An entity with no <c>audit</c> has no version source at all, so it cannot answer "has this row
    /// changed since you read it". Refused — a silently ignored precondition is a lost update the caller
    /// believes it prevented, and they would have no way to find out. The message points at <c>audit: true</c>
    /// because that is the fix, and the ordinary update in the same act is the counterweight: this cannot be
    /// implemented as "refuse every update on a non-audited entity".
    /// </summary>
    [Fact]
    public async Task A_precondition_against_an_entity_with_no_version_column_is_refused_not_ignored()
    {
        var world = await UnauditedWorldAsync();
        var created = await world.Data.CreateAsync(Drafts, Payload("first"), world.Caller, cancellationToken: Ct);

        var refusal = await Should.ThrowAsync<AlvoPreconditionFailedException>(() => world.Data.UpdateAsync(
            Drafts,
            IdOf(created),
            Payload("second"),
            world.Caller,
            new AlvoPrecondition(DateTimeOffset.UnixEpoch),
            cancellationToken: Ct));
        refusal.Message.ShouldContain("audit");

        var stored = await world.Data.GetAsync(Drafts, IdOf(created), world.Caller, Ct);
        stored.ShouldNotBeNull();
        stored!["title"].ShouldBe("first", "the refused write must not have landed");

        var updated = await world.Data.UpdateAsync(
            Drafts, IdOf(created), Payload("second"), world.Caller, cancellationToken: Ct);
        updated["title"].ShouldBe("second");
    }

    /// <summary>
    /// The round trip, which is the whole reason a version is a stored value rather than a minted one:
    /// PostgreSQL's <c>timestamptz</c> keeps microseconds, SQLite keeps rendered text, and a .NET clock keeps
    /// 100-nanosecond ticks. Every precondition here is minted from the record the previous call
    /// <em>returned</em>, so an implementation that compares against anything other than the stored value —
    /// or a create that returns its candidate payload instead of re-reading the row — fails on the engine
    /// whose precision is coarsest, with no diagnosis available to the caller.
    /// </summary>
    [Fact]
    public async Task The_version_a_write_returns_is_the_one_a_following_precondition_accepts()
    {
        var world = await AuditedWorldAsync();
        var created = await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, cancellationToken: Ct);

        var updated = await world.Data.UpdateAsync(
            Orders, IdOf(created), Payload("second"), world.Caller, new AlvoPrecondition(VersionOf(created)), cancellationToken: Ct);
        var again = await world.Data.UpdateAsync(
            Orders, IdOf(created), Payload("third"), world.Caller, new AlvoPrecondition(VersionOf(updated)), cancellationToken: Ct);

        again["title"].ShouldBe("third");
    }

    /// <summary>
    /// Invisibility outranks the precondition. One stale version is used against two rows: the caller's own,
    /// which is visible, and another caller's, which their <c>USING</c> predicate excludes. The visible row
    /// answers <see cref="AlvoPreconditionFailedException"/> and the invisible one must still answer
    /// <see cref="AlvoRecordNotFoundException"/> — identically to a row that never existed. Ordered the other
    /// way round, "412 rather than 404" would confirm a row's existence to a caller who cannot read it, one
    /// request at a time; the pair of assertions is what makes the order observable at all.
    /// </summary>
    [Fact]
    public async Task A_stale_precondition_is_refused_before_the_policy_check_reveals_anything()
    {
        var world = await OwnedWorldAsync();
        var hers = await world.Data.CreateAsync(
            Tickets, OwnedPayload("hers", world.Alice), world.Alice, cancellationToken: Ct);
        var his = await world.Data.CreateAsync(
            Tickets, OwnedPayload("his", world.Bob), world.Bob, cancellationToken: Ct);
        var stale = VersionOf(hers);
        var advanced = await world.Data.UpdateAsync(
            Tickets, IdOf(hers), Payload("hers-again"), world.Alice, cancellationToken: Ct);
        VersionOf(advanced).ShouldNotBe(stale, "a write must advance the version, or nothing below discriminates");

        await Should.ThrowAsync<AlvoPreconditionFailedException>(() => world.Data.UpdateAsync(
            Tickets, IdOf(hers), Payload("x"), world.Alice, new AlvoPrecondition(stale), cancellationToken: Ct));

        await Should.ThrowAsync<AlvoRecordNotFoundException>(() => world.Data.UpdateAsync(
            Tickets, IdOf(his), Payload("x"), world.Alice, new AlvoPrecondition(stale), cancellationToken: Ct));
        await Should.ThrowAsync<AlvoRecordNotFoundException>(() => world.Data.DeleteAsync(
            Tickets, IdOf(his), world.Alice, new AlvoPrecondition(stale), cancellationToken: Ct));
    }

    /// <summary>
    /// The replay itself: the same key and the same fingerprint answer with the row the first request
    /// created. The version is compared too, because a replay that quietly re-wrote the row would return the
    /// right id with a new version — and the caller's own <c>If-Match</c> would then be stale for a request
    /// they believe never happened twice.
    /// </summary>
    [Fact]
    public async Task Replaying_an_idempotency_key_with_the_same_fingerprint_returns_the_first_row()
    {
        var world = await AuditedWorldAsync();
        var token = TokenFor(Orders);

        var first = await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, token, Ct);
        var replay = await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, token, Ct);

        IdOf(replay).ShouldBe(IdOf(first));
        VersionOf(replay).ShouldBe(VersionOf(first), "a replay returns the stored row, it does not write again");
        replay["title"].ShouldBe("first");
    }

    /// <summary>
    /// The half a returned row cannot prove on its own: nothing new was written. An implementation that
    /// created a second row and happened to return the first one's id would satisfy the fact above and fail
    /// this one.
    /// </summary>
    [Fact]
    public async Task Replaying_an_idempotency_key_returns_the_row_and_creates_no_second_one()
    {
        var world = await AuditedWorldAsync();
        var token = TokenFor(Orders);

        await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, token, Ct);
        await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, token, Ct);

        var all = await world.Data.QueryAsync(new AlvoQuery { Entity = Orders }, world.Caller, Ct);
        all.Items.Count.ShouldBe(1);
    }

    /// <summary>
    /// A key reused for a <em>different</em> request is not a replay: answering with the first row would
    /// report success for a create that never happened and silently discard the second payload. Refused, and
    /// the second row is not created either — the only two answers that do not lose data.
    /// </summary>
    [Fact]
    public async Task The_same_idempotency_key_with_a_different_fingerprint_is_a_conflict()
    {
        var world = await AuditedWorldAsync();
        var key = NewKey();

        await world.Data.CreateAsync(
            Orders, Payload("first"), world.Caller, new AlvoIdempotency(key, "fingerprint-of-the-first"), Ct);

        await Should.ThrowAsync<AlvoIdempotencyConflictException>(() => world.Data.CreateAsync(
            Orders, Payload("second"), world.Caller, new AlvoIdempotency(key, "fingerprint-of-the-second"), Ct));

        var all = await world.Data.QueryAsync(new AlvoQuery { Entity = Orders }, world.Caller, Ct);
        all.Items.Count.ShouldBe(1);
    }

    /// <summary>
    /// The case the whole mechanism exists for — a client that retried because the first response never
    /// arrived, so both requests are in flight at once. Both calls are started before either is awaited, so
    /// on any backend that awaits its I/O they genuinely overlap; a check-then-insert with no unique
    /// constraint behind it lets both pass the check and creates two rows. Both callers must also come back
    /// with the <em>same</em> row: the loser is translated into a replay, never into a raw provider
    /// exception the caller has no contract for.
    /// </summary>
    /// <remarks>
    /// <b>Only the PostgreSQL leg carries the proof that the unique constraint is what makes this true</b>, so
    /// a future change that drops that leg is dropping the evidence. On SQLite the loser is refused with
    /// <c>database is locked</c> before the constraint is ever consulted, so the file-level write lock
    /// serializes the pair; <c>InMemoryAlvoData</c> is synchronous, so two calls started here never interleave
    /// inside it at all. Both legs still prove that the replay path exists and that the two callers converge on
    /// one row — deleting the record lookup fails this fact everywhere — but deleting the table's
    /// <c>PRIMARY KEY</c> fails it on PostgreSQL alone.
    /// </remarks>
    [Fact]
    public async Task Two_concurrent_creates_with_one_idempotency_key_produce_exactly_one_row()
    {
        var world = await AuditedWorldAsync();
        var token = TokenFor(Orders);

        var first = world.Data.CreateAsync(Orders, Payload("first"), world.Caller, token, Ct);
        var second = world.Data.CreateAsync(Orders, Payload("first"), world.Caller, token, Ct);
        var both = await Task.WhenAll(first, second);

        IdOf(both[1]).ShouldBe(IdOf(both[0]), "the loser of the race must be answered with the winner's row");
        var all = await world.Data.QueryAsync(new AlvoQuery { Entity = Orders }, world.Caller, Ct);
        all.Items.Count.ShouldBe(1);
    }

    /// <summary>
    /// A key is the caller's own string, so two tenants will collide on <c>"1"</c> sooner rather than later.
    /// In a shared key space the second tenant's replay would be answered with the first tenant's row id — a
    /// cross-tenant read through the one channel that is meant to be a safe retry. Each tenant therefore
    /// gets its own row, and each sees exactly one.
    /// </summary>
    [Fact]
    public async Task An_idempotency_key_is_scoped_to_its_tenant_so_one_tenant_cannot_replay_anothers()
    {
        var world = await TenantedWorldAsync();
        var token = new AlvoIdempotency("1", $"{Invoices}:fingerprint-both-tenants-happen-to-share");

        var acme = await world.Data.CreateAsync(
            Invoices, TenantPayload("acme", world.Acme), world.AcmeCaller, token, Ct);
        var globex = await world.Data.CreateAsync(
            Invoices, TenantPayload("globex", world.Globex), world.GlobexCaller, token, Ct);

        IdOf(globex).ShouldNotBe(IdOf(acme), "a shared key space would answer one tenant with another's row");
        globex["title"].ShouldBe("globex");
        (await world.Data.QueryAsync(new AlvoQuery { Entity = Invoices }, world.AcmeCaller, Ct))
            .Items.Count.ShouldBe(1);
        (await world.Data.QueryAsync(new AlvoQuery { Entity = Invoices }, world.GlobexCaller, Ct))
            .Items.Count.ShouldBe(1);
    }

    /// <summary>
    /// The row-level authorization fact this suite was missing: two users in <b>one tenant</b> who happen to
    /// send the same key are two different clients, and the second must get their own row — never the first
    /// user's. It takes <b>both</b> halves of the fix to pass, and each half fails it differently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With the record's identity scoped to the tenant alone, the second caller's lookup <em>finds the first
    /// caller's record</em>. What happened next was the bypass: a replay re-read under the <c>create</c>
    /// decision carries no <c>USING</c> predicate at all (<c>create</c> has no stored row to filter, so that
    /// predicate is <see langword="null"/> by contract and a backend renders it as a constant true), so the
    /// second caller was handed the first caller's row.
    /// </para>
    /// <para>
    /// Re-reading under a <c>get</c> decision alone would turn that leak into an
    /// <see cref="AlvoRecordNotFoundException"/> — better, and still wrong: these are two distinct requests and
    /// the answer is two rows. Scoping the identity to the acting user is what makes the collision
    /// unreachable, which is why the assertion below is a <em>new row</em> rather than a refusal.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_replay_by_a_second_user_in_the_same_tenant_never_returns_the_first_users_row()
    {
        var world = await OwnedWorldAsync();
        var shared = new AlvoIdempotency("1", $"{Tickets}:a-fingerprint-two-clients-happen-to-share");

        var hers = await world.Data.CreateAsync(
            Tickets, OwnedPayload("hers", world.Alice), world.Alice, shared, Ct);
        var his = await world.Data.CreateAsync(Tickets, OwnedPayload("his", world.Bob), world.Bob, shared, Ct);

        IdOf(his).ShouldNotBe(IdOf(hers), "one client's key must never reach another client's record");
        his["owner_id"].ShouldBe(world.Bob.User.Value);
        his["title"].ShouldBe("his");

        (await world.Data.QueryAsync(new AlvoQuery { Entity = Tickets }, world.Alice, Ct))
            .Items.ShouldHaveSingleItem()["id"].ShouldBe(IdOf(hers));
        (await world.Data.QueryAsync(new AlvoQuery { Entity = Tickets }, world.Bob, Ct))
            .Items.ShouldHaveSingleItem()["id"].ShouldBe(IdOf(his));
    }

    /// <summary>
    /// A replay returns the same field set a <see cref="IAlvoData.GetAsync"/> by that caller returns: the field
    /// this caller's <c>hidden</c> expression covers is absent from both, so a replay is masked like the read
    /// it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The axis this discriminates is "masked or not".</b> Returning the stored row unmasked is the one-line
    /// production change that fails it, and that is a real and easy mistake on a path that already has the row
    /// in hand.
    /// </para>
    /// <para>
    /// <b>The axis it cannot discriminate is the operation.</b> <c>PolicyEngine</c> builds every decision's mask
    /// from the same <c>policy.Hidden</c> plus the context, so <c>hidden</c> is per entity and per caller and
    /// never per operation: a <c>create</c> decision's mask and a <c>get</c> decision's are equal for one
    /// caller, and swapping which one the replay uses changes nothing here. The name says "the same field set a
    /// get returns" rather than "masks as a get would" for exactly that reason — a name that promises more than
    /// the body can deliver is a vacuous test one level up. What proves the replay reads under <c>get</c>, and
    /// stops answering the full row the moment <c>get</c> is denied outright, is
    /// <see cref="A_replay_on_an_entity_the_caller_cannot_read_performs_no_row_read"/>, on the visibility axis,
    /// where the two decisions genuinely differ.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_replay_returns_the_same_field_set_a_get_by_that_caller_returns()
    {
        var world = await MaskedWorldAsync();
        var token = TokenFor(Vaults);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = "first",
            ["secret"] = "shh",
        };

        var created = await world.Data.CreateAsync(Vaults, payload, world.Caller, token, Ct);
        var replay = await world.Data.CreateAsync(Vaults, payload, world.Caller, token, Ct);
        var read = await world.Data.GetAsync(Vaults, IdOf(created), world.Caller, Ct);

        read.ShouldNotBeNull();
        replay.Values.Keys.OrderBy(key => key, StringComparer.Ordinal)
            .ShouldBe(read!.Values.Keys.OrderBy(key => key, StringComparer.Ordinal));
        replay.Values.ContainsKey("secret").ShouldBeFalse("a replay is a read, and this caller may not read it");
    }

    /// <summary>
    /// A replay of a create on an entity this caller may write but not read is no longer refused: it answers
    /// with the id alone, and — the fact that matters — it performs <b>no row read</b> to produce it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why "no row read" cannot be proved by inspecting the answer alone.</b> A regression that read the
    /// recorded row under the <c>create</c> decision — the exact bypass the row-level fix above closes — and
    /// then discarded every field but <c>id</c> would produce an identical-looking id-only record, because
    /// <c>create</c>'s <c>USING</c> predicate is <see langword="null"/> and a backend renders that as a
    /// constant true: it matches any existing row, so the read would silently succeed and this fact would pass
    /// for the wrong reason.
    /// </para>
    /// <para>
    /// <b>The structural proof: the row is hard-deleted before the replay.</b> <see cref="Dropbox"/> is given a
    /// <c>delete</c> rule for exactly this — the caller removes their own row outright once created. With the
    /// row physically gone, <em>any</em> read of <c>record.RowId</c> fails with
    /// <see cref="AlvoRecordNotFoundException"/>, whichever decision or predicate it is read under — a
    /// constant-true <c>create</c> predicate included, since there is no row left for any predicate to match.
    /// The only way this fact can still pass is for the replay to never issue that read at all and answer from
    /// the idempotency record's own <c>RowId</c> instead, which is exactly the fix's claim. A predicate-based
    /// proof (excluding the row from a <em>configured</em> rule) cannot do this: it would still let a
    /// create-decision read through, since that predicate is never consulted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_replay_on_an_entity_the_caller_cannot_read_performs_no_row_read()
    {
        var world = await WriteOnlyWorldAsync();
        var token = TokenFor(Dropbox);

        var created = await world.Data.CreateAsync(Dropbox, Payload("first"), world.Caller, token, Ct);
        await world.Data.DeleteAsync(Dropbox, IdOf(created), world.Caller, cancellationToken: Ct);

        var replay = await world.Data.CreateAsync(Dropbox, Payload("first"), world.Caller, token, Ct);

        IdOf(replay).ShouldBe(IdOf(created), "the replay must still name the row it created, gone or not");
        replay.Values.Keys.ShouldBe(
            [AlvoManagedColumns.Id], "no field beyond the id may appear — none but the id was ever read");

        var another = await world.Data.CreateAsync(
            Dropbox, Payload("second"), world.Caller, TokenFor(Dropbox), Ct);
        IdOf(another).ShouldNotBe(IdOf(created), "writing under a fresh key must still work on this entity");
    }

    /// <summary>
    /// One key on two entities. A conforming fingerprint covers the entity (see
    /// <see cref="AlvoIdempotency.Fingerprint"/>), so this is a different request under a used key — a
    /// conflict, not a silent nothing. The second arm is the fail-closed branch for a caller whose fingerprint
    /// does <em>not</em> distinguish the entity: the recorded row id is not in the entity being served, so the
    /// answer is <see cref="AlvoRecordNotFoundException"/> and never a cross-entity row.
    /// </summary>
    /// <remarks>
    /// This is what makes the dropped <c>entity</c> column safe. Storing it and never reading it — the first
    /// round's shape — made one key unique per scope across every entity while telling the lookup nothing, so
    /// reusing a key on a second entity silently created nothing at all.
    /// </remarks>
    [Fact]
    public async Task The_same_key_on_a_different_entity_is_a_conflict_not_a_silent_replay()
    {
        var world = await TwoEntityWorldAsync();
        var key = NewKey();

        await world.Data.CreateAsync(
            Orders, Payload("first"), world.Caller, new AlvoIdempotency(key, $"{Orders}:body"), Ct);

        await Should.ThrowAsync<AlvoIdempotencyConflictException>(() => world.Data.CreateAsync(
            Receipts, Payload("first"), world.Caller, new AlvoIdempotency(key, $"{Receipts}:body"), Ct));

        await Should.ThrowAsync<AlvoRecordNotFoundException>(() => world.Data.CreateAsync(
            Receipts, Payload("first"), world.Caller, new AlvoIdempotency(key, $"{Orders}:body"), Ct));

        (await world.Data.QueryAsync(new AlvoQuery { Entity = Orders }, world.Caller, Ct)).Items.Count.ShouldBe(1);
        (await world.Data.QueryAsync(new AlvoQuery { Entity = Receipts }, world.Caller, Ct)).Items.ShouldBeEmpty();
    }

    /// <summary>
    /// An idempotency key needs an identity to be scoped to, and every anonymous caller carries the same
    /// reserved all-zero one — so a token from an anonymous caller is refused rather than filed under a key
    /// space every anonymous caller shares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The refusal is the malformed-request family (422) with a fix suggestion, not a denial: the caller is not
    /// being told they may not create — the same create without a token still lands, which is the counterweight
    /// in the same act — they are being told this combination cannot be served.
    /// </para>
    /// <para>
    /// <b>This is also the fact that proves both implementations call the guard</b>, because it is inherited:
    /// the port owns the rule (<see cref="AlvoIdempotency.EnsureUsableKey"/>) and an implementation
    /// that skipped the call would fail here on its own leg while the other stayed green.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_idempotency_token_from_an_anonymous_caller_is_refused_with_a_fix_suggestion()
    {
        var world = await AnonymousWorldAsync();

        var refusal = await Should.ThrowAsync<ArgumentException>(() => world.Data.CreateAsync(
            Orders, Payload("first"), AlvoContext.Anonymous, TokenFor(Orders), Ct));
        refusal.Message.ShouldContain("without an idempotency key");

        var created = await world.Data.CreateAsync(
            Orders, Payload("first"), AlvoContext.Anonymous, cancellationToken: Ct);
        created["title"].ShouldBe("first");
    }

    /// <summary>
    /// The counterweight that keeps the refusal narrow: <see cref="AlvoContext.System"/> carries a
    /// <b>distinct</b> reserved user id, not the all-zero one, so a system-context token scopes like any other
    /// caller's and stays legal — including its replay.
    /// </summary>
    /// <remarks>
    /// Measured rather than assumed: <c>AlvoContext.Anonymous.User</c> is the all-zero
    /// <see cref="UserId"/> and <c>AlvoContext.System(...).User</c> is <c>…-0000000000a1</c>. Were they equal,
    /// the two would share one key space and refusing only the anonymous one would be a half-measure — so this
    /// fact is what makes the guard's exemption a checked property rather than a reading of the code.
    /// </remarks>
    [Fact]
    public async Task An_idempotency_token_from_the_system_context_is_accepted()
    {
        var world = await AuditedWorldAsync();
        var system = AlvoContext.System(tenant: null);
        var token = TokenFor(Orders);

        var created = await world.Data.CreateAsync(Orders, Payload("first"), system, token, Ct);
        var replay = await world.Data.CreateAsync(Orders, Payload("first"), system, token, Ct);

        IdOf(replay).ShouldBe(IdOf(created));
        (await world.Data.QueryAsync(new AlvoQuery { Entity = Orders }, system, Ct)).Items.Count.ShouldBe(1);
    }

    /// <summary>
    /// A <b>blank</b> idempotency key is refused, and it is refused by the port — because a request layer is not
    /// the only caller, and this is the rule whose absence is silent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An embedded host reaches <c>CreateAsync</c> with no HTTP in front of it, and
    /// <see cref="AlvoIdempotency"/> is a <c>readonly record struct</c>: <c>default(AlvoIdempotency)</c> and
    /// <c>new AlvoIdempotency("", "")</c> both exist and no constructor can be made to run, so a static guard is
    /// the only shape that can hold this at all. Both spellings are driven here for exactly that reason.
    /// </para>
    /// <para>
    /// <b>Why blank is the worst of the three key rules.</b> The empty string lands in
    /// <c>PRIMARY KEY (idempotency_key, scope)</c> and <em>succeeds</em>, so every caller in one scope who ever
    /// sent a blank key would share a single record — the shared key space
    /// <see cref="AlvoIdempotency.IdentityOf"/> exists to remove, restored silently. An over-long key at least
    /// fails loudly at storage; a blank one starts answering the wrong row.
    /// </para>
    /// <para>
    /// The tokenless create at the end is the counterweight, exactly as in the anonymous fact: it proves the
    /// refusal is about the key rather than about a create this world would refuse anyway.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_blank_idempotency_key_is_refused_by_the_port()
    {
        var world = await AuditedWorldAsync();

        foreach (var blank in new[] { string.Empty, "   ", null! })
        {
            var refusal = await Should.ThrowAsync<ArgumentException>(() => world.Data.CreateAsync(
                Orders, Payload("first"), world.Caller, new AlvoIdempotency(blank, "a-digest"), Ct));
            refusal.Message.ShouldContain("must not be blank");
        }

        await Should.ThrowAsync<ArgumentException>(() => world.Data.CreateAsync(
            Orders, Payload("first"), world.Caller, default(AlvoIdempotency), Ct));

        var created = await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, cancellationToken: Ct);
        created["title"].ShouldBe("first");
    }

    /// <summary>
    /// A key past <see cref="AlvoIdempotency.MaxKeyBytes"/> is refused by the port, <b>counted in UTF-8
    /// bytes</b> — and a key of exactly the bound is accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The unit is the fact.</b> The bound exists because the key is half of the record's composite primary
    /// key and PostgreSQL caps a btree index entry at roughly 2700 bytes. Counted in UTF-16
    /// <c>string.Length</c> — which is how the HTTP layer first spelled it — a key of two-byte characters is
    /// half its byte size, so <see cref="AlvoIdempotency.MaxKeyBytes"/> characters of <c>é</c> pass a character
    /// count while being twice the bound in bytes. That key is the second oversized case below; a build counting
    /// characters accepts it and fails here.
    /// </para>
    /// <para>
    /// On the port rather than only in a request layer for the reason the blank rule is: an embedded host is a
    /// caller too, and it is the one that can hand storage an unbounded index key.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_idempotency_key_past_the_ports_byte_bound_is_refused()
    {
        var world = await AuditedWorldAsync();
        var atTheBound = new string('k', AlvoIdempotency.MaxKeyBytes);

        var created = await world.Data.CreateAsync(
            Orders, Payload("first"), world.Caller, new AlvoIdempotency(atTheBound, "a-digest"), Ct);
        created["title"].ShouldBe("first", "a key of exactly the bound must be usable");

        foreach (var oversized in new[] { atTheBound + "k", new string('é', AlvoIdempotency.MaxKeyBytes) })
        {
            var refusal = await Should.ThrowAsync<ArgumentException>(() => world.Data.CreateAsync(
                Orders, Payload("second"), world.Caller, new AlvoIdempotency(oversized, "a-digest"), Ct));
            refusal.Message.ShouldContain("bytes when encoded as UTF-8");
        }
    }

    private const string Orders = "orders";
    private const string Receipts = "receipts";
    private const string Tickets = "tickets";
    private const string Drafts = "drafts";
    private const string Invoices = "invoices";
    private const string Vaults = "vaults";
    private const string Dropbox = "dropbox";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A key no other fact can collide with, so facts stay independent even on a shared store.</summary>
    private static string NewKey() => $"key-{Guid.NewGuid():N}";

    /// <summary>
    /// A fresh token whose fingerprint covers <paramref name="entity"/>, as
    /// <see cref="AlvoIdempotency.Fingerprint"/> requires of whoever computes one.
    /// </summary>
    /// <param name="entity">The entity the fingerprinted request writes.</param>
    private static AlvoIdempotency TokenFor(string entity) => new(NewKey(), $"{entity}:a-request-digest");

    private static Dictionary<string, object?> Payload(string title) =>
        new(StringComparer.Ordinal) { ["title"] = title };

    private static Dictionary<string, object?> OwnedPayload(string title, AlvoContext owner) =>
        new(StringComparer.Ordinal) { ["title"] = title, ["owner_id"] = owner.User.Value };

    private static Dictionary<string, object?> TenantPayload(string title, TenantId tenant) =>
        new(StringComparer.Ordinal) { ["title"] = title, ["tenant_id"] = tenant.Value };

    private static Guid IdOf(AlvoRecord record) => (Guid)record[AlvoManagedColumns.Id]!;

    /// <summary>
    /// The row's version as this port returned it, read from the record rather than reconstructed — which is
    /// the point of every round-trip assertion above.
    /// </summary>
    private static DateTimeOffset VersionOf(AlvoRecord record) =>
        (DateTimeOffset)record[AlvoManagedColumns.UpdatedAt]!;

    /// <summary>An audited, global <c>orders</c> entity every operation is permitted on.</summary>
    private Task<World> AuditedWorldAsync() => WorldAsync(EntityFixture.Permissive(Orders, audit: true));

    /// <summary>
    /// An audited <c>orders</c> entity whose rules admit the anonymous caller, so the token refusal is reached
    /// on a create the policy would otherwise allow — and the tokenless create in the same fact really lands.
    /// </summary>
    private Task<World> AnonymousWorldAsync() => WorldAsync(
        EntityFixture.Permissive(Orders, audit: true) with
        {
            Rules = new AccessRules { List = "true", Get = "true", Create = "true" },
        });

    /// <summary>A non-audited <c>drafts</c> entity — the one with no version column at all.</summary>
    private Task<World> UnauditedWorldAsync() => WorldAsync(EntityFixture.Permissive(Drafts, audit: false));

    /// <summary>
    /// An audited <c>tickets</c> entity row-scoped by owner, so one caller's row is genuinely invisible to
    /// the other — which is what lets the check-order and cross-user facts tell their answers apart.
    /// </summary>
    private Task<World> OwnedWorldAsync() => WorldAsync(
        EntityFixture.Permissive(Tickets, audit: true) with
        {
            Rules = OwnerRules,
            Extra = ("owner_id", DescField.Uuid),
        });

    /// <summary>An audited, tenant-scoped <c>invoices</c> entity, plus a caller in each of two tenants.</summary>
    private Task<World> TenantedWorldAsync() => WorldAsync(
        EntityFixture.Permissive(Invoices, audit: true) with { Tenancy = EntityTenancy.Scoped });

    /// <summary>
    /// An audited <c>vaults</c> entity with a field whose <c>hidden</c> expression covers a non-admin caller,
    /// so a replay's projection is comparable against what a <c>get</c> by that caller returns.
    /// </summary>
    private Task<World> MaskedWorldAsync() => WorldAsync(
        EntityFixture.Permissive(Vaults, audit: true) with { Hidden = ("secret", "!('admin' in @user.roles)") });

    /// <summary>
    /// An audited <c>dropbox</c> entity a caller may write and not read — no <c>get</c> or <c>list</c> rule at
    /// all, so the replay's own read has nothing to resolve. <c>delete</c> is granted too, deliberately: it is
    /// still a write, and it is what lets <see cref="A_replay_on_an_entity_the_caller_cannot_read_performs_no_row_read"/>
    /// remove the row out from under a replay to prove structurally that nothing reads it.
    /// </summary>
    private Task<World> WriteOnlyWorldAsync() => WorldAsync(
        EntityFixture.Permissive(Dropbox, audit: true) with
        {
            Rules = new AccessRules { Create = "true", Delete = "true" },
        });

    /// <summary>Two audited, permissive entities in one store, for the one-key-two-entities fact.</summary>
    private Task<World> TwoEntityWorldAsync() => WorldAsync(
        EntityFixture.Permissive(Orders, audit: true),
        EntityFixture.Permissive(Receipts, audit: true));

    private const string OwnerRule = "owner_id == @user.id";

    private static AccessRules OwnerRules => new()
    {
        List = OwnerRule,
        Get = OwnerRule,
        Create = OwnerRule,
        Update = OwnerRule,
        Delete = OwnerRule,
    };

    /// <summary>
    /// One entity of a fixture: the traits the descriptor and the schema have to agree on, in one place so the
    /// pair cannot drift.
    /// </summary>
    /// <param name="Name">The entity name.</param>
    /// <param name="Audit">Whether it declares <c>audit</c>, and therefore has a version column.</param>
    /// <param name="Tenancy">Its tenancy.</param>
    /// <param name="Rules">The access rules to compile.</param>
    /// <param name="Extra">An additional required field, for the owner-scoped fixture.</param>
    /// <param name="Hidden">A field and the <c>hidden</c> expression that masks it, for the masking fixture.</param>
    private sealed record EntityFixture(
        string Name,
        bool Audit,
        EntityTenancy Tenancy,
        AccessRules Rules,
        (string Name, DescField Type)? Extra = null,
        (string Field, string Expression)? Hidden = null)
    {
        internal static EntityFixture Permissive(string name, bool audit) => new(
            name,
            audit,
            EntityTenancy.Global,
            new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" });
    }

    /// <summary>
    /// A store over <paramref name="entities"/>, its descriptor and its schema paired by hand — the schema
    /// mapper that injects the managed columns is <see langword="internal"/> to the core, so this suite pairs
    /// them exactly as the adversarial suite does.
    /// </summary>
    /// <param name="entities">The entities the fixture declares.</param>
    private async Task<World> WorldAsync(params EntityFixture[] entities)
    {
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "concurrency-fixture",
            Entities = entities.ToDictionary(entity => entity.Name, DescriptorOf, StringComparer.Ordinal),
        };

        var data = await CreateAsync(
            new SchemaModel([.. entities.Select(SchemaOf)]),
            descriptor,
            new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal));

        return new World(data);
    }

    private static EntityDescriptor DescriptorOf(EntityFixture entity) => new()
    {
        Tenancy = entity.Tenancy,
        Audit = entity.Audit,
        Fields = DescriptorFieldsOf(entity),
        Rules = entity.Rules,
    };

    private static Dictionary<string, FieldDescriptor> DescriptorFieldsOf(EntityFixture entity)
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescField.String },
        };
        if (entity.Extra is { } extra)
        {
            fields[extra.Name] = new FieldDescriptor { Type = extra.Type, Required = true };
        }

        if (entity.Hidden is { } hidden)
        {
            fields[hidden.Field] = new FieldDescriptor
            {
                Type = DescField.String,
                Hidden = BoolOrCel.FromExpression(hidden.Expression),
            };
        }

        return fields;
    }

    /// <summary>
    /// The schema half of one fixture entity: the row key, the declared fields, and whichever framework
    /// columns the traits ask for.
    /// </summary>
    private static EntitySchema SchemaOf(EntityFixture entity) => new()
    {
        Name = entity.Name,
        Tenancy = entity.Tenancy == EntityTenancy.Scoped ? TenancyMode.Scoped : TenancyMode.Global,
        Audit = entity.Audit,
        Fields = [.. SchemaFieldsOf(entity)],
    };

    /// <summary>The row key and whatever the fixture declares, then whatever its traits inject.</summary>
    private static IEnumerable<FieldSchema> SchemaFieldsOf(EntityFixture entity) =>
        [.. DeclaredFieldsOf(entity), .. ManagedFieldsOf(entity)];

    private static IEnumerable<FieldSchema> DeclaredFieldsOf(EntityFixture entity)
    {
        yield return new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true };
        yield return new FieldSchema { Name = "title", Type = SchemaField.String, Nullable = true };

        if (entity.Extra is { } extra)
        {
            yield return new FieldSchema
            {
                Name = extra.Name,
                Type = Enum.Parse<SchemaField>(extra.Type.ToString()),
                Required = true,
            };
        }

        if (entity.Hidden is { } hidden)
        {
            yield return new FieldSchema { Name = hidden.Field, Type = SchemaField.String, Nullable = true };
        }
    }

    /// <summary>The columns the framework injects for these traits, in the mapper's own order.</summary>
    private static IEnumerable<FieldSchema> ManagedFieldsOf(EntityFixture entity) =>
    [
        .. entity.Tenancy == EntityTenancy.Scoped ? TenantField : [],
        .. entity.Audit ? AuditFields : [],
    ];

    private static IEnumerable<FieldSchema> TenantField =>
    [
        new FieldSchema
        {
            Name = AlvoManagedColumns.TenantId,
            Type = SchemaField.Uuid,
            Required = true,
            Indexed = true,
        },
    ];

    /// <summary>
    /// The audit quartet as the schema mapper injects it. <c>updated_at</c> is <c>required</c> — the version
    /// column a precondition compares can never be absent on a row the framework wrote.
    /// </summary>
    private static IEnumerable<FieldSchema> AuditFields =>
    [
        new FieldSchema { Name = AlvoManagedColumns.CreatedAt, Type = SchemaField.DateTime, Required = true },
        new FieldSchema { Name = AlvoManagedColumns.CreatedBy, Type = SchemaField.Uuid, Nullable = true },
        new FieldSchema { Name = AlvoManagedColumns.UpdatedAt, Type = SchemaField.DateTime, Required = true },
        new FieldSchema { Name = AlvoManagedColumns.UpdatedBy, Type = SchemaField.Uuid, Nullable = true },
    ];

    /// <summary>One fixture database plus the callers and tenants the facts above write as.</summary>
    private sealed class World(IAlvoData data)
    {
        internal IAlvoData Data { get; } = data;

        /// <summary>The single caller the global fixtures write as.</summary>
        internal AlvoContext Caller { get; } = NewCaller(tenant: null);

        internal TenantId Acme { get; } = TenantId.New();

        internal TenantId Globex { get; } = TenantId.New();

        /// <summary>
        /// Two callers <b>in one tenant</b>, which is what the cross-user replay fact needs: with a record
        /// identity scoped to the tenant alone, these two would share one key space.
        /// </summary>
        internal AlvoContext Alice => _alice ??= NewCaller(Acme);

        /// <inheritdoc cref="Alice"/>
        internal AlvoContext Bob => _bob ??= NewCaller(Acme);

        internal AlvoContext AcmeCaller => _acmeCaller ??= NewCaller(Acme);

        internal AlvoContext GlobexCaller => _globexCaller ??= NewCaller(Globex);

        private AlvoContext? _alice;
        private AlvoContext? _bob;
        private AlvoContext? _acmeCaller;
        private AlvoContext? _globexCaller;

        private static AlvoContext NewCaller(TenantId? tenant) => new()
        {
            User = UserId.New(),
            Roles = new HashSet<Role> { Role.Authenticated },
            Tenant = tenant,
        };
    }
}
