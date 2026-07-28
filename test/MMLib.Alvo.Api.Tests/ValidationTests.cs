using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Schema;
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
    /// explore every partition of the run.
    /// </para>
    /// <para>
    /// <b>This pattern measures defence 1 and nothing else.</b> <c>NonBacktracking</c> compiles
    /// <c>(a+)+b</c> happily, so the fast engine is what answers here and the match timeout is never
    /// consulted — remove <c>NonBacktracking</c> alone and this fact still passes (measured), because the
    /// timeout then catches it. Remove <em>both</em> and the request does not return at all: sixty
    /// <c>a</c>s is on the order of 2^60 paths, so the failure is a hung run rather than a red assertion,
    /// which is exactly the production symptom. The fallback and the timeout get their own facts over the
    /// <c>lookahead-*</c> formats, which are the only shapes that reach them.
    /// </para>
    /// <para>
    /// The elapsed bound is far above what either defence costs and far below anything backtracking would
    /// take, so it is not a flaky timing assertion. The status matters as much: a timeout that escaped as a
    /// 500 would turn a refused value into a broken invariant, so "refused" and "promptly" are asserted
    /// together.
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
    /// The <b>fallback</b> arm: a pattern <see cref="System.Text.RegularExpressions.RegexOptions.NonBacktracking"/>
    /// refuses to compile still enforces its format — accepting what it should and refusing what it should.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The linear-time engine cannot express a lookahead, so <c>(?=[A-Z])[A-Za-z]{2,8}</c> compiles only on
    /// the backtracking engine. Until this fact existed, no descriptor in the suite used such a pattern:
    /// <c>(a+)+b</c> compiles under <c>NonBacktracking</c>, so the entire fallback — and the timeout the
    /// fallback depends on — was unreached, and deleting it was invisible.
    /// </para>
    /// <para>
    /// Both directions are asserted because a fallback that threw, or that matched nothing, would be caught
    /// only by the accepting half, and one that matched everything only by the refusing half.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_format_the_linear_time_engine_cannot_compile_is_still_enforced()
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: WithField("capitalised", "lowercase"));
        using var accepted = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: WithField("capitalised", "Capital"));

        refused.StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity,
            "the backtracking fallback must enforce the format, not silently admit everything");
        (await refused.ReadViolationsAsync()).ShouldBe([("/capitalised", "format")]);
        accepted.StatusCode.ShouldBe(
            HttpStatusCode.Created, "and it must still admit a value the pattern matches");
    }

    /// <summary>
    /// The <b>fail-closed timeout</b> arm: a value the backtracking engine cannot decide inside the match
    /// timeout is <b>refused</b>, promptly — never admitted, and never a 500.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the arm the linear-time engine cannot protect, because the pattern
    /// (<c>(?=(a+)+b)a*</c> — a catastrophic core behind a lookahead) is one it refuses to compile. On the
    /// backtracking engine, 3 000 <c>a</c>s with no <c>b</c> is exponential, so the timeout is the only thing
    /// standing between a caller-supplied string and a held thread.
    /// </para>
    /// <para>
    /// <b>"Refused" is as load-bearing as "promptly".</b> A <c>RegexMatchTimeoutException</c> left uncaught
    /// would leave the field's format undecided and answer 500 — turning a value the framework declined to
    /// judge into a broken invariant. Answering <see langword="true"/> instead would admit it. So the status,
    /// the violation and the elapsed time are asserted together.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_value_the_backtracking_engine_cannot_decide_in_time_is_refused_not_admitted()
    {
        await using var world = await WorldAsync();

        var stopwatch = Stopwatch.StartNew();
        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: WithField("backtracking", new string('a', 3_000)));
        stopwatch.Stop();

        refused.StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity,
            "a value the engine could not decide must be refused — not admitted, and not rendered as a 500");
        (await refused.ReadViolationsAsync()).ShouldBe([("/backtracking", "format")]);
        stopwatch.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(10),
            "the match timeout is the only bound on this pattern; without it the request never returns");
    }

    /// <summary>
    /// A <see cref="FieldSchema.FormatPattern"/> that is not a regular expression is refused when the
    /// catalogue is <b>built</b> — at startup, naming the format — not at the first request to reach the
    /// field.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DescriptorToSchemaMapper</c> refuses an unparseable pattern at apply, and for a while that was
    /// offered as proof that this branch could not be reached. Making <see cref="FieldSchema.FormatPattern"/>
    /// public falsified it: any other producer of a <see cref="SchemaModel"/> — a host with its own
    /// <c>ISchemaRegistry</c>, a hand-assembled model, F7's dynamic registry — can carry a pattern nothing
    /// checked, and <c>Regex</c>'s own <c>ArgumentException</c> would then escape <c>MapAlvoDataApi</c> without
    /// naming which format was at fault.
    /// </para>
    /// <para>
    /// Driven directly rather than over HTTP, because the point is that no request is involved: the failure
    /// belongs to whoever composed the schema, and it happens before a route can serve anything.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_format_pattern_that_is_not_a_regular_expression_is_refused_when_the_catalogue_is_built()
    {
        var broken = new EntitySchema
        {
            Name = "items",
            Fields =
            [
                new FieldSchema { Name = "code", Type = FieldType.String, Format = "half-open", FormatPattern = "([0-9" },
            ],
        };

        var exception = Should.Throw<InvalidOperationException>(() => FormatCatalog.Build([broken]));

        exception.Message.ShouldContain(
            "half-open", Case.Sensitive, "the refusal must name the format, or the author cannot find it");
        FormatCatalog.Build([broken with { Fields = [new FieldSchema { Name = "code", Type = FieldType.String }] }])
            .ShouldNotBeNull("a schema with no pattern still builds, so the refusal is about the pattern");
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
        (await refused.ReadViolationsAsync()).ShouldBe(
            [("/smuggled_field", "unknown-field")],
            "the pointer must name the caller's own key — it is a location in their request, not a claim "
            + "about the entity — and against the body pointer this violation once discarded every other one");
        (await refused.ReadProblemDetailAsync()).ShouldNotContain(
            "smuggled_field",
            Case.Sensitive,
            "the prose is what gets logged and re-rendered, so it stays free of caller-supplied text");
        (await refused.ReadTextAsync()).ShouldNotContain(
            "items", Case.Sensitive, "and no wording names the entity");
    }

    /// <summary>
    /// <b>An unrecognised key does not suppress the rest.</b> Four violations of four different kinds — an
    /// unknown key, a missing required field, a value over its length bound, and a write to a read-only
    /// field — arrive in one response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fact whose absence let a Critical ship. The unknown-key violation carried the empty body
    /// pointer while the reader inferred "the body did not bind" from an empty pointer, so <em>one</em>
    /// unrecognised key made the reader discard everything else: this exact payload reported <b>1</b>
    /// violation where the same payload without the smuggled key reported <b>4</b>.
    /// </para>
    /// <para>
    /// The control is what makes it discriminating rather than merely true: the same body <em>minus</em> the
    /// smuggled key must report exactly the other three. Without it, a validator that dropped one of the
    /// three for an unrelated reason would still satisfy a count, and the "4 versus 1" asymmetry — the shape
    /// of the original defect — would be invisible.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_unrecognised_key_does_not_suppress_the_other_violations()
    {
        await using var world = await WorldAsync();
        const string WithSmuggledKey =
            @"{""smuggled"":1,""quantity"":1,""sku"":""xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"",""slug"":""x""}";
        const string WithoutIt =
            @"{""quantity"":1,""sku"":""xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"",""slug"":""x""}";

        using var withKey = await world.SendRawAsync(
            HttpMethod.Post, "/api/items", _admin, content: AlvoApiWorld.RawJson(WithSmuggledKey));
        using var withoutKey = await world.SendRawAsync(
            HttpMethod.Post, "/api/items", _admin, content: AlvoApiWorld.RawJson(WithoutIt));

        withKey.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await withKey.ReadViolationsAsync()).ShouldBe(
            [
                ("/smuggled", "unknown-field"),
                ("/name", "required"),
                ("/sku", "max-length"),
                ("/slug", "read-only-field"),
            ],
            ignoreOrder: true,
            "one unrecognised key must not cost the caller the other three violations");
        (await withoutKey.ReadViolationsAsync()).ShouldBe(
            [("/name", "required"), ("/sku", "max-length"), ("/slug", "read-only-field")],
            ignoreOrder: true,
            "the same payload without the smuggled key reports the other three — so the four above are the "
            + "three plus one, not a coincidence");
    }

    /// <summary>
    /// Two unrecognised keys are two violations, not one: they are distinguishable by pointer, so the
    /// de-duplication that collapses identical refusals must not collapse these.
    /// </summary>
    /// <remarks>
    /// Task 4 added de-duplication by <c>(code, pointer)</c> for the query parser, where several refusals
    /// genuinely are the same statement. Here they are not — each names a different key the caller has to
    /// remove — and a caller told about one of two bad keys pays a round trip to learn about the second.
    /// </remarks>
    [Fact]
    public async Task Two_unrecognised_keys_are_two_violations()
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendRawAsync(
            HttpMethod.Post, "/api/items", _admin,
            content: AlvoApiWorld.RawJson(@"{""name"":""n"",""quantity"":1,""first"":1,""second"":2}"));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).ShouldBe(
            [("/first", "unknown-field"), ("/second", "unknown-field")],
            ignoreOrder: true,
            "each unrecognised key is its own fix, so each earns its own violation");
        var detail = await refused.ReadProblemDetailAsync();
        detail.Split("not writable on this entity").Length.ShouldBe(
            2,
            "the two violations share one sentence, and the prose joins it once — the detail is for a human, "
            + "the array is the machine half that names both keys");
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

    /// <summary>
    /// <b>Every built-in format the frozen schema enumerates is one this build actually enforces</b>, and no
    /// more — asserted against <c>schema/project.schema.json</c>'s own enum branch rather than a hand-written
    /// copy of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The names and their patterns were briefly two lists with nothing tying them: deleting <c>uri</c> and
    /// <c>phone</c> from the pattern list built clean and left the whole suite green while both formats
    /// silently validated nothing. A format that validates nothing is a fail-open on caller input, and the
    /// only assertion that catches it is one that reads the authoritative set from outside the code under
    /// test — the schema is that outside source, and it is frozen.
    /// </para>
    /// <para>
    /// Both directions matter. A name the schema lists but the catalogue lacks resolves to no pattern and
    /// validates nothing; a name the catalogue carries but the schema does not lets a descriptor that only
    /// this build accepts stop validating on any other one.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_built_in_format_the_schema_declares_is_one_this_build_enforces()
    {
        var schema = JsonNode.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;
        var declared = schema["$defs"]!["field"]!["properties"]!["format"]!["anyOf"]!
            .AsArray()
            .SelectMany(branch => branch!["enum"]?.AsArray() ?? [])
            .Select(name => name!.GetValue<string>())
            .ToList();

        declared.ShouldBe(
            ["email", "uri", "phone"],
            ignoreOrder: true,
            "read from the frozen schema — if this changes, the schema changed and the catalogue owes it a visit");
        FormatCatalog.BuiltIns.Keys.ShouldBe(
            declared,
            ignoreOrder: true,
            "a name the schema lists and the catalogue lacks validates nothing; a name only the catalogue "
            + "carries accepts a descriptor no other build honours");
        FormatCatalog.BuiltIns.Values.ShouldAllBe(pattern => pattern.Length > 0);
    }

    /// <summary>
    /// Each built-in is enforced <em>end to end</em>, so a pattern that exists but is never applied cannot
    /// pass the catalogue fact above.
    /// </summary>
    /// <remarks>
    /// The set fact proves the names line up; it cannot prove a pattern reaches a request. These two cases do,
    /// over the live API, and they are why deleting a built-in's pattern now fails something a caller would
    /// notice rather than only an inventory.
    /// </remarks>
    /// <param name="field">The field declaring the built-in under test.</param>
    /// <param name="rejected">A value the format must refuse.</param>
    /// <param name="accepted">A value the format must admit.</param>
    [Theory]
    [InlineData("contact", "not-an-address", "owner@example.com")]
    [InlineData("homepage", "example.com", "https://example.com/x")]
    [InlineData("telephone", "no-digits-here", "+421 900 123 456")]
    public async Task Each_built_in_format_is_enforced_over_the_live_api(
        string field, string rejected, string accepted)
    {
        await using var world = await WorldAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: WithField(field, rejected));
        using var allowed = await world.SendAsync(
            HttpMethod.Post, "/api/items", _admin, body: WithField(field, accepted));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).ShouldBe([($"/{field}", "format")]);
        allowed.StatusCode.ShouldBe(
            HttpStatusCode.Created, "or the refusal is this format refusing everything rather than enforcing");
    }

    private static JsonObject WithField(string field, string value)
    {
        var body = Item();
        body[field] = value;
        return body;
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
