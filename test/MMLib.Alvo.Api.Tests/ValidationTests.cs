using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Schema-derived validation over the live API: what the entity's declared facets refuse, what they must
/// <b>not</b> refuse, and that a refusal carries <em>every</em> reason rather than the first.
/// </summary>
/// <remarks>
/// <para>
/// Every fact here runs against <c>validated-records.alvo.json</c>, whose rules permit every caller on
/// purpose. That is what makes a 422 evidence about the payload: with a rule that could deny, a refusal
/// would be ambiguous between "your body is wrong" and "you may not do this", which is precisely the
/// distinction §0 principle 4 exists to keep clear.
/// </para>
/// <para>
/// Each fact is written so that removing the check it names — and nothing else — fails it, and almost every
/// one carries a positive control: a 422 proves nothing until some payload is accepted, because an endpoint
/// that refused everything would satisfy a whole suite of refusal facts.
/// </para>
/// </remarks>
public sealed class ValidationTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["authenticated"], ["*:read", "*:write"]);

    /// <summary>
    /// A caller the reference target's own predicate admits to the otherwise-invisible row. It exists so a
    /// confidentiality fact can prove the row is <em>there</em> without being able to read it as the caller
    /// under test.
    /// </summary>
    private static readonly TestApiKey _auditor =
        new("auditor-key", ["authenticated", "auditor"], ["*:read", "*:write"]);

    /// <summary>
    /// The load-bearing one: §2.1 and #19's definition of done both ask for <b>all</b> violations, and the
    /// reason is arithmetic — one per response is one round trip per mistake.
    /// </summary>
    /// <remarks>
    /// Asserted as an exact set of (pointer, code) pairs rather than "more than one violation": a validator
    /// that reported the first field twice would satisfy a count, and one that reported them in a different
    /// order should not fail. Both required fields are named, so dropping either check fails this fact.
    /// </remarks>
    [Fact]
    public async Task A_create_missing_two_required_fields_reports_both_not_just_the_first()
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/items", _admin, body: new JsonObject());
        using var accepted = await world.SendAsync(HttpMethod.Post, "/api/items", _admin, body: Item());

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).ShouldBe(
            [("/name", "required"), ("/quantity", "required")],
            ignoreOrder: true,
            "both required fields must be reported, or an agent pays a round trip per missing field");
        accepted.StatusCode.ShouldBe(
            HttpStatusCode.Created, "or the refusal above could be this endpoint refusing every create");
    }

    /// <summary>
    /// Three <em>different kinds</em> of violation over three different fields in one response — the trap
    /// this suite has to close is "all violations" quietly meaning "all violations of the first kind".
    /// </summary>
    /// <remarks>
    /// A missing required field, a value over its <c>maxLength</c>, and a write to a read-only field are
    /// decided by three separate branches of the validator, so a validator that returned early after any one
    /// of them fails this and nothing else in the suite would notice.
    /// </remarks>
    [Fact]
    public async Task A_payload_failing_three_different_rules_reports_all_three_in_one_response()
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post,
            "/api/items",
            _admin,
            body: new JsonObject
            {
                ["quantity"] = 1,
                ["sku"] = new string('x', 60),
                ["slug"] = "assigned-by-the-server",
            });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).ShouldBe(
            [("/name", "required"), ("/sku", "max-length"), ("/slug", "read-only-field")],
            ignoreOrder: true,
            "three kinds of violation over three fields must arrive together");
    }

    /// <summary>
    /// A value over the declared width is refused, and the message <b>names the limit</b>: an agent told
    /// "too long" guesses, an agent told "at most 10" fixes it once.
    /// </summary>
    [Fact]
    public async Task A_string_past_its_max_length_is_a_violation_naming_the_limit()
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(name: new string('n', 11)));
        using var accepted = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(name: new string('n', 10)));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).ShouldBe([("/name", "max-length")]);
        (await refused.ReadProblemDetailAsync()).ShouldContain(
            "10", Case.Sensitive, "the refusal must name the declared bound, not merely that one was crossed");
        accepted.StatusCode.ShouldBe(
            HttpStatusCode.Created, "exactly the bound is inside it — an off-by-one here refuses a legal value");
    }

    /// <summary>
    /// A decimal carrying more fractional digits than the column keeps is refused rather than rounded: a
    /// silently rounded amount is a number the caller never agreed to, and on a money field it is a defect
    /// nobody sees until reconciliation.
    /// </summary>
    /// <remarks>
    /// The two controls matter for different reasons. <c>1.23</c> is at the declared scale and must pass, or
    /// the check is refusing everything; <c>1.230</c> is a value with a <em>literal</em> scale of three that
    /// is representable at two, and it must pass as well — a validator counting the digits of the literal
    /// instead of measuring representability would refuse a value the caller cannot fix.
    /// </remarks>
    [Fact]
    public async Task A_decimal_past_its_scale_is_a_violation()
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(price: 1.234m));
        using var atTheScale = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(price: 1.23m));
        using var trailingZero = await world.SendRawAsync(
            HttpMethod.Post, "/api/items", _admin,
            content: AlvoApiWorld.RawJson(@"{""name"":""n"",""quantity"":1,""price"":1.230}"));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).ShouldBe([("/price", "scale")]);
        atTheScale.StatusCode.ShouldBe(HttpStatusCode.Created);
        trailingZero.StatusCode.ShouldBe(
            HttpStatusCode.Created, "a trailing zero is not a fourth digit — the value is representable at scale 2");
    }

    /// <summary>
    /// The other half of a <c>decimal</c>'s declaration: <c>precision</c> bounds the digits <em>before</em>
    /// the point too, and an engine left to notice it answers with a truncation on one backend and an
    /// overflow on another.
    /// </summary>
    [Fact]
    public async Task A_decimal_needing_more_integral_digits_than_its_precision_leaves_is_a_violation()
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(price: 10000.00m));
        using var accepted = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(price: 9999.99m));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).ShouldBe([("/price", "precision")]);
        accepted.StatusCode.ShouldBe(
            HttpStatusCode.Created, "the largest value NUMERIC(6,2) holds must pass, or the bound is off by one");
    }

    /// <summary>
    /// A value outside an enum's declared set is refused, and the fix <b>lists the set</b> — those values
    /// are the descriptor author's own, not the caller's, so naming them discloses nothing and saves every
    /// subsequent guess.
    /// </summary>
    [Fact]
    public async Task A_value_outside_an_enums_declared_values_is_a_violation_listing_them()
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(status: "archived"));
        using var accepted = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(status: "active"));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).ShouldBe([("/status", "enum-value")]);
        var fix = (await refused.ReadFixSuggestionsAsync()).ShouldHaveSingleItem()
            .ShouldNotBeNull("§0 principle 4 makes a fix suggestion part of the contract, not a nicety");
        fix.ShouldContain("draft");
        fix.ShouldContain("active");
        fix.ShouldContain("retired");
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// A value failing its field's format is refused, and the violation <b>names the format</b> rather than
    /// echoing the pattern: the name is what the descriptor author chose and what the OpenAPI document
    /// publishes, so it is the term the caller can look up.
    /// </summary>
    /// <remarks>
    /// Both rungs of the format ladder are exercised, because they resolve through different code: a
    /// built-in (<c>email</c>, whose pattern the framework owns) and a declared named format
    /// (<c>sku-code</c>, whose pattern came out of the descriptor and had to survive the mapper).
    /// </remarks>
    [Fact]
    public async Task A_value_failing_a_named_format_is_a_violation_naming_the_format()
    {
        await using var world = await WorldAsync();

        using var badSku = await world.SendAsync(HttpMethod.Post, "/api/items", _admin, body: Item(sku: "nope"));
        using var badEmail = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(contact: "not-an-address"));
        using var accepted = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(sku: "ABC-1234", contact: "owner@example.com"));

        badSku.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await badSku.ReadViolationsAsync()).ShouldBe([("/sku", "format")]);
        (await badSku.ReadProblemDetailAsync()).ShouldContain(
            "sku-code", Case.Sensitive, "the declared format's own name is what the caller can look up");
        badEmail.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await badEmail.ReadViolationsAsync()).ShouldBe([("/contact", "format")]);
        accepted.StatusCode.ShouldBe(
            HttpStatusCode.Created, "a value in both formats passes, so the refusals are about the values");
    }

    /// <summary>
    /// A format is a statement about the <b>whole</b> value, so a descriptor pattern with no anchors of its
    /// own must still refuse a value that merely <em>contains</em> a match.
    /// </summary>
    /// <remarks>
    /// <c>sku-code</c> is declared as <c>[A-Z]{3}-[0-9]{4}</c> with no <c>^…$</c> on purpose. Unanchored,
    /// <c>ABC-1234-oops; DROP TABLE items</c> matches on its prefix and is stored — which is how an
    /// author's reasonable-looking pattern becomes no validation at all. This is the fact that fails if the
    /// anchoring is dropped, and it cannot be satisfied by an author who happened to anchor their own.
    /// </remarks>
    [Fact]
    public async Task A_format_pattern_with_no_anchors_of_its_own_still_matches_the_whole_value()
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(sku: "ABC-1234-oops"));
        using var accepted = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(sku: "ABC-1234"));

        refused.StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity,
            "an unanchored pattern must be anchored by the framework, or a prefix match is enough to pass");
        (await refused.ReadViolationsAsync()).ShouldBe([("/sku", "format")]);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// A hostile value against a catastrophic descriptor pattern is answered <b>promptly</b>, and refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>greedy</c> is declared as <c>(a+)+b</c> — the textbook exponential-backtracking pattern — and the
    /// value is a run of <c>a</c>s with no <c>b</c>, which is the input that makes a backtracking engine
    /// explore every partition of the run. With neither of <c>FormatCatalog</c>'s two defences the request
    /// does not return at all: sixty <c>a</c>s is on the order of 2^60 paths, so this fact does not fail
    /// slowly, it hangs the run — which is exactly the production symptom.
    /// </para>
    /// <para>
    /// The elapsed bound is deliberately far above both defences (<c>NonBacktracking</c> finishes in
    /// microseconds, the timeout fallback in ~100 ms) and far below anything backtracking would take, so it
    /// is not a flaky timing assertion. The status matters as much: a timeout that escaped as a 500 would
    /// turn a refused value into a broken invariant, so "refused" and "promptly" are asserted together.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_pathological_value_against_a_catastrophic_pattern_is_refused_without_hanging()
    {
        await using var world = await WorldAsync();
        var pathological = new string('a', 60);

        var stopwatch = Stopwatch.StartNew();
        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(pathological: pathological));
        stopwatch.Stop();

        refused.StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity, "a value that cannot be decided must be refused, never 500");
        (await refused.ReadViolationsAsync()).ShouldBe([("/pathological", "format")]);
        stopwatch.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(10),
            "a caller-supplied value must not be able to drive a descriptor pattern into exponential backtracking");
    }

    /// <summary>
    /// <b>A write to a <c>readOnly</c> field is 422 from validation, and the port's 403 stays the
    /// backstop.</b> Both are correct in their own layer, and this fact pins which one an HTTP caller sees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Validation runs before the port, so an HTTP caller gets the actionable 422 with a fix suggestion; the
    /// port's <c>AlvoAuthorizationException</c> (403) remains for a caller reaching <c>IAlvoData</c>
    /// directly, and is asserted in the adversarial suite. A later refactor that moved the check into the
    /// port would silently turn this into a 403 with no fix — which is exactly the regression this fact
    /// exists to catch, so the status is asserted as 422 rather than merely "a refusal".
    /// </para>
    /// <para>
    /// Never a silent drop: a caller who sent a value and received a 201 believes it was stored. The
    /// positive control is a create that omits the field entirely — the read-only field must not make the
    /// entity unwritable.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_write_to_a_read_only_field_is_422_with_a_violation_not_a_silent_drop()
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(slug: "chosen-by-the-caller"));
        using var accepted = await world.SendAsync(HttpMethod.Post, "/api/items", _admin, body: Item());

        refused.StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity,
            "a 403 here means the port answered instead of validation — the same write, the wrong layer, and "
            + "a refusal with no fix suggestion");
        (await refused.ReadViolationsAsync()).ShouldBe([("/slug", "read-only-field")]);
        (await refused.ReadFixSuggestionsAsync()).ShouldAllBe(fix => fix != null);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// <b>A write to a <c>hidden</c> field is ACCEPTED.</b> <c>hidden</c> restricts reading and
    /// <c>readOnly</c> restricts writing; they are different flags and the port treats them so, so a caller
    /// may legitimately write a field whose value they may not read back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This fact exists because the "helpful" refusal is so tempting. Refusing the write would silently
    /// change <c>IAlvoData</c>'s contract <em>and</em> hand the caller the hidden field's existence — the one
    /// thing the query parser's mask parameter exists to withhold, since a hidden field's name must stay
    /// indistinguishable from an unknown one. A later tidy-up that "fixes" the asymmetry fails here.
    /// </para>
    /// <para>
    /// The response is asserted not to carry the field back, which is the other half of the contract: the
    /// write is accepted and the value is still masked out of every representation.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_write_to_a_hidden_field_is_accepted_because_hidden_restricts_reading_not_writing()
    {
        await using var world = await WorldAsync();

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(secret: "for-the-server-only"));

        created.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            "hidden restricts reading, so refusing the write would change the port's contract and disclose the field");
        (await created.ReadTextAsync()).ShouldNotContain(
            "for-the-server-only", Case.Sensitive, "the written value must still be masked out of the response");
        (await created.ReadJsonObjectAsync()).ContainsKey("secret").ShouldBeFalse();
    }

    /// <summary>
    /// A reference naming a row that is not there is a violation on that field, not a foreign-key error out
    /// of the engine — an agent can act on the first and not on the second.
    /// </summary>
    [Fact]
    public async Task A_ref_field_pointing_at_a_row_that_does_not_exist_is_a_violation()
    {
        await using var world = await WorldAsync();
        var real = await CreateCatalogAsync(world, "visible", visible: true);

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(catalog: Guid.NewGuid()));
        using var accepted = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(catalog: real));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).ShouldBe([("/catalog_id", "unresolved-reference")]);
        accepted.StatusCode.ShouldBe(
            HttpStatusCode.Created, "a real, readable row is accepted — so the refusal is about the row, not the field");
    }

    /// <summary>
    /// The security half: a reference to a row that <b>exists but this caller cannot read</b> must be
    /// byte-identical to a reference to a row that does not exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Told apart, a create endpoint becomes a cross-tenant existence oracle wearing a 201/422 shape — one
    /// request per candidate id, answered without ever reading a byte of the row. So the assertion is
    /// equality of the <em>whole body</em> and of the status, not "both are 422": a differing <c>detail</c>
    /// or a differing violation code is the leak.
    /// </para>
    /// <para>
    /// <b>The row is proved to exist by a caller who <em>can</em> read it.</b> The descriptor's predicate is
    /// <c>visible == true || 'auditor' in @user.roles</c>, so the same id resolves for an auditor and is
    /// referenced successfully by one. Without that control the equality is satisfied by two requests
    /// referencing nothing — the failure mode where a broken seed makes a confidentiality fact vacuous.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_ref_field_pointing_at_a_row_the_caller_cannot_see_is_the_same_violation()
    {
        await using var world = await WorldAsync([_admin, _auditor]);
        var invisible = await CreateCatalogAsync(world, "invisible", visible: false);

        using var unreadable = await world.SendAsync(HttpMethod.Get, $"/api/catalogs/{invisible}", _admin);
        using var readableByAuditor = await world.SendAsync(HttpMethod.Get, $"/api/catalogs/{invisible}", _auditor);
        using var referencingInvisible = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(catalog: invisible));
        using var referencingAbsent = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(catalog: Guid.NewGuid()));
        using var auditorReferencingIt = await world.SendAsync(
            HttpMethod.Post, "/api/items", _auditor, body: Item(catalog: invisible));

        unreadable.StatusCode.ShouldBe(
            HttpStatusCode.NotFound, "the row must really be invisible to this caller");
        readableByAuditor.StatusCode.ShouldBe(
            HttpStatusCode.OK, "and it must really exist, or 'unresolved' below is about a row nobody wrote");
        referencingInvisible.StatusCode.ShouldBe(referencingAbsent.StatusCode);
        (await referencingInvisible.ReadTextAsync()).ShouldBe(
            await referencingAbsent.ReadTextAsync(),
            "any difference answers 'does this id exist' for a row the caller may not read");
        auditorReferencingIt.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            "the very same reference resolves for a caller whose policy admits the row — so the refusal above "
            + "is the caller's own decision being applied, not the reference being unresolvable in principle");
    }

    /// <summary>
    /// A reference the caller may not <em>read at all</em> — the target entity has no <c>get</c> rule, so
    /// default-deny refuses the read outright — is the <b>same</b> unresolved-reference violation, not a 403.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two reasons, and they are separate. Confidentiality: "your policy refused this read" and "your
    /// predicate excluded this row" are two ways of not being able to resolve the reference, and telling them
    /// apart is a coarser version of the existence oracle the fact above closes. Actionability: the 403 would
    /// be about <c>vaults</c>, an entity the caller never named in this request, so an agent would go looking
    /// for a rule on the entity it was trying to write.
    /// </para>
    /// <para>
    /// This is the fact that reaches the reference probe's <c>AlvoAuthorizationException</c> arm at all. The
    /// invisible-row case above cannot: its exclusion is a row predicate, which returns no row rather than
    /// refusing the read, so with only that fact the catch arm is unreachable and its removal is invisible.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_reference_to_an_entity_the_caller_may_not_read_is_the_same_violation_not_a_403()
    {
        await using var world = await WorldAsync();
        var vault = await CreateVaultAsync(world, "the-vault");
        var catalog = await CreateCatalogAsync(world, "visible", visible: true);

        using var referencingVault = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(vault: vault));
        using var referencingAbsentVault = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(vault: Guid.NewGuid()));
        using var control = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: Item(catalog: catalog));

        referencingVault.StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity,
            "a 403 here is a refusal about 'vaults', an entity this request never named");
        (await referencingVault.ReadViolationsAsync()).ShouldBe([("/vault_id", "unresolved-reference")]);
        (await referencingVault.ReadTextAsync()).ShouldBe(
            await referencingAbsentVault.ReadTextAsync(),
            "a vault that exists and one that does not must be indistinguishable to a caller who may read neither");
        control.StatusCode.ShouldBe(
            HttpStatusCode.Created, "a readable reference still resolves, so the refusals are about the target");
    }

    /// <summary>
    /// A key naming no field is a violation, and the refusal <b>does not confirm the schema</b>: the key is
    /// caller-supplied text, so echoing it back both names what does not exist and puts attacker-controlled
    /// bytes in every log that records the response.
    /// </summary>
    [Fact]
    public async Task A_payload_key_naming_no_field_is_a_violation_that_does_not_confirm_the_schema()
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendRawAsync(
            HttpMethod.Post, "/api/items", _admin,
            content: AlvoApiWorld.RawJson(@"{""name"":""n"",""quantity"":1,""smuggled_field"":1}"));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).Select(violation => violation.Code)
            .ShouldBe(["unknown-field"]);
        var body = await refused.ReadTextAsync();
        body.ShouldNotContain("smuggled_field");
        body.ShouldNotContain("items");
    }

    /// <summary>
    /// A body that is not a JSON object is <b>422 and not 500</b>: it is the caller's mistake, and a 500
    /// tells them the server is broken while their request was simply the wrong shape.
    /// </summary>
    /// <remarks>
    /// Every non-object shape is covered — an array, a scalar, a bare <c>null</c>, and an empty body — because
    /// they take different paths through the reader and the last two are the ones a hand-rolled check
    /// forgets. Each is asserted to carry a violation, so none of them can be a 422 with an empty document.
    /// </remarks>
    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("")]
    public async Task A_body_that_is_not_a_json_object_is_422_and_not_500(string body)
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendRawAsync(
            HttpMethod.Post, "/api/items", _admin, content: AlvoApiWorld.RawJson(body));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).ShouldAllBe(violation => violation.Pointer == "");
    }

    /// <summary>
    /// A partial update legitimately omits a required field — that is <c>IAlvoData.UpdateAsync</c>'s own
    /// contract ("a field this dictionary does not mention keeps its stored value") — while explicitly
    /// nulling one is still refused.
    /// </summary>
    /// <remarks>
    /// The one bit of validation that differs between the two write verbs, and the direction that breaks
    /// loudly is the wrong one: a validator applying the create's rule to a PATCH would make every partial
    /// update send the whole row. The null case is the other half, and it must not be admitted just because
    /// absence is — a caller nulling a required field is asking for something no create may do either.
    /// </remarks>
    [Fact]
    public async Task A_partial_update_may_omit_a_required_field_but_may_not_null_one()
    {
        await using var world = await WorldAsync();
        var id = await CreateItemAsync(world);

        using var omitted = await world.SendAsync(
            HttpMethod.Patch, $"/api/items/{id}", _admin, body: new JsonObject { ["quantity"] = 7 });
        using var nulled = await world.SendAsync(
            HttpMethod.Patch, $"/api/items/{id}", _admin, body: new JsonObject { ["name"] = null });

        omitted.StatusCode.ShouldBe(
            HttpStatusCode.OK, "a PATCH that does not mention 'name' is not a PATCH that removed it");
        nulled.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await nulled.ReadViolationsAsync()).ShouldBe([("/name", "required")]);
    }

    /// <summary>
    /// A mistyped value and a missing required field arrive in <b>one</b> response, though one is the body
    /// reader's finding and the other the validator's.
    /// </summary>
    /// <remarks>
    /// The two run in sequence over the same payload, and the naive wiring — short-circuit as soon as the
    /// reader reports anything — costs a round trip for no reason. The field the reader refused must also not
    /// be double-reported: <c>quantity</c> never bound, so reporting it as <em>required</em> as well would
    /// tell the caller to fix two things that are one thing.
    /// </remarks>
    [Fact]
    public async Task A_mistyped_value_and_a_missing_required_field_arrive_in_one_response()
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendRawAsync(
            HttpMethod.Post, "/api/items", _admin,
            content: AlvoApiWorld.RawJson(@"{""quantity"":""not-a-number""}"));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).ShouldBe(
            [("/quantity", "invalid-value"), ("/name", "required")],
            ignoreOrder: true,
            "the reader's finding and the validator's must arrive together, and 'quantity' only once");
    }

    /// <summary>
    /// Validation runs on the caller's behalf, so it must never be reachable <b>before</b> authorization:
    /// an unauthorized write is refused without its body being validated at all.
    /// </summary>
    /// <remarks>
    /// The body is simultaneously missing a required field and over a bound, so a 422 would be unambiguous
    /// evidence that validation ran first — and validation is the one step that now costs a database round
    /// trip per reference field, which makes running it for a caller who cannot succeed a denial-of-service
    /// amplifier rather than merely wrong precedence.
    /// </remarks>
    [Fact]
    public async Task An_unauthorized_write_is_refused_before_its_body_is_validated()
    {
        var reader = new TestApiKey("reader-key", ["authenticated"], ["items:read"]);
        await using var world = await WorldAsync([_admin, reader]);

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/items", reader, body: Item(name: new string('n', 99), sku: "nope"));
        using var accepted = await world.SendAsync(HttpMethod.Post, "/api/items", _admin, body: Item());

        refused.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden, "the scope gate must answer before a reference probe is worth running");
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private static Task<AlvoApiWorld> WorldAsync(IReadOnlyList<TestApiKey>? keys = null) =>
        AlvoApiWorld.FromDescriptorAsync("validated-records.alvo.json", keys ?? [_admin]);

    private static async Task<Guid> CreateCatalogAsync(AlvoApiWorld world, string name, bool visible)
    {
        var body = new JsonObject { ["name"] = name, ["visible"] = visible };
        using var response = await world.SendAsync(HttpMethod.Post, "/api/catalogs", _admin, body: body);
        response.StatusCode.ShouldBe(
            HttpStatusCode.Created, $"seeding catalog '{name}' must succeed, or the facts over it prove nothing");
        return (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private static async Task<Guid> CreateVaultAsync(AlvoApiWorld world, string label)
    {
        var body = new JsonObject { ["label"] = label };
        using var response = await world.SendAsync(HttpMethod.Post, "/api/vaults", _admin, body: body);
        response.StatusCode.ShouldBe(
            HttpStatusCode.Created, "seeding a vault must succeed, or 'the row exists' below is not true");
        return (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private static async Task<Guid> CreateItemAsync(AlvoApiWorld world)
    {
        using var response = await world.SendAsync(HttpMethod.Post, "/api/items", _admin, body: Item());
        response.StatusCode.ShouldBe(
            HttpStatusCode.Created, "seeding an item must succeed, or the facts over it prove nothing");
        return (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    /// <summary>
    /// A valid <c>items</c> payload, with exactly the one facet under test overridden — so every fact's
    /// refusal can only be about that facet, and the accepted control differs from the refused one by a
    /// single value.
    /// </summary>
    private static JsonObject Item(
        string name = "n",
        string? sku = null,
        string? contact = null,
        string? pathological = null,
        decimal? price = null,
        string? status = null,
        string? slug = null,
        string? secret = null,
        Guid? catalog = null,
        Guid? vault = null)
    {
        var body = new JsonObject { ["name"] = name, ["quantity"] = 1 };
        Set(body, "sku", sku);
        Set(body, "contact", contact);
        Set(body, "pathological", pathological);
        Set(body, "status", status);
        Set(body, "slug", slug);
        Set(body, "secret", secret);

        if (price is { } amount)
        {
            body["price"] = JsonValue.Create(amount);
        }

        if (catalog is { } reference)
        {
            body["catalog_id"] = reference.ToString();
        }

        if (vault is { } vaultReference)
        {
            body["vault_id"] = vaultReference.ToString();
        }

        return body;
    }

    private static void Set(JsonObject body, string field, string? value)
    {
        if (value is not null)
        {
            body[field] = value;
        }
    }
}
