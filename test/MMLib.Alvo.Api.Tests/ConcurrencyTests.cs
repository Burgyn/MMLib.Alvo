using MMLib.Alvo.Descriptor;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Optimistic concurrency over HTTP: the <c>ETag</c> a read hands out, the <c>If-Match</c> a write carries,
/// and the <c>412</c> that stops the second of two callers from overwriting the first.
/// </summary>
/// <remarks>
/// <para>
/// §2.1 of the domain analysis is blunt about why this exists — without it "clients overwrite each other's
/// data" — and a lost update is invisible to every test that does not deliberately look for one. The two
/// facts that actually prove the mechanism live in <see cref="DataApiEngineTests"/> and run on both engines,
/// because they depend on what the engine does to a stored value: the race itself
/// (<see cref="DataApiEngineTests.A_lost_update_is_prevented_when_two_callers_read_then_both_write"/>) and the
/// round trip a tag has to survive
/// (<see cref="DataApiEngineTests.An_etag_from_a_get_is_accepted_verbatim_by_a_following_update"/>).
/// </para>
/// <para>
/// Everything here is the request-layer half: a fact about preconditions that never reaches a comparison the
/// engine performs — a malformed header, a precedence rule, a refusal's wording — is engine-insensitive, so
/// running it a second time on PostgreSQL would cost a container and prove nothing new.
/// </para>
/// <para>
/// <c>owners</c> is the audited entity throughout and <c>inspections</c> the non-audited one; that split is
/// the descriptor's, not this file's, and <see cref="MMLib.Alvo.Schema.AlvoManagedColumns.VersionColumn"/> is
/// the only thing that reads it.
/// </para>
/// </remarks>
public sealed class ConcurrencyTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>
    /// A create answers 201 with the new row's <c>Location</c> <b>and</b> its entity tag, so the caller's
    /// first conditional write needs no read first.
    /// </summary>
    /// <remarks>
    /// The 201's tag is not a convenience. Without it a caller who wants to write conditionally has to GET
    /// the row it just created, and the window between the create and that read is exactly the window the
    /// precondition exists to close. The write already re-read the row (PR2), so the stored version is at
    /// hand either way.
    /// </remarks>
    [Fact]
    public async Task A_create_returns_201_with_a_location_header_and_an_etag()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: new JsonObject { ["name"] = "Created Ltd" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        var id = (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
        response.Headers.Location!.ToString().ShouldBe(Owner(id));
        response.ETagOf().ShouldBe(
            TagOf(await VersionOfAsync(world, id)), "a 201's tag must denote the version the create stored");
    }

    /// <summary>
    /// The happy path: a write carrying the current tag is served, and the response hands back the tag for
    /// the version it just created, so a caller can chain conditional writes without re-reading.
    /// </summary>
    [Fact]
    public async Task An_update_with_the_current_etag_succeeds_and_returns_the_new_one()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var owner = await SeedOwnerAsync(world, "Before");

        using var response = await world.SendAsync(
            HttpMethod.Patch, Owner(owner.Id), _admin, body: Rename("After"), headers: IfMatch(owner.ETag));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        (await NameOfAsync(world, owner.Id)).ShouldBe("After");
        response.ETagOf().ShouldNotBe(owner.ETag, "a write advances the version, so it must hand back a new tag");
        response.ETagOf().ShouldBe(
            TagOf(await VersionOfAsync(world, owner.Id)), "and the new tag must be the version now stored");
    }

    /// <summary>
    /// A write carrying a version the row no longer has is refused with 412, and the row keeps the value the
    /// writer who got there first gave it.
    /// </summary>
    /// <remarks>
    /// "Changes nothing" is the half a status code cannot prove. An implementation that wrote the row and
    /// <em>then</em> noticed the stale version would answer 412 too, and the caller would be told their write
    /// was refused while it had already landed.
    /// </remarks>
    [Fact]
    public async Task An_update_with_a_stale_etag_is_412_and_changes_nothing()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var owner = await SeedOwnerAsync(world, "Original");
        await AdvanceAsync(world, owner.Id, "Winner");

        using var response = await world.SendAsync(
            HttpMethod.Patch, Owner(owner.Id), _admin, body: Rename("Loser"), headers: IfMatch(owner.ETag));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.PreconditionFailed);
        (await NameOfAsync(world, owner.Id)).ShouldBe("Winner", "a refused write must not have landed");
    }

    /// <summary>
    /// The same for a delete, where the cost of getting it wrong is the row itself: a stale precondition is
    /// refused and the row is still there.
    /// </summary>
    /// <remarks>
    /// Its own fact rather than a second assertion on the update, because <c>DeleteAsync</c> reaches the
    /// precondition down a different path in the port (no post-image, no <c>WITH CHECK</c>) and the HTTP verb
    /// carries no body at all — so a wiring that reads <c>If-Match</c> only where a body is read passes the
    /// update fact and fails this one.
    /// </remarks>
    [Fact]
    public async Task A_delete_with_a_stale_etag_is_412_and_the_row_survives()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var owner = await SeedOwnerAsync(world, "Survivor");
        await AdvanceAsync(world, owner.Id, "Survivor renamed");

        using var response = await world.SendAsync(
            HttpMethod.Delete, Owner(owner.Id), _admin, headers: IfMatch(owner.ETag));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.PreconditionFailed);
        (await NameOfAsync(world, owner.Id)).ShouldBe("Survivor renamed", "a refused delete must not have landed");
    }

    /// <summary>
    /// <c>If-Match: *</c> asks only that the row still exist, and the port's own not-found already answers
    /// that — so it succeeds on a row that is there and is a 404 on one that is not.
    /// </summary>
    /// <remarks>
    /// The 404 half is what makes this more than "the header was ignored": <c>*</c> must not become a 412
    /// (which would report a version mismatch for a row that has no version to mismatch), and it must not be
    /// handed to the tag parser either, which would refuse it as malformed.
    /// </remarks>
    [Fact]
    public async Task If_match_star_succeeds_when_the_row_exists()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var owner = await SeedOwnerAsync(world, "Present");

        using var present = await world.SendAsync(
            HttpMethod.Patch, Owner(owner.Id), _admin, body: Rename("Still present"), headers: IfMatch("*"));
        using var absent = await world.SendAsync(
            HttpMethod.Patch, Owner(Guid.NewGuid()), _admin, body: Rename("Nobody"), headers: IfMatch("*"));

        present.StatusCode.ShouldBe(HttpStatusCode.OK, await present.ReadTextAsync());
        (await NameOfAsync(world, owner.Id)).ShouldBe("Still present");
        absent.StatusCode.ShouldBe(HttpStatusCode.NotFound, "'*' is a claim about existence, not about a version");
    }

    /// <summary>
    /// An <c>If-Match</c> against an entity that keeps no version of a row is 412 with a fix suggestion
    /// naming <c>audit: true</c> — never a 200 that silently ignored it.
    /// </summary>
    /// <remarks>
    /// Silently ignoring it is the worst outcome available: the caller sent a precondition, read a 200, and
    /// overwrote a concurrent writer believing they had prevented exactly that. The row is a real, existing
    /// inspection, so the 412 cannot be confused with a not-found, and the baseline assertion below shows the
    /// same request without the header is served — which is what makes the refusal attributable to the header.
    /// </remarks>
    [Fact]
    public async Task If_match_against_a_non_audited_entity_is_412_with_a_fix_suggestion()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var inspection = await SeedInspectionAsync(world);
        var path = $"/api/inspections/{inspection}";

        using var conditional = await world.SendAsync(
            HttpMethod.Patch, path, _admin, body: Note("conditional"), headers: IfMatch(TagOf(DateTimeOffset.UtcNow)));
        using var unconditional = await world.SendAsync(HttpMethod.Patch, path, _admin, body: Note("plain"));

        unconditional.StatusCode.ShouldBe(
            HttpStatusCode.OK, "the same write without the header must succeed, or the 412 proves nothing");
        conditional.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await conditional.ReadProblemDetailAsync()).ShouldContain(
            "audit: true", Case.Sensitive, "§0 principle 4: the refusal must name the descriptor change that fixes it");
    }

    /// <summary>
    /// A header this API cannot turn into one row version is 412, not 422, and nothing is written — because
    /// the caller's intent, "only if unchanged", must never be reinterpreted as "unconditionally".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second request is what pins the status: it carries a body that would earn a 422 on its own, so a
    /// wiring that read <c>If-Match</c> after validating the body would answer 422 and fail here. The first
    /// carries a valid body, so it isolates the header.
    /// </para>
    /// <para>
    /// Each spelling is a different way to get it wrong: prose that is not a tag at all, the digits without
    /// their quotes, and a tick count past <c>DateTimeOffset.MaxValue</c> — the last one measures that the
    /// range is checked before <c>new DateTimeOffset(ticks, …)</c> is reached, since the constructor's own
    /// answer is an exception a caller-controlled header must not be able to raise.
    /// </para>
    /// <para>
    /// None of these spellings names a version the row has, so none of them could succeed even if the header
    /// were compared. The two spellings that <em>could</em> — a weak form of the current tag, and the current
    /// tag inside a list — are their own fact below, because only a live tag can tell "refused as
    /// uncomparable" from "compared and found stale".
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("not-a-tag")]
    [InlineData("638000000000000000")]
    [InlineData("\"9223372036854775807\"")]
    public async Task A_malformed_if_match_is_412_not_422_and_never_writes(string header)
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var owner = await SeedOwnerAsync(world, "Untouched");

        using var valid = await world.SendAsync(
            HttpMethod.Patch, Owner(owner.Id), _admin, body: Rename("Written"), headers: IfMatch(header));
        using var invalid = await world.SendAsync(
            HttpMethod.Patch, Owner(owner.Id), _admin, body: Rename(new string('n', 200)), headers: IfMatch(header));

        valid.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await valid.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.PreconditionFailed);
        invalid.StatusCode.ShouldBe(
            HttpStatusCode.PreconditionFailed, "a failed precondition outranks whatever the body says");
        (await NameOfAsync(world, owner.Id)).ShouldBe("Untouched", "a refused precondition must never write");
    }

    /// <summary>
    /// An <c>If-Match</c> this API cannot compare is refused <b>even when it names the version the row
    /// actually has</b> — a weak form of the current tag, and the current tag inside a two-member list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fact the theory above could not be: every static spelling there names a version the row
    /// does not have, so a wiring that quietly compared the header would also answer 412 and the fact would
    /// pass for the wrong reason. That is not hypothetical — it is what happened here.
    /// <c>Microsoft.Net.Http.Headers.EntityTagHeaderValue</c> lifts <c>W/</c> into its own flag, so
    /// <c>W/"&lt;current&gt;"</c> reached the tag parser as a bare strong tag, was accepted as a version, and
    /// the request's outcome turned on whether that version happened to be current. With a fabricated version
    /// the defect was invisible; with the live one the request answers 200.
    /// </para>
    /// <para>
    /// The list case is the same trap: RFC 9110 §13.1.1 lets <c>If-Match</c> carry several tags and succeed if
    /// any matches, which <c>AlvoPrecondition</c>'s single version cannot express — so an implementation that
    /// simply took the first tag would serve this request, and one that refuses the ambiguity must not.
    /// </para>
    /// <para>
    /// The third is the padded spelling. Strong comparison is octet-for-octet, so <c>"0638…"</c> is not the tag
    /// this API minted even though it decodes to the same instant — and only the current version can show that,
    /// since a padded stale tag earns a 412 either way. It is what makes the encoder and the parser provably
    /// exact inverses rather than approximately so.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_if_match_this_api_cannot_compare_is_refused_even_when_it_names_the_current_version()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var owner = await SeedOwnerAsync(world, "Uncomparable");
        var padded = $"\"0{owner.ETag.Trim('"')}\"";

        using var weak = await world.SendAsync(
            HttpMethod.Patch, Owner(owner.Id), _admin, body: Rename("Weakly"), headers: IfMatch($"W/{owner.ETag}"));
        using var listed = await world.SendAsync(
            HttpMethod.Patch,
            Owner(owner.Id),
            _admin,
            body: Rename("Listed"),
            headers: IfMatch($"{owner.ETag}, \"638000000000000001\""));
        using var noncanonical = await world.SendAsync(
            HttpMethod.Patch, Owner(owner.Id), _admin, body: Rename("Padded"), headers: IfMatch(padded));

        weak.StatusCode.ShouldBe(
            HttpStatusCode.PreconditionFailed, "RFC 9110 §13.1.1's strong comparison can never match a weak tag");
        listed.StatusCode.ShouldBe(
            HttpStatusCode.PreconditionFailed, "a disjunction of versions cannot be expressed on the port's channel");
        noncanonical.StatusCode.ShouldBe(
            HttpStatusCode.PreconditionFailed,
            $"'{padded}' decodes to the current version but is not the tag this API minted, and strong "
            + "comparison is octet-for-octet");
        (await NameOfAsync(world, owner.Id)).ShouldBe("Uncomparable", "none of the refused writes may have landed");
    }

    /// <summary>
    /// A conditional read whose <c>If-None-Match</c> already names the current version is answered 304 with
    /// no body — and with the tag repeated, so the client does not have to re-read the row to keep one.
    /// </summary>
    /// <remarks>
    /// The 304 is measured against a body of exactly zero bytes rather than against "no <c>items</c>": a 304
    /// carrying a body is a protocol error, and a reader that tolerates one would let the saved round trip be
    /// no saving at all.
    /// </remarks>
    [Fact]
    public async Task If_none_match_with_the_current_etag_is_304_with_no_body()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var owner = await SeedOwnerAsync(world, "Cached");

        using var held = await world.SendAsync(
            HttpMethod.Get, Owner(owner.Id), _admin, headers: IfNoneMatch(owner.ETag));
        using var stale = await world.SendAsync(
            HttpMethod.Get, Owner(owner.Id), _admin, headers: IfNoneMatch(TagOf(DateTimeOffset.UnixEpoch)));

        held.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await held.ReadTextAsync()).ShouldBeEmpty("a 304 carries no body at all");
        held.ETagOf().ShouldBe(owner.ETag, "RFC 9110 §15.4.5: a 304 carries the tag it was generated from");
        stale.StatusCode.ShouldBe(
            HttpStatusCode.OK, "a tag the row does not have must produce the representation, not a 304");
    }

    /// <summary>
    /// An <c>If-None-Match</c> on a <em>write</em> is refused rather than ignored: it is a precondition the
    /// port's single-version channel cannot express, and passing the write through would be the silently
    /// ignored precondition the whole feature exists to prevent.
    /// </summary>
    /// <remarks>
    /// Beyond the brief, and deliberately. RFC 9110 §13.1.2 makes <c>If-None-Match</c> a precondition on any
    /// method, so a caller who sends one on a PATCH and reads a 200 has been told their condition held. The
    /// only two honest answers are to evaluate it or to refuse it, and <c>AlvoPrecondition</c> expresses only
    /// the positive form. The deviation this creates — the spec would let a <em>non-matching</em>
    /// <c>If-None-Match</c> simply succeed — is labelled on <c>EnsureNoIfNoneMatch</c> rather than left to be
    /// discovered from a 412.
    /// </remarks>
    [Fact]
    public async Task An_if_none_match_on_a_write_is_refused_rather_than_ignored()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var owner = await SeedOwnerAsync(world, "Unwritten");

        using var patched = await world.SendAsync(
            HttpMethod.Patch, Owner(owner.Id), _admin, body: Rename("Written"), headers: IfNoneMatch("*"));
        using var deleted = await world.SendAsync(
            HttpMethod.Delete, Owner(owner.Id), _admin, headers: IfNoneMatch(owner.ETag));

        patched.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        deleted.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await patched.ReadProblemDetailAsync()).ShouldContain("If-Match", Case.Sensitive);
        (await NameOfAsync(world, owner.Id)).ShouldBe("Unwritten", "neither refused write may have landed");
    }

    /// <summary>
    /// With <b>both</b> precondition headers present, <c>If-Match</c> is evaluated first — the order RFC 9110
    /// §13.2.2 fixes — and the refusal never tells a caller to send a header they already sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both orderings answer 412, so neither the status nor "the write did not land" can tell them apart; only
    /// <em>which</em> problem is reported can. So the first request pairs an unusable <c>If-Match</c> with an
    /// <c>If-None-Match</c> and requires the <c>If-Match</c> diagnosis: an implementation that refused
    /// <c>If-None-Match</c> first reports the less specific of the two and passes every other fact in this file.
    /// </para>
    /// <para>
    /// The second request is the message wart. With a <em>usable</em> <c>If-Match</c> alongside, the old wording
    /// answered "Send 'If-Match' with the 'ETag' a previous response returned" — advice to do the thing the
    /// caller had just done, which reads as a contradiction and sends an agent in a circle.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task With_both_headers_the_if_match_is_evaluated_first_and_the_advice_is_not_circular()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var owner = await SeedOwnerAsync(world, "Both");
        var both = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["If-Match"] = "not-a-tag",
            ["If-None-Match"] = "*",
        };
        var usable = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["If-Match"] = owner.ETag,
            ["If-None-Match"] = "*",
        };

        using var unusableFirst = await world.SendAsync(
            HttpMethod.Patch, Owner(owner.Id), _admin, body: Rename("Neither"), headers: both);
        using var usableAlongside = await world.SendAsync(
            HttpMethod.Patch, Owner(owner.Id), _admin, body: Rename("Neither"), headers: usable);

        (await unusableFirst.ReadProblemDetailAsync()).ShouldContain(
            "The 'If-Match' header is not one entity tag",
            Case.Sensitive,
            "RFC 9110 §13.2.2 evaluates If-Match first, so its problem is the one reported");
        (await usableAlongside.ReadProblemDetailAsync()).ShouldContain(
            "keep the 'If-Match' you already sent",
            Case.Sensitive,
            "a fix suggestion must never tell a caller to send the header they just sent");
        (await NameOfAsync(world, owner.Id)).ShouldBe("Both", "neither refused write may have landed");
    }

    /// <summary>
    /// Both descriptors that could switch this whole feature off silently are refused at <b>apply</b>: an
    /// audited entity declaring its own <c>updated_at</c>, whether it masks the column or retypes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both were reachable, and the masked one is the milder. The schema mapper used to inject a
    /// framework-managed column only when the entity did not already declare that name, so an author's own
    /// <c>updated_at</c> won. Declared <c>hidden</c>, the mask drops the key from every returned record, so
    /// <c>RowVersionETag.For</c> finds no version, no <c>ETag</c> is minted, the caller has nothing to send as
    /// <c>If-Match</c>, and <b>every other fact in this file would still pass while the entity had no
    /// lost-update protection at all</b>. Declared as <c>{"type":"string"}</c> it is worse: apply succeeded and
    /// then every create answered 422 with an internal parameter name in the body, because the audit stamp
    /// writes a timestamp into a column the schema calls text.
    /// </para>
    /// <para>
    /// So the rule is not "you may not hide it" but "you may not declare it", and both fixtures are driven
    /// together because a rule closing only the mask leaves the worse route open — which is exactly what the
    /// first attempt did.
    /// </para>
    /// <para>
    /// Refused at apply on the settled precedent — <c>softDelete</c>, <c>computed</c>, and Task 5's
    /// <c>validation</c>/<c>default</c>/<c>rollup</c>: a descriptor that silently loses a documented guarantee
    /// fails at save, because a bad descriptor is a one-off configuration error and a per-request failure is
    /// not. <c>DescriptorValidatorTests</c> and <c>ManagedColumnNamesTests</c> own which columns the rule covers
    /// and why; this owns the claim that the refusal really reaches <em>this</em> API's apply path, which no
    /// unit fact can show — a migration runner that collected the errors and carried on would satisfy them all
    /// and fail here.
    /// </para>
    /// </remarks>
    /// <param name="descriptor">A fixture declaring <c>updated_at</c> on an audited entity.</param>
    [Theory]
    [InlineData("hidden-version.alvo.json")]
    [InlineData("retyped-version.alvo.json")]
    public async Task An_audited_entity_that_declares_its_own_version_column_is_refused_at_apply(string descriptor)
    {
        var failure = await Should.ThrowAsync<DescriptorValidationException>(
            () => AlvoApiWorld.FromDescriptorAsync(descriptor, [_admin]));

        failure.Message.ShouldContain("/entities/records/fields/updated_at", Case.Sensitive);
        failure.Message.ShouldContain(
            "If-Match",
            Case.Sensitive,
            "§0 principle 4: the refusal must say what the author is about to lose, not only that it is refused");
    }

    /// <summary>
    /// A caller the <c>delete</c> policy denies is told they are unauthorized, not that their
    /// <c>If-Match</c> was unusable — 403 outranks 412.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delete used to read the precondition header first, because Task 3 put the up-front policy check only
    /// on the two verbs that read a body, where the reason was resource cost. So a denied caller sending a
    /// malformed header got a 412 — an answer about a header they were never going to get to use, which sends
    /// an agent to fix the wrong thing. It disclosed nothing (the 412 depends only on the caller's own header),
    /// but it was the wrong diagnosis, and the precedence rule is uniform now.
    /// </para>
    /// <para>
    /// <c>ledgers</c> declares no <c>delete</c> rule at all, so default-deny refuses it for every caller. The
    /// key still holds the write scope — without it the scope gate would answer first, from a different slug,
    /// and the fact would be measuring that instead.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_denied_caller_is_refused_before_their_precondition_header_is()
    {
        var scoped = new TestApiKey("ledger-writer", ["authenticated"], ["ledgers:read", "ledgers:write"]);
        await using var world = await AlvoApiWorld.FromDescriptorAsync("masked-notes.alvo.json", [scoped]);
        var path = $"/api/ledgers/{Guid.NewGuid()}";

        using var malformed = await world.SendAsync(HttpMethod.Delete, path, scoped, headers: IfMatch("not-a-tag"));
        using var plain = await world.SendAsync(HttpMethod.Delete, path, scoped);

        malformed.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "a denied caller must be told they are denied, not that their header was unusable");
        (await malformed.ReadTextAsync()).ShouldBe(
            await plain.ReadTextAsync(), "and the header must make no difference at all to what they are told");
    }

    /// <summary>
    /// A create carrying a precondition header is refused rather than served, and no row is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Beyond the brief, and for the same reason as the fact above: a create has no stored version for either
    /// header to be compared against, so serving it would answer "your condition held" to a caller whose
    /// condition was never evaluated. It is the third write verb, and the only one where the honest answer is
    /// neither "compare it" nor "there is nothing at risk".
    /// </para>
    /// <para>
    /// The row count is read straight from the table rather than through a list, because a create that was
    /// refused after writing would answer 412 and still leave the row — and a list is filtered by the
    /// caller's own policy, so an empty one proves nothing.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("If-Match", "*")]
    [InlineData("If-Match", "\"638000000000000000\"")]
    [InlineData("If-None-Match", "*")]
    public async Task A_create_carrying_a_precondition_header_is_refused(string name, string value)
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: Rename("Conditional Ltd"), headers: Header(name, value));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.PreconditionFailed);
        (await world.CountRowsAsync("owners")).ShouldBe(0, "a refused create must not have written a row");
    }

    /// <summary>
    /// Every response a generated endpoint produces carries <c>Cache-Control: no-store</c> — which is what
    /// pays for a strong tag minted over the row version rather than over the response bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two callers whose policies mask different fields share one tag for one row version while their
    /// representations differ. That is only safe because no intermediary may keep these responses at all, so
    /// the header is part of the tag's design rather than hardening added beside it.
    /// </para>
    /// <para>
    /// The 401 and the 403 are the two that pin the filter's <em>order</em>: both are written by the
    /// authorization filter itself, so they are only stamped if the header comes from a filter that wraps it. A
    /// no-store written inside the endpoint delegate, or by a filter added second, passes every other case here
    /// and fails those two. They are asserted separately because they leave that filter by different exits —
    /// an unusable credential and a scope that does not cover the entity.
    /// </para>
    /// <para>
    /// The <em>successful</em> 200 of a <c>PATCH</c> is measured too, and that needed saying: the only PATCH in
    /// the probe list carries a malformed <c>If-Match</c> and therefore always 412s, so the header's coverage
    /// had a hole exactly where a write succeeds — the response that actually carries a fresh <c>ETag</c>.
    /// </para>
    /// <para>
    /// The assertion never touches <c>Headers.CacheControl?.NoStore</c>, and that is not style. A
    /// null-conditional propagates through the <em>whole</em> member-access chain, so
    /// <c>CacheControl?.NoStore.ShouldBe(true)</c> does not call <c>ShouldBe</c> at all when the header is
    /// absent — the one case this fact exists to catch. It was written that way, measured against a build with
    /// the filter deleted, and passed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_response_a_generated_endpoint_produces_is_no_store()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin, _narrow]);
        var owner = await SeedOwnerAsync(world, "Uncacheable");
        var ghost = new TestApiKey("ghost-key", ["admin"], ["*:read"]);

        foreach (var probe in Everything(owner))
        {
            using var response = await world.SendAsync(
                probe.Method, probe.Path, _admin, body: probe.Body, headers: probe.Headers);
            response.StatusCode.ShouldBe(
                probe.Expected, $"{probe.Method} {probe.Path} must reach the response this probe stands for");
            ShouldBeNoStore(response, $"{probe.Method} {probe.Path} answered {(int)response.StatusCode} and");
        }

        using var unauthenticated = await world.SendAsync(HttpMethod.Get, Owner(owner.Id), ghost);
        using var outOfScope = await world.SendAsync(HttpMethod.Get, Owner(owner.Id), _narrow);

        unauthenticated.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "or the 401 is not being measured");
        ShouldBeNoStore(unauthenticated, "the 401 the authorization filter itself answers with");
        outOfScope.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "or the 403 is not being measured");
        ShouldBeNoStore(outOfScope, "the 403 the scope gate answers with, on that filter's other exit,");
    }

    /// <summary>A key whose scopes cover a different entity, so the scope gate answers 403 from the filter itself.</summary>
    private static readonly TestApiKey _narrow = new("narrow-key", ["admin", "authenticated"], ["vehicles:read"]);

    /// <summary>Requires a response to carry <c>Cache-Control: no-store</c>, failing when it carries no such header.</summary>
    /// <param name="response">The response to measure.</param>
    /// <param name="what">How to name the response in a failure message.</param>
    private static void ShouldBeNoStore(HttpResponseMessage response, string what)
    {
        var cacheControl = response.Headers.CacheControl;
        cacheControl.ShouldNotBeNull($"{what} carried no parsable Cache-Control at all");
        cacheControl.NoStore.ShouldBeTrue($"{what} carried '{cacheControl}' rather than no-store");
    }

    /// <summary>
    /// One response of every kind a row's endpoints can produce, for the header claim above — each paired with
    /// the status it must reach.
    /// </summary>
    /// <remarks>
    /// The expected status is part of the probe because a probe that quietly stopped reaching its own case would
    /// still be measured for the header and still pass. That is not hypothetical: the successful <c>PATCH</c>
    /// below is here precisely because the only other one always 412s, so the 200 of a write — the response that
    /// carries a fresh <c>ETag</c> — was never measured at all. The order matters: the successful PATCH must
    /// come before the DELETE that removes the row.
    /// </remarks>
    private static IEnumerable<Probe> Everything(SeededOwner owner) =>
    [
        new(HttpMethod.Get, "/api/owners", null, null, HttpStatusCode.OK),
        new(HttpMethod.Get, Owner(owner.Id), null, null, HttpStatusCode.OK),
        new(HttpMethod.Get, Owner(owner.Id), null, IfNoneMatch(owner.ETag), HttpStatusCode.NotModified),
        new(HttpMethod.Get, Owner(Guid.NewGuid()), null, null, HttpStatusCode.NotFound),
        new(HttpMethod.Post, "/api/owners", new JsonObject { ["name"] = "Another Ltd" }, null, HttpStatusCode.Created),
        new(HttpMethod.Patch, Owner(owner.Id), Rename("Renamed"), null, HttpStatusCode.OK),
        new(HttpMethod.Patch, Owner(owner.Id), Rename("Refused"), IfMatch("not-a-tag"), HttpStatusCode.PreconditionFailed),
        new(HttpMethod.Delete, Owner(owner.Id), null, IfMatch("*"), HttpStatusCode.NoContent),
    ];

    /// <summary>One request the no-store claim is measured over.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Path">The request path.</param>
    /// <param name="Body">The body to send, or <see langword="null"/> for none.</param>
    /// <param name="Headers">Any further headers to present.</param>
    /// <param name="Expected">The status this probe must reach, or it is measuring the wrong response.</param>
    private sealed record Probe(
        HttpMethod Method,
        string Path,
        JsonObject? Body,
        IReadOnlyDictionary<string, string>? Headers,
        HttpStatusCode Expected);

    /// <summary>A created owner and the tag its 201 handed out.</summary>
    /// <param name="Id">The row's assigned id.</param>
    /// <param name="ETag">The entity tag for the version the create stored.</param>
    private sealed record SeededOwner(Guid Id, string ETag);

    private static string Owner(Guid id) => $"/api/owners/{id}";

    private static JsonObject Rename(string name) => new() { ["name"] = name };

    private static JsonObject Note(string notes) => new() { ["notes"] = notes };

    private static Dictionary<string, string> IfMatch(string value) => Header("If-Match", value);

    private static Dictionary<string, string> IfNoneMatch(string value) => Header("If-None-Match", value);

    private static Dictionary<string, string> Header(string name, string value) =>
        new(StringComparer.Ordinal) { [name] = value };

    /// <summary>
    /// The tag this API must mint for one stored instant, spelled out here rather than taken from the
    /// production encoder.
    /// </summary>
    /// <remarks>
    /// Reusing <c>RowVersionETag</c> would make every "the tag denotes the stored version" assertion agree
    /// with itself: an encoder that dropped to whole seconds would satisfy a comparison against its own
    /// output. This is the second, independent statement of the encoding — quoted invariant
    /// <see cref="DateTimeOffset.UtcTicks"/> — so the two have to agree with each other.
    /// </remarks>
    private static string TagOf(DateTimeOffset version) =>
        $"\"{version.UtcTicks.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>The row's stored <c>updated_at</c>, read back through the API as the response renders it.</summary>
    private static async Task<DateTimeOffset> VersionOfAsync(AlvoApiWorld world, Guid id)
    {
        using var response = await world.SendAsync(HttpMethod.Get, Owner(id), _admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return (await response.ReadJsonObjectAsync())["updated_at"]!.GetValue<DateTimeOffset>();
    }

    private static async Task<string?> NameOfAsync(AlvoApiWorld world, Guid id)
    {
        using var response = await world.SendAsync(HttpMethod.Get, Owner(id), _admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return (await response.ReadJsonObjectAsync())["name"]?.GetValue<string>();
    }

    /// <summary>Writes the row once <em>without</em> a precondition, so the version a fact holds goes stale.</summary>
    private static async Task AdvanceAsync(AlvoApiWorld world, Guid id, string name)
    {
        using var response = await world.SendAsync(HttpMethod.Patch, Owner(id), _admin, body: Rename(name));
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK, "the row must really be rewritten, or the stale tag below is still current");
    }

    private static async Task<SeededOwner> SeedOwnerAsync(AlvoApiWorld world, string name)
    {
        using var response = await world.SendAsync(HttpMethod.Post, "/api/owners", _admin, body: Rename(name));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return new SeededOwner((await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>(), response.ETagOf());
    }

    /// <summary>
    /// One inspection — the descriptor's only non-audited entity — plus the owner and vehicle its required
    /// refs need.
    /// </summary>
    /// <remarks>
    /// Deliberately built with <see cref="CreateAsync"/> rather than <see cref="SeedOwnerAsync"/>, which reads
    /// an <c>ETag</c> off its 201: a fact about an entity that must carry <em>no</em> tag has to be able to
    /// pass while nothing anywhere mints one, and a seed that demands a tag would fail it from the setup.
    /// </remarks>
    private static async Task<Guid> SeedInspectionAsync(AlvoApiWorld world)
    {
        var owner = await CreateAsync(world, "owners", Rename("Fleet Ltd"));
        var vehicle = await CreateAsync(world, "vehicles", new JsonObject
        {
            ["vin"] = "VIN00000000000001",
            ["plate"] = "AA-111-BB",
            ["make"] = "Skoda",
            ["model"] = "Octavia",
            ["year"] = 2020,
            ["owner_id"] = owner.ToString(),
        });

        return await CreateAsync(world, "inspections", new JsonObject
        {
            ["vehicle_id"] = vehicle.ToString(),
            ["inspector_name"] = "Ivan Inspector",
            ["inspected_on"] = "2026-01-15",
        });
    }

    private static async Task<Guid> CreateAsync(AlvoApiWorld world, string entity, JsonObject body)
    {
        using var response = await world.SendAsync(HttpMethod.Post, $"/api/{entity}", _admin, body: body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }
}
