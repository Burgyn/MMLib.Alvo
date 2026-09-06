using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// Create-or-replace as a rule of the <b>port</b>, proved over every <see cref="IAlvoData"/> implementation
/// this suite runs against: the path's id is the row's identity, the branch is chosen from a policy-scoped
/// read, and the caller's <c>WITH CHECK</c> judges the candidate on <em>both</em> branches.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this suite exists to catch is "checks the update branch and lets the create branch
/// through."</b> An upsert is the one shape where a plausible implementation — read, and if nothing is
/// there just insert — skips the rule entirely on half its inputs, and it does so silently, because the
/// replace branch inherits <c>UpdateAsync</c>'s existing check and looks correct in review. So one rule is
/// asserted against both branches, from the same fixture, in two facts that differ only in whether the row
/// already existed.
/// </para>
/// <para>
/// <b>The refusal facts carry controls, because a refusal is the easiest thing to pass vacuously.</b> A
/// fact that asserts "this throws" passes just as well when the route is broken, when the fixture is
/// misconfigured, or when the caller was never permitted anything. Each refusal here is paired with the
/// same call that should succeed — same world, same entity, same branch — so a refusal that refuses
/// everything fails the pair.
/// </para>
/// </remarks>
public abstract class AlvoDataReplaceTests : AlvoDataFixture
{
    /// <summary>A replace on an id no row holds creates that row, under the id the caller named.</summary>
    /// <remarks>
    /// The half of #105 that made it a decision rather than a route: the store no longer mints every key.
    /// It is asserted on the returned row rather than a read-back, because the returned row is what the
    /// endpoint turns into its <c>Location</c> header.
    /// </remarks>
    [Fact]
    public async Task Replacing_an_absent_row_creates_it_under_the_id_the_caller_named()
    {
        var world = await AuditedWorldAsync();
        var id = Guid.NewGuid();

        var result = await world.Data.ReplaceAsync(Orders, id, Payload("Fix the boiler"), world.Caller, cancellationToken: Ct);

        result.Created.ShouldBeTrue();
        IdOf(result.Row).ShouldBe(id, "the path's id is the row's id");
    }

    /// <summary>A replace on an id a visible row holds replaces that row rather than creating a second.</summary>
    [Fact]
    public async Task Replacing_a_visible_row_replaces_it()
    {
        var world = await AuditedWorldAsync();
        var existing = await world.Data.CreateAsync(Orders, Payload("First"), world.Caller, cancellationToken: Ct);

        var result = await world.Data.ReplaceAsync(
            Orders, IdOf(existing), Payload("Second"), world.Caller, cancellationToken: Ct);

        result.Created.ShouldBeFalse();
        result.Row["title"].ShouldBe("Second");
        IdOf(result.Row).ShouldBe(IdOf(existing), "a replaced row is the same row, not a new one");
    }

    /// <summary>The caller's <c>WITH CHECK</c> judges the candidate on the create branch.</summary>
    /// <remarks>
    /// <b>This is the fact the whole feature is held up by.</b> The owner-scoped fixture's rule is
    /// <c>owner_id == @user.id</c>, so a candidate naming someone else as owner is refused — and on a
    /// create branch there is no stored row to inherit a refusal from, so passing this can only mean the
    /// rule really ran against the candidate.
    /// </remarks>
    [Fact]
    public async Task The_write_check_refuses_the_candidate_on_the_create_branch()
    {
        var world = await OwnedWorldAsync();

        await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.ReplaceAsync(
                Tickets, Guid.NewGuid(), OwnedPayload("Not mine", world.Alice), world.Bob, cancellationToken: Ct));
    }

    /// <summary>And the same rule refuses the candidate on the replace branch.</summary>
    /// <remarks>
    /// The control for the fact above, and a claim in its own right: a caller may not use a replace to hand
    /// their own row to somebody else. The pre-image passes <c>USING</c> — it really is Bob's row — so the
    /// only thing that can refuse this is <c>WITH CHECK</c> over the post-image.
    /// </remarks>
    [Fact]
    public async Task The_write_check_refuses_the_candidate_on_the_replace_branch()
    {
        var world = await OwnedWorldAsync();
        var mine = await world.Data.CreateAsync(
            Tickets, OwnedPayload("Mine", world.Bob), world.Bob, cancellationToken: Ct);

        await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.ReplaceAsync(
                Tickets, IdOf(mine), OwnedPayload("Handed to Alice", world.Alice), world.Bob, cancellationToken: Ct));
    }

    /// <summary>The control both refusals need: the same rule admits a candidate that satisfies it.</summary>
    /// <remarks>
    /// Without this, a fixture that refused every write — a mis-compiled rule, a caller with no roles —
    /// would make both refusal facts pass while proving nothing at all.
    /// </remarks>
    [Fact]
    public async Task The_write_check_admits_a_candidate_that_satisfies_it_on_both_branches()
    {
        var world = await OwnedWorldAsync();

        var created = await world.Data.ReplaceAsync(
            Tickets, Guid.NewGuid(), OwnedPayload("Mine", world.Bob), world.Bob, cancellationToken: Ct);
        var replaced = await world.Data.ReplaceAsync(
            Tickets, IdOf(created.Row), OwnedPayload("Still mine", world.Bob), world.Bob, cancellationToken: Ct);

        created.Created.ShouldBeTrue();
        replaced.Created.ShouldBeFalse();
        replaced.Row["title"].ShouldBe("Still mine");
    }

    /// <summary>An id held by a row in another tenant is refused, and that row is left exactly as it was.</summary>
    /// <remarks>
    /// <b>The design records the residual disclosure this carries (§3): the caller learns the id is taken.</b>
    /// What must never happen is either of the other two outcomes — writing over a row the caller's
    /// <c>USING</c> excludes, or reporting success — and those are what this asserts. The other tenant's row
    /// is read back <b>as that tenant</b> rather than counted, because a count cannot tell an overwrite from
    /// a no-op.
    /// </remarks>
    [Fact]
    public async Task Replacing_a_row_in_another_tenant_is_refused_and_leaves_it_untouched()
    {
        var world = await TenantedWorldAsync();
        var theirs = await world.Data.CreateAsync(
            Invoices, TenantPayload("Theirs", world.Globex), world.GlobexCaller, cancellationToken: Ct);

        await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => world.Data.ReplaceAsync(
                Invoices, IdOf(theirs), Payload("Mine now"), world.AcmeCaller, cancellationToken: Ct));

        var stillTheirs = await world.Data.GetAsync(
            Invoices, IdOf(theirs), world.GlobexCaller, cancellationToken: Ct);
        stillTheirs!["title"].ShouldBe("Theirs");
    }

    /// <summary>A create on a tenant-scoped entity lands in the caller's own tenant, unasked.</summary>
    /// <remarks>
    /// The other half of the <c>tenant_id</c> decision (§5). The caller may not name a tenant on this route,
    /// so the framework names the only one the scope would have accepted — without which the candidate would
    /// carry no tenant at all and its own scope would refuse it, and the route would not work on any scoped
    /// entity.
    /// </remarks>
    [Fact]
    public async Task Creating_through_a_replace_lands_the_row_in_the_caller_s_own_tenant()
    {
        var world = await TenantedWorldAsync();
        var id = Guid.NewGuid();

        await world.Data.ReplaceAsync(Invoices, id, Payload("Mine"), world.AcmeCaller, cancellationToken: Ct);

        var mine = await world.Data.GetAsync(Invoices, id, world.AcmeCaller, cancellationToken: Ct);
        mine.ShouldNotBeNull("the row landed in the caller's own tenant, so the caller can read it");

        var theirs = await world.Data.GetAsync(Invoices, id, world.GlobexCaller, cancellationToken: Ct);
        theirs.ShouldBeNull("and in no other");
    }

    /// <summary>A nullable field the replacement omits does not keep its stored value.</summary>
    /// <remarks>
    /// <b>This is what separates a replacement from a patch</b>, and it is asserted from a row that already
    /// has the field set — a starting state where the field was empty cannot tell the two apart, because
    /// both leave it empty.
    /// </remarks>
    [Fact]
    public async Task A_nullable_field_the_replacement_omits_becomes_null()
    {
        var world = await ExtraWorldAsync();
        var existing = await world.Data.CreateAsync(
            Extras, ExtraPayload("Ring the bell", 7), world.Caller, cancellationToken: Ct);

        var replaced = await world.Data.ReplaceAsync(
            Extras, IdOf(existing), ExtraOnlyPayload(7), world.Caller, cancellationToken: Ct);

        replaced.Row["title"].ShouldBeNull("a replacement replaces the row; it does not merge into it");
    }

    /// <summary>A required field the replacement omits is refused, naming the field.</summary>
    /// <remarks>
    /// A body that cannot express the whole row is a caller error rather than a partial write — accepting it
    /// and keeping the stored value is exactly the merge the fact above rules out, arriving through the one
    /// field where it cannot be undone.
    /// </remarks>
    [Fact]
    public async Task A_required_field_the_replacement_omits_is_refused()
    {
        var world = await ExtraWorldAsync();
        var existing = await world.Data.CreateAsync(
            Extras, ExtraPayload("First", 7), world.Caller, cancellationToken: Ct);

        var refusal = await Should.ThrowAsync<ArgumentException>(
            () => world.Data.ReplaceAsync(
                Extras, IdOf(existing), Payload("Second"), world.Caller, cancellationToken: Ct));

        refusal.Message.ShouldContain(ExtraField);
    }

    /// <summary>The same rule refuses a create that cannot express the row either.</summary>
    /// <remarks>
    /// The control the refusal above needs, and a claim of its own: the branch does not decide whether a
    /// body has to be complete. If only the replace branch checked, a caller could write a half row simply
    /// by naming an unused id.
    /// </remarks>
    [Fact]
    public async Task A_required_field_is_refused_on_the_create_branch_too()
    {
        var world = await ExtraWorldAsync();

        await Should.ThrowAsync<ArgumentException>(
            () => world.Data.ReplaceAsync(
                Extras, Guid.NewGuid(), Payload("No rank"), world.Caller, cancellationToken: Ct));
    }

    /// <summary><c>created_at</c> and <c>created_by</c> survive a replacement, because it is the same row.</summary>
    /// <remarks>
    /// The boundary of the rule above: a replacement replaces the <em>caller's</em> fields. A framework
    /// column nulled by a replacement would make a row's own history depend on how it was last written.
    /// </remarks>
    [Fact]
    public async Task The_creation_stamp_survives_a_replacement()
    {
        var world = await AuditedWorldAsync();
        var existing = await world.Data.CreateAsync(Orders, Payload("First"), world.Caller, cancellationToken: Ct);

        var replaced = await world.Data.ReplaceAsync(
            Orders, IdOf(existing), Payload("Second"), world.Caller, cancellationToken: Ct);

        replaced.Row[AlvoManagedColumns.CreatedAt].ShouldBe(existing[AlvoManagedColumns.CreatedAt]);
        replaced.Row[AlvoManagedColumns.CreatedBy].ShouldBe(existing[AlvoManagedColumns.CreatedBy]);
    }

    /// <summary>The same replacement applied to two differently-populated rows leaves one row shape.</summary>
    /// <remarks>
    /// <b>The spec's own acceptance criterion, asserted the only way that proves anything</b> — from two
    /// starting states. Read literally, "the same PUT twice leaves the same state" is satisfied by a merge
    /// too, since a patch applied twice is also idempotent; what a merge cannot do is bring two different
    /// rows to the same place.
    /// </remarks>
    [Fact]
    public async Task The_same_replacement_from_two_starting_states_lands_on_one_row()
    {
        var world = await ExtraWorldAsync();
        var sparse = await world.Data.CreateAsync(Extras, ExtraOnlyPayload(1), world.Caller, cancellationToken: Ct);
        var full = await world.Data.CreateAsync(Extras, ExtraPayload("noisy", 1), world.Caller, cancellationToken: Ct);

        var one = await world.Data.ReplaceAsync(
            Extras, IdOf(sparse), ExtraOnlyPayload(2), world.Caller, cancellationToken: Ct);
        var two = await world.Data.ReplaceAsync(
            Extras, IdOf(full), ExtraOnlyPayload(2), world.Caller, cancellationToken: Ct);

        one.Row["title"].ShouldBe(two.Row["title"]);
        one.Row[ExtraField].ShouldBe(two.Row[ExtraField]);
    }

    /// <summary>A replay reports the state a previous request left, never an act of creation.</summary>
    /// <remarks>
    /// <b><c>201</c> reports that this request created the row, and a replay performs no act at all.</b> The
    /// idempotency record carries row ids, not the branch that wrote them, and it deliberately gains nothing
    /// to carry it: storing the branch grows the record for one header, and inferring it from
    /// <c>created_at == updated_at</c> is a guess a row replaced in the instant it was created defeats.
    /// </remarks>
    [Fact]
    public async Task A_replayed_replacement_answers_that_it_created_nothing()
    {
        var world = await AuditedWorldAsync();
        var id = Guid.NewGuid();
        var token = TokenFor(Orders);

        var first = await world.Data.ReplaceAsync(
            Orders, id, Payload("Once"), world.Caller, idempotency: token, cancellationToken: Ct);
        var replay = await world.Data.ReplaceAsync(
            Orders, id, Payload("Once"), world.Caller, idempotency: token, cancellationToken: Ct);

        first.Created.ShouldBeTrue("the first request really did create the row");
        replay.Created.ShouldBeFalse("a replay reports the state the first request left, not an act");
        IdOf(replay.Row).ShouldBe(id);
    }

    /// <summary>And it writes nothing the second time.</summary>
    /// <remarks>
    /// The control the fact above needs: an implementation that simply replaced again would also answer
    /// <c>Created: false</c> and look correct, while spending a write and moving the row's version.
    /// </remarks>
    [Fact]
    public async Task A_replayed_replacement_does_not_write_again()
    {
        var world = await AuditedWorldAsync();
        var id = Guid.NewGuid();
        var token = TokenFor(Orders);

        var first = await world.Data.ReplaceAsync(
            Orders, id, Payload("Once"), world.Caller, idempotency: token, cancellationToken: Ct);
        var replay = await world.Data.ReplaceAsync(
            Orders, id, Payload("Once"), world.Caller, idempotency: token, cancellationToken: Ct);

        VersionOf(replay.Row).ShouldBe(VersionOf(first.Row), "a replay performs no write, so nothing moved");
    }

    /// <summary>An <c>If-Match</c> on the create branch cannot match anything.</summary>
    /// <remarks>
    /// Naming a version is asserting the row exists, so a precondition supplied for an id nothing holds is
    /// refused rather than quietly ignored — a caller guarding against a lost update must not have that
    /// guard silently drop away on the branch where it matters most.
    /// </remarks>
    [Fact]
    public async Task An_if_match_on_an_absent_row_fails_its_precondition()
    {
        var world = await AuditedWorldAsync();
        var other = await world.Data.CreateAsync(Orders, Payload("Something else"), world.Caller, cancellationToken: Ct);

        await Should.ThrowAsync<AlvoPreconditionFailedException>(
            () => world.Data.ReplaceAsync(
                Orders, Guid.NewGuid(), Payload("Nope"), world.Caller,
                precondition: new AlvoPrecondition(VersionOf(other)), cancellationToken: Ct));
    }

    /// <summary>An <c>If-Match</c> carrying the row's own version replaces it.</summary>
    /// <remarks>
    /// The control the fact above needs, and the ordinary case: without it, a precondition that refused
    /// everything would make the refusal pass while proving nothing.
    /// </remarks>
    [Fact]
    public async Task An_if_match_carrying_the_stored_version_replaces_the_row()
    {
        var world = await AuditedWorldAsync();
        var existing = await world.Data.CreateAsync(Orders, Payload("First"), world.Caller, cancellationToken: Ct);

        var replaced = await world.Data.ReplaceAsync(
            Orders, IdOf(existing), Payload("Second"), world.Caller,
            precondition: new AlvoPrecondition(VersionOf(existing)), cancellationToken: Ct);

        replaced.Row["title"].ShouldBe("Second");
    }

    /// <summary><c>tenant_id</c> is refused on this route whether or not the row already exists.</summary>
    /// <remarks>
    /// <b><c>tenant_id</c> is caller-writable on a create and refused on an update</b>, so a route that
    /// asked that question per branch would answer "does this row exist?" with "was my <c>tenant_id</c>
    /// refused?" — an existence oracle decided from the payload alone, before any row is read, and one a
    /// caller can trigger deliberately. The payload guard is therefore told <c>isUpdate: true</c>
    /// unconditionally, and this fact is what keeps that from drifting back.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Tenant_id_in_the_payload_is_refused_whether_or_not_the_row_exists(bool rowExists)
    {
        var world = await TenantedWorldAsync();
        var id = Guid.NewGuid();
        if (rowExists)
        {
            var existing = await world.Data.CreateAsync(
                Invoices, TenantPayload("Here", world.Acme), world.AcmeCaller, cancellationToken: Ct);
            id = IdOf(existing);
        }

        await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.ReplaceAsync(
                Invoices, id, TenantPayload("Moved", world.Acme), world.AcmeCaller, cancellationToken: Ct));
    }
}
