using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The transactional batch as a rule of the <b>port</b>, proved over every <see cref="IAlvoData"/>
/// implementation this suite runs against: a batch writes every row or none, judges each row individually
/// against the caller's own policy, and reports every offending row rather than the first.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this suite exists to catch is "checks the first row and lets the rest through."</b> #106's
/// per-row policy is the one place in the framework where a plausible implementation — resolve once, check
/// once, write many — is a bulk authorization bypass. So every fact here that could pass vacuously carries a
/// control: a refusal is paired with the same batch minus the bad row, and an atomicity claim is asserted as
/// a count over the whole entity rather than per row, because a partial commit is exactly what a per-row
/// assertion would miss.
/// </para>
/// <para>
/// <b>The count is taken through <c>QueryAsync</c> and that is sound here, not a shortcut.</b> Every fixture
/// this suite uses is permissive and global, so the caller's decision carries no <c>USING</c> predicate and
/// the page it reads <em>is</em> the entity. The one fixture where that would be untrue is the tenant-scoped
/// one, and there the assertion is not a count at all: the other tenant's row is read back <b>as that
/// tenant</b>, which is the stronger claim because it compares the value rather than a number.
/// </para>
/// </remarks>
public abstract class AlvoDataBatchTests : AlvoDataFixture
{
    /// <summary>
    /// The headline claim: a batch is one transaction. A batch whose <b>last</b> row fails leaves no row
    /// written — asserted as a count over the whole entity, because a partial commit is exactly what a
    /// per-row assertion would miss.
    /// </summary>
    /// <remarks>
    /// The control is the second batch: the same rows without the offending one write, so "nothing was
    /// written" cannot pass because the batch path writes nothing at all.
    /// </remarks>
    [Fact]
    public async Task A_batch_whose_last_row_fails_writes_nothing()
    {
        var world = await OwnedWorldAsync();

        var refused = await world.Data.CreateManyAsync(
            Tickets,
            [OwnedPayload("first", world.Alice), OwnedPayload("second", world.Alice), UnownedPayload("third")],
            world.Alice,
            cancellationToken: Ct);

        refused.Succeeded.ShouldBeFalse();
        refused.Rows.ShouldBeEmpty();
        (await CountAsync(world, Tickets, world.Alice)).ShouldBe(0, "a batch commits every row or none");

        var wrote = await world.Data.CreateManyAsync(
            Tickets,
            [OwnedPayload("first", world.Alice), OwnedPayload("second", world.Alice)],
            world.Alice,
            cancellationToken: Ct);

        wrote.Succeeded.ShouldBeTrue();
        (await CountAsync(world, Tickets, world.Alice)).ShouldBe(2, "or the batch path writes nothing at all");
    }

    /// <summary>
    /// Every offending row is reported, not the first — a five-hundred-row import that reports row 3 and
    /// stops will be run five hundred times.
    /// </summary>
    [Fact]
    public async Task Every_offending_row_is_reported_with_its_index()
    {
        var world = await OwnedWorldAsync();

        var result = await world.Data.CreateManyAsync(
            Tickets,
            [
                OwnedPayload("ok", world.Alice),
                UnownedPayload("bad"),
                OwnedPayload("ok", world.Alice),
                OwnedPayload("ok", world.Alice),
                UnownedPayload("bad"),
            ],
            world.Alice,
            cancellationToken: Ct);

        result.Refusals.Select(refusal => refusal.Index).ShouldBe(
            [1, 4], ignoreOrder: false, customMessage: "every bad row, in the order the batch carried them");
    }

    /// <summary>
    /// Policy is judged per row over that row's own post-image, so a batch cannot smuggle a value past a
    /// check by pairing it with a row that passes.
    /// </summary>
    [Fact]
    public async Task A_row_the_check_refuses_is_refused_even_beside_rows_it_admits()
    {
        var world = await OwnedWorldAsync();

        var alone = await world.Data.CreateManyAsync(
            Tickets, [ForeignPayload("theirs", world.Bob)], world.Alice, cancellationToken: Ct);
        var beside = await world.Data.CreateManyAsync(
            Tickets,
            [OwnedPayload("mine", world.Alice), ForeignPayload("theirs", world.Bob)],
            world.Alice,
            cancellationToken: Ct);

        alone.Refusals.ShouldNotBeEmpty();
        beside.Refusals.Select(refusal => refusal.Index).ShouldBe(
            [1], customMessage: "a passing neighbour must not admit a failing row");
        (await CountAsync(world, Tickets, world.Alice)).ShouldBe(0);
    }

    /// <summary>
    /// Cross-tenant isolation, as a test rather than an argument: a batch naming one row of another tenant
    /// writes nothing, and the same batch without that row succeeds.
    /// </summary>
    /// <remarks>
    /// The other tenant's row is read back <b>as its own tenant</b> rather than counted, which is what makes
    /// this a claim about that row rather than about how many rows exist.
    /// </remarks>
    [Fact]
    public async Task A_batch_naming_another_tenants_row_writes_nothing()
    {
        var world = await TenantedWorldAsync();
        var theirs = IdOf(await world.Data.CreateAsync(
            Invoices, TenantPayload("theirs", world.Globex), world.GlobexCaller, cancellationToken: Ct));
        var mine = IdOf(await world.Data.CreateAsync(
            Invoices, TenantPayload("mine", world.Acme), world.AcmeCaller, cancellationToken: Ct));

        var refused = await world.Data.UpdateManyAsync(
            Invoices,
            [new AlvoRowPatch(mine, Payload("renamed")), new AlvoRowPatch(theirs, Payload("renamed"))],
            world.AcmeCaller,
            cancellationToken: Ct);

        refused.Succeeded.ShouldBeFalse();
        refused.Rows.ShouldBeEmpty();

        var untouched = await world.Data.GetAsync(Invoices, theirs, world.GlobexCaller, Ct);
        untouched.ShouldNotBeNull()["title"].ShouldBe(
            "theirs", "the other tenant's row must be exactly as they left it");

        var allowed = await world.Data.UpdateManyAsync(
            Invoices, [new AlvoRowPatch(mine, Payload("renamed"))], world.AcmeCaller, cancellationToken: Ct);
        allowed.Rows.Count.ShouldBe(1, "the same batch without the other tenant's row must succeed");
    }

    /// <summary>
    /// A batch update naming a row another <b>user of the same tenant</b> owns writes nothing, and that
    /// user's row is untouched.
    /// </summary>
    /// <remarks>
    /// The tenant facts prove the scope; this proves the row predicate, which is a different mechanism. Two
    /// callers in one tenant differ only in which rows <c>owner_id == @user.id</c> admits, so a batch that
    /// crossed that line would be a per-row <c>USING</c> failure the tenant facts cannot see.
    /// </remarks>
    [Fact]
    public async Task A_batch_update_naming_another_users_row_writes_nothing()
    {
        var world = await OwnedWorldAsync();
        var hers = IdOf(await world.Data.CreateAsync(
            Tickets, OwnedPayload("hers", world.Alice), world.Alice, cancellationToken: Ct));
        var his = IdOf(await world.Data.CreateAsync(
            Tickets, OwnedPayload("his", world.Bob), world.Bob, cancellationToken: Ct));

        var refused = await world.Data.UpdateManyAsync(
            Tickets,
            [new AlvoRowPatch(hers, Payload("renamed")), new AlvoRowPatch(his, Payload("renamed"))],
            world.Alice,
            cancellationToken: Ct);

        refused.Succeeded.ShouldBeFalse();
        refused.Rows.ShouldBeEmpty();

        var untouched = await world.Data.GetAsync(Tickets, his, world.Bob, Ct);
        untouched.ShouldNotBeNull()["title"].ShouldBe("his", "the other user's row must be as they left it");

        var allowed = await world.Data.UpdateManyAsync(
            Tickets, [new AlvoRowPatch(hers, Payload("renamed"))], world.Alice, cancellationToken: Ct);
        allowed.Rows.Count.ShouldBe(1, "the same batch without the other user's row must succeed");
    }

    /// <summary>A batch delete naming another user's row removes nothing, including her own.</summary>
    /// <remarks>
    /// The delete verb's own per-row <c>USING</c> fact. It had none — its only isolation-adjacent fact was
    /// "an absent row refuses the batch", over a permissive global fixture, which cannot tell a predicate
    /// from an absence.
    /// </remarks>
    [Fact]
    public async Task A_batch_delete_naming_another_users_row_removes_nothing()
    {
        var world = await OwnedWorldAsync();
        var hers = IdOf(await world.Data.CreateAsync(
            Tickets, OwnedPayload("hers", world.Alice), world.Alice, cancellationToken: Ct));
        var his = IdOf(await world.Data.CreateAsync(
            Tickets, OwnedPayload("his", world.Bob), world.Bob, cancellationToken: Ct));

        var refused = await world.Data.DeleteManyAsync(Tickets, [hers, his], world.Alice, cancellationToken: Ct);

        refused.Succeeded.ShouldBeFalse();
        (await world.Data.GetAsync(Tickets, his, world.Bob, Ct)).ShouldNotBeNull("his row survives");
        (await world.Data.GetAsync(Tickets, hers, world.Alice, Ct)).ShouldNotBeNull(
            "and so does hers — a refused batch removes nothing at all");

        var removed = await world.Data.DeleteManyAsync(Tickets, [hers], world.Alice, cancellationToken: Ct);
        removed.Succeeded.ShouldBeTrue("the same batch without the other user's row must succeed");
    }

    /// <summary>A batch delete naming another <b>tenant's</b> row removes nothing.</summary>
    /// <remarks>
    /// The two-tenant half the delete verb was missing. The other tenant's row is read back as that tenant
    /// rather than counted, which compares the row rather than a number.
    /// </remarks>
    [Fact]
    public async Task A_batch_delete_naming_another_tenants_row_removes_nothing()
    {
        var world = await TenantedWorldAsync();
        var theirs = IdOf(await world.Data.CreateAsync(
            Invoices, TenantPayload("theirs", world.Globex), world.GlobexCaller, cancellationToken: Ct));
        var mine = IdOf(await world.Data.CreateAsync(
            Invoices, TenantPayload("mine", world.Acme), world.AcmeCaller, cancellationToken: Ct));

        var refused = await world.Data.DeleteManyAsync(
            Invoices, [mine, theirs], world.AcmeCaller, cancellationToken: Ct);

        refused.Succeeded.ShouldBeFalse();
        (await world.Data.GetAsync(Invoices, theirs, world.GlobexCaller, Ct))
            .ShouldNotBeNull()["title"].ShouldBe("theirs");

        var removed = await world.Data.DeleteManyAsync(
            Invoices, [mine], world.AcmeCaller, cancellationToken: Ct);
        removed.Succeeded.ShouldBeTrue("the same batch without the other tenant's row must succeed");
    }

    /// <summary>
    /// A row another tenant owns and a row that never existed are <b>one</b> refusal, byte for byte.
    /// Distinguishing them would make a batch answer as many existence questions per request as it carries
    /// rows — the oracle the single-row not-found closes, multiplied by the batch size.
    /// </summary>
    [Fact]
    public async Task An_invisible_row_and_an_absent_row_are_one_refusal()
    {
        var world = await TenantedWorldAsync();
        var theirs = IdOf(await world.Data.CreateAsync(
            Invoices, TenantPayload("theirs", world.Globex), world.GlobexCaller, cancellationToken: Ct));

        var invisible = await world.Data.UpdateManyAsync(
            Invoices, [new AlvoRowPatch(theirs, Payload("renamed"))], world.AcmeCaller, cancellationToken: Ct);
        var absent = await world.Data.UpdateManyAsync(
            Invoices,
            [new AlvoRowPatch(Guid.NewGuid(), Payload("renamed"))],
            world.AcmeCaller,
            cancellationToken: Ct);

        var hidden = invisible.Refusals.ShouldHaveSingleItem();
        var missing = absent.Refusals.ShouldHaveSingleItem();
        hidden.Code.ShouldBe(missing.Code);
        hidden.Message.ShouldBe(missing.Message);
        hidden.FixSuggestion.ShouldBe(missing.FixSuggestion);
    }

    /// <summary>
    /// A batch naming one row twice is refused, and every repeat is named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a <c>WITH CHECK</c> bypass, not a tidiness rule.</b> Every row is judged against its own
    /// locked pre-image <em>before</em> any row is written, so two patches for one row are both judged
    /// against the <em>original</em> — and then both applied. The stored row is the composition of the two,
    /// which no verdict ever saw. With a rule of the shape <c>a != b</c> over <c>{a:1, b:2}</c>: <c>{a:5}</c>
    /// passes against <c>{a:5, b:2}</c>, <c>{b:5}</c> passes against <c>{a:1, b:5}</c>, and <c>{a:5, b:5}</c>
    /// lands.
    /// </para>
    /// <para>
    /// Refused rather than folded, because a partial order over one row is not expressible in one
    /// transaction: "which patch wins" has no answer this API ever promised, and picking one silently would
    /// make the outcome depend on an ordering the caller cannot see.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_batch_naming_one_row_twice_is_refused()
    {
        var world = await AuditedWorldAsync();
        var row = IdOf(await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, cancellationToken: Ct));

        var result = await world.Data.UpdateManyAsync(
            Orders,
            [
                new AlvoRowPatch(row, Payload("second")),
                new AlvoRowPatch(row, Payload("third")),
            ],
            world.Caller,
            cancellationToken: Ct);

        result.Succeeded.ShouldBeFalse("one row named twice is a batch nobody can judge");
        result.Refusals.Select(refusal => refusal.Index).ShouldBe(
            [1], customMessage: "the first occurrence stands; every repeat is named");

        var stored = await world.Data.GetAsync(Orders, row, world.Caller, Ct);
        stored.ShouldNotBeNull()["title"].ShouldBe("first", "a refused batch writes nothing");
    }

    /// <summary>A batch delete naming one row twice is refused, so its count can never exceed the rows it removed.</summary>
    /// <remarks>
    /// Without this the second delete of a row affects zero rows, and the batch still reports it as affected
    /// and emits a second <c>deleted</c> event for one physical row — a consumer that is not idempotent then
    /// processes the deletion twice.
    /// </remarks>
    [Fact]
    public async Task A_batch_delete_naming_one_row_twice_is_refused()
    {
        var world = await AuditedWorldAsync();
        var row = IdOf(await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, cancellationToken: Ct));

        var result = await world.Data.DeleteManyAsync(Orders, [row, row], world.Caller, cancellationToken: Ct);

        result.Succeeded.ShouldBeFalse();
        result.Refusals.Select(refusal => refusal.Index).ShouldBe([1]);
        (await CountAsync(world, Orders, world.Caller)).ShouldBe(1, "a refused batch removes nothing");
    }

    /// <summary>A batch is one request under one key, so replaying it writes no second set of rows.</summary>
    [Fact]
    public async Task A_replayed_batch_writes_no_second_set_of_rows()
    {
        var world = await AuditedWorldAsync();
        var token = TokenFor(Orders);

        var first = await world.Data.CreateManyAsync(
            Orders, [Payload("a"), Payload("b")], world.Caller, token, Ct);
        var replay = await world.Data.CreateManyAsync(
            Orders, [Payload("a"), Payload("b")], world.Caller, token, Ct);

        replay.Rows.Select(IdOf).ShouldBe(
            first.Rows.Select(IdOf), ignoreOrder: false,
            customMessage: "a replay answers the rows the first batch wrote, in the order it wrote them");
        (await CountAsync(world, Orders, world.Caller)).ShouldBe(2, "two rows, not four");
    }

    /// <summary>
    /// A caller who may write and not read gets their rows' <b>ids</b> back on a replay, and no field values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A replay re-reads under a freshly resolved <c>get</c>, and when <c>get</c> is denied outright there is
    /// nothing to read — so the answer is the ids alone. That is not a refusal: the retry must not be worse
    /// than the batch it replays, and the record's identity (key, tenant, acting user) already proves this
    /// caller wrote those rows. The ids are exactly what their own first response gave them.
    /// </para>
    /// <para>
    /// <b>The replay is narrower than the answer it replays, and that asymmetry is the fact.</b> A create
    /// returns the row it wrote, under the <c>create</c> decision — so the first answer here carries every
    /// field. The replay cannot do that: it is a <em>read</em>, and this caller has none, so it answers the
    /// ids alone. Both halves are asserted, or "the replay is id-only" would also pass on a path that
    /// answered id-only throughout.
    /// </para>
    /// <para>
    /// <b>Pinned because the branch had no driver.</b> The HTTP replay fact used to reach it by accident, on
    /// a key that happened to lack the read role; widening that key for an unrelated fact moved it off, and
    /// nothing else covered an id-only replay. It fails closed, so this is a proof gap rather than a hole —
    /// but it is a replay path in the security core.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_replayed_batch_by_a_caller_who_cannot_read_answers_ids_and_no_values()
    {
        var world = await WriteOnlyWorldAsync();
        var token = TokenFor(Dropbox);

        var first = await world.Data.CreateManyAsync(
            Dropbox, [Payload("a"), Payload("b")], world.Caller, token, Ct);
        var replay = await world.Data.CreateManyAsync(
            Dropbox, [Payload("a"), Payload("b")], world.Caller, token, Ct);

        first.Rows.ShouldAllBe(
            row => row.Values.Count > 1,
            "the FIRST answer carries the rows as written — a create returns what you wrote, under the "
            + "create decision. That is the contrast the replay below is measured against: the replay is "
            + "NARROWER than the answer it replays, and without this it could be narrow because the whole "
            + "path is.");

        replay.Rows.Select(IdOf).ShouldBe(
            first.Rows.Select(IdOf), ignoreOrder: false,
            customMessage: "the ids the caller's own first answer already gave them");
        foreach (var row in replay.Rows)
        {
            row.Values.Keys.ShouldBe(
                [AlvoManagedColumns.Id],
                customMessage: "a caller who may not read is told no field value, not even one they wrote");
        }
    }

    /// <summary>A delete removes every named row, or none of them.</summary>
    [Fact]
    public async Task A_batch_delete_removes_every_named_row_or_none()
    {
        var world = await AuditedWorldAsync();
        var first = IdOf(await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, cancellationToken: Ct));
        var second = IdOf(await world.Data.CreateAsync(Orders, Payload("second"), world.Caller, cancellationToken: Ct));

        var refused = await world.Data.DeleteManyAsync(
            Orders, [first, second, Guid.NewGuid()], world.Caller, cancellationToken: Ct);

        refused.Refusals.ShouldNotBeEmpty("an absent row refuses the batch");
        (await CountAsync(world, Orders, world.Caller)).ShouldBe(2, "and leaves the two real rows in place");

        var removed = await world.Data.DeleteManyAsync(Orders, [first, second], world.Caller, cancellationToken: Ct);

        removed.Succeeded.ShouldBeTrue();
        (await CountAsync(world, Orders, world.Caller)).ShouldBe(0);
    }

    /// <summary>
    /// A successful delete is distinguishable from a refusal, which it was not while the result carried only
    /// rows: a delete produces none, so both were an empty list.
    /// </summary>
    [Fact]
    public async Task A_successful_delete_is_not_an_empty_refusal()
    {
        var world = await AuditedWorldAsync();
        var first = IdOf(await world.Data.CreateAsync(Orders, Payload("a"), world.Caller, cancellationToken: Ct));
        var second = IdOf(await world.Data.CreateAsync(Orders, Payload("b"), world.Caller, cancellationToken: Ct));

        var removed = await world.Data.DeleteManyAsync(Orders, [first, second], world.Caller, cancellationToken: Ct);

        removed.Succeeded.ShouldBeTrue();
        removed.Affected.ShouldBe(2, "a count is what tells a delete apart from a refusal");
        removed.Rows.ShouldBeEmpty("a delete produces no rows");
    }

    /// <summary>
    /// A refusal carries no caller-supplied text. The port obligation <see cref="AlvoRowRefusal"/> states is
    /// the one a third-party provider is most likely to break, and it is the cheapest oracle a framework has:
    /// a message that echoed a field name would answer "does this entity declare one" a request at a time.
    /// </summary>
    [Fact]
    public async Task A_refusal_never_echoes_what_the_caller_sent()
    {
        var world = await AuditedWorldAsync();
        const string Marker = "zqmarkerqz";

        var result = await world.Data.CreateManyAsync(
            Orders,
            [new Dictionary<string, object?>(StringComparer.Ordinal) { [Marker] = Marker }],
            world.Caller,
            cancellationToken: Ct);

        result.Refusals.ShouldNotBeEmpty("an undeclared field must refuse the row, or this proves nothing");
        foreach (var refusal in result.Refusals)
        {
            refusal.Message.ShouldNotContain(Marker, Case.Insensitive);
            (refusal.FixSuggestion ?? string.Empty).ShouldNotContain(Marker, Case.Insensitive);
        }
    }

    /// <summary>
    /// An empty batch is refused rather than treated as a write of nothing — an intermediary that stripped
    /// the body would otherwise look exactly like a success.
    /// </summary>
    [Fact]
    public async Task An_empty_batch_is_refused_rather_than_answered_as_a_write_of_nothing()
    {
        var world = await AuditedWorldAsync();

        await Should.ThrowAsync<ArgumentException>(() =>
            world.Data.CreateManyAsync(Orders, [], world.Caller, cancellationToken: Ct));
        await Should.ThrowAsync<ArgumentException>(() =>
            world.Data.UpdateManyAsync(Orders, [], world.Caller, cancellationToken: Ct));
        await Should.ThrowAsync<ArgumentException>(() =>
            world.Data.DeleteManyAsync(Orders, [], world.Caller, cancellationToken: Ct));
    }

    /// <summary>
    /// A caller no policy admits at all is refused by a throw rather than by a per-row report. The decision
    /// is made before any row is looked at, so reporting it per row would disclose how many of the rows they
    /// sent were real.
    /// </summary>
    [Fact]
    public async Task A_caller_the_policy_denies_outright_is_refused_before_any_row_is_judged()
    {
        var world = await WriteOnlyWorldAsync();

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.UpdateManyAsync(
            Dropbox, [new AlvoRowPatch(Guid.NewGuid(), Payload("x"))], world.Caller, cancellationToken: Ct));
    }

    /// <summary>How many rows of <paramref name="entity"/> this caller can see.</summary>
    /// <remarks>
    /// Sound as a count of the whole entity only on the permissive, global fixtures — see this type's own
    /// remarks for why, and for what the tenant-scoped fixture asserts instead.
    /// </remarks>
    /// <param name="world">The running store.</param>
    /// <param name="entity">The entity to count.</param>
    /// <param name="caller">The caller to count as.</param>
    private static async Task<int> CountAsync(World world, string entity, AlvoContext caller) =>
        (await world.Data.QueryAsync(new AlvoQuery { Entity = entity }, caller, Ct)).Items.Count;

    /// <summary>A ticket owned by nobody, which the owner rule refuses for every caller.</summary>
    /// <param name="title">The row's title.</param>
    private static Dictionary<string, object?> UnownedPayload(string title) =>
        new(StringComparer.Ordinal) { ["title"] = title, ["owner_id"] = Guid.NewGuid() };

    /// <summary>A ticket claiming another caller as its owner, which the check refuses for this one.</summary>
    /// <param name="title">The row's title.</param>
    /// <param name="other">The caller the row would be owned by.</param>
    private static Dictionary<string, object?> ForeignPayload(string title, AlvoContext other) =>
        new(StringComparer.Ordinal) { ["title"] = title, ["owner_id"] = other.User.Value };
}
