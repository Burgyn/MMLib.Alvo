using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using MMLib.Alvo.Api.Internal;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The generated OpenAPI document as a contract rather than as documentation. §0 principle 4 makes the
/// document the thing an agent reads instead of this source, and §6 makes it the reason a first-party SDK is a
/// convenience rather than a prerequisite — so every claim here is written against the served bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The snapshot proves stability, not correctness</b>, and that division is deliberate.
/// <see cref="The_document_is_stable"/> cannot tell a right document from a wrong one — it would have frozen a
/// document with no paths just as happily — so every substantive claim is asserted directly and the snapshot
/// is there to make drift a reviewed event.
/// </para>
/// <para>
/// <b>Every enumerating fact pins its count from outside the document.</b> "Each path has a description" over a
/// document with no paths passes trivially; the expected sets here come from the mapped endpoint table, from
/// the descriptor file, or from a literal, never from the same walk being measured.
/// </para>
/// <para>
/// <b>Two limitations this suite knowingly does not close, stated rather than left silent.</b>
/// <see cref="Every_documented_status_code_is_one_the_endpoint_can_actually_return"/>'s reverse direction —
/// "no reachable status goes undocumented" — is bounded by <see cref="ProbesAsync"/>'s own hand-written probe
/// list: a status the framework can reach but this suite never provokes (a 415, a 405) would be invisible to
/// it, the same "pin the set from outside" gap this PR has hit before. And
/// <see cref="The_filter_sort_and_paging_parameters_are_documented_per_list_route"/> validates the filter
/// parameter set only against the document's own read schema, never against what
/// <c>QueryStringParser</c> actually accepts per field type — so the document could in principle describe a
/// filter the parser refuses. Closing either is a real improvement; it is not attempted here.
/// </para>
/// </remarks>
public sealed class OpenApiDocumentTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>
    /// A key that authenticates and grants no scope at all, so every operation on every entity is 403
    /// out-of-scope.
    /// </summary>
    /// <remarks>
    /// An empty scope set rather than one naming another entity, because the document lists a 403 on all ten
    /// operations and one key has to reach every one of them: any non-empty scope would grant something. It is
    /// also the behaviour <c>ScopeGate</c> documents in its own summary — scopes are mandatory, and a key
    /// without them is not an all-powerful key.
    /// </remarks>
    private static readonly TestApiKey _narrow = new("narrow-key", ["authenticated"], []);

    /// <summary>A credential that was presented and cannot be resolved, so every operation is 401.</summary>
    private static readonly TestApiKey _ghost = new("ghost-key", ["admin"], ["*:read", "*:write"]);

    /// <summary>The two entities the fixture descriptor declares, and the five routes each of them gets.</summary>
    private static readonly string[] _entities = ["categories", "products"];

    private const int RoutesPerEntity = 5;

    /// <summary>
    /// .NET 10 emits OpenAPI 3.1 over JSON Schema draft 2020-12 by default, and #75 requires keeping it —
    /// the same draft as <c>schema/project.schema.json</c>.
    /// </summary>
    /// <remarks>
    /// The version string alone would be satisfied by a document that declared 3.1 and then described a
    /// nullable field the 3.0 way, so the second half is the load-bearing one: in draft 2020-12 nullability is
    /// a <em>type union</em>, and <c>nullable: true</c> is not a keyword at all. A downgrade to 3.0 — which is
    /// the concrete regression this fact exists to refuse — flips both.
    /// </remarks>
    [Fact]
    public async Task The_document_declares_openapi_3_1()
    {
        await using var world = await StoreAsync();

        var document = await world.OpenApiDocumentAsync();

        document["openapi"]!.GetValue<string>().ShouldStartWith("3.1");
        var nullable = Property(document, "products", "discontinued_at");
        nullable["type"]!.AsArray().Select(entry => entry!.GetValue<string>())
            .ShouldBe(["null", "string"], ignoreOrder: true, "3.1 spells a nullable field as a type union");
        nullable.ContainsKey("nullable").ShouldBeFalse("'nullable' is a 3.0 keyword and no longer a keyword at all");
    }

    /// <summary>
    /// The document's paths are exactly the routes that were mapped — in <b>both</b> directions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A one-directional check passes a document that describes routes nobody mapped, which is the drift §2.1's
    /// "consistent with actual behaviour" criterion is about. The mapped set is read off the endpoint table
    /// through <see cref="DataApiOperationMetadata"/> — the marker <c>DataApiEndpoints.Protect</c> attaches in
    /// the same call as the authorization filter — so it is Alvo's own endpoints and not a path-prefix guess.
    /// </para>
    /// <para>
    /// The count is pinned from outside so the equality cannot be satisfied by two empty sets.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_mapped_route_appears_in_the_document_and_nothing_else_does()
    {
        await using var world = await StoreAsync();

        var mapped = MappedRoutes(world);
        var documented = DocumentedRoutes(await world.OpenApiDocumentAsync());

        mapped.Count.ShouldBe(
            _entities.Length * RoutesPerEntity,
            $"or this fact is comparing the document against the wrong set: {string.Join(", ", mapped)}");
        documented.ShouldBe(mapped, "the document must describe every mapped route, and no path nobody mapped");
    }

    /// <summary>
    /// Every status the document lists is one a real request can actually produce, and every status a real
    /// request produces is listed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reachability is measured by driving requests, never by comparing the document with the table it was
    /// built from</b> — which would be the same claim twice. A catalogue entry for a status no path emits is
    /// documentation of a behaviour that does not exist, and the opposite direction catches a status a caller
    /// can reach and no document mentions.
    /// </para>
    /// <para>
    /// Both entities are provoked, because the two disagree about exactly one status: <c>products</c> is audited
    /// and can answer 304, <c>categories</c> is not and never can. A one-entity fixture would pass whichever
    /// way that went.
    /// </para>
    /// <para>
    /// Each provocation asserts the status it expected before it counts, so a provocation that silently stopped
    /// reaching its branch fails here — naming itself — rather than showing up as a missing status.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_documented_status_code_is_one_the_endpoint_can_actually_return()
    {
        await using var world = await StoreAsync();
        var documented = DocumentedStatuses(await world.OpenApiDocumentAsync());

        var observed = new SortedSet<string>(StringComparer.Ordinal);
        var categoryId = await CreateAsync(world, "categories", CategoryBody());
        foreach (var entity in _entities)
        {
            observed.UnionWith(await ProvokeEveryStatusAsync(world, entity, categoryId));
        }

        documented.Count.ShouldBe(
            55,
            "27 on the version-less entity and 28 on the audited one, whose read adds a 304 — pinned from "
            + "outside so the equality below cannot be satisfied by two empty sets. It went 51 -> 55 with "
            + "#138: an update and a delete can each now answer 409 when a database constraint refuses the "
            + "write, on both entities");
        observed.ShouldBe(documented, "a documented status no request reaches, or a status no document lists");
    }

    /// <summary>
    /// A field's <c>description</c> in the descriptor reaches the published schema — in all three of an
    /// entity's schemas and on the filter parameter the field contributes.
    /// </summary>
    /// <remarks>
    /// The expected sentence is read out of the descriptor file rather than restated here, so the fact really
    /// asserts "what the descriptor said" and cannot pass against a description the transformer invented. It
    /// covers four places because they are four separate code paths, and a fix that carried the description into
    /// the read schema alone would leave a client writing a body blind.
    /// </remarks>
    [Fact]
    public async Task A_field_description_from_the_descriptor_reaches_the_schema()
    {
        var declared = DescriptorFieldDescription("products", "name");
        await using var world = await StoreAsync();

        var document = await world.OpenApiDocumentAsync();

        declared.ShouldNotBeNullOrWhiteSpace("the fixture descriptor must describe the field, or this proves nothing");
        foreach (var schema in new[] { "products", "productsCreate", "productsPatch" })
        {
            Property(document, schema, "name")["description"]!.GetValue<string>().ShouldBe(
                declared, $"'{schema}' must carry the descriptor's own sentence for 'name'");
        }

        // A filter parameter says what the field is before it says how to filter on it, so a client offering
        // the parameter can label it.
        ListParameter(document, "products", "name")["description"]!.GetValue<string>()
            .ShouldStartWith(declared, Case.Sensitive);
    }

    /// <summary>An <c>enum</c> field's declared values reach the schema as a JSON Schema <c>enum</c>.</summary>
    /// <remarks>
    /// The values come from the descriptor file for the reason the description does. Asserted on all three
    /// schemas: a client that knew the allowed values on the way out and not on the way in would still have to
    /// discover them from a 422.
    /// </remarks>
    [Fact]
    public async Task An_enum_fields_declared_values_reach_the_schema_as_an_enum()
    {
        var declared = DescriptorEnumValues("products", "status");
        await using var world = await StoreAsync();

        var document = await world.OpenApiDocumentAsync();

        declared.ShouldBe(["draft", "active", "retired"], "the fixture's declared values, spelled out once");
        foreach (var schema in new[] { "products", "productsCreate", "productsPatch" })
        {
            var field = Property(document, schema, "status");
            field["enum"]!.AsArray().Select(value => value!.GetValue<string>()).ShouldBe(
                declared, $"'{schema}' must publish the declared values");
            field["type"]!.GetValue<string>().ShouldBe("string", "an Alvo enum travels as its member's name");
        }
    }

    /// <summary>
    /// An <b>optional</b> <c>hidden</c> field appears in no schema, no parameter and no prose — <b>a
    /// confidentiality fact, not a tidiness one</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hidden field's <em>name</em> in a public document is exactly the schema oracle the query parser's
    /// refusals and the port's deny reasons are worded to avoid: it answers "does this entity have a field
    /// called X" for every caller at once, including the ones the mask exists to keep out. That is the rule
    /// for an <em>optional</em> hidden field without qualification; a <c>required</c> one is the narrow,
    /// deliberate exception <see cref="A_required_hidden_field_is_published_in_write_schemas_only"/> covers.
    /// </para>
    /// <para>
    /// <b>It is asserted structurally, over the document's own property and parameter names, not as a substring
    /// search.</b> The fixture's hidden field is called <c>cost</c>, and the document legitimately contains the
    /// English word "cost" several times in Alvo's own prose ("an ignored key costs nothing") — so a substring
    /// assertion would fail for the wrong reason, and a fix for that would have been to weaken it. The field's
    /// own <em>description</em> is a long distinctive sentence, so that one is checked as an exact phrase.
    /// </para>
    /// <para>
    /// A visible sibling of the same type is asserted present in the same walk, or "the name is nowhere" would
    /// also hold for a document with no schemas at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_optional_hidden_field_appears_in_no_schema_at_all()
    {
        var confidential = DescriptorFieldDescription("products", "cost");
        await using var world = await StoreAsync();

        var text = await world.OpenApiTextAsync();
        var names = EveryDeclaredName(JsonNode.Parse(text)!.AsObject());

        confidential.ShouldNotBeNullOrWhiteSpace(
            "the fixture's hidden field must carry a description, or the phrase check below asserts nothing");
        names.ShouldContain("price", "a visible decimal must be published, or this fact holds over an empty document");
        names.ShouldNotContain("cost", "an optional hidden field's name must appear in no schema and no parameter");
        text.ShouldNotContain(
            confidential, Case.Sensitive, "the hidden field's own description must not reach the document either");
    }

    /// <summary>
    /// A <c>hidden</c> field the descriptor also marks <c>required</c> is the one deliberate exception: its
    /// name reaches the two write schemas — never a response, and never a filter parameter — because a
    /// mandatory field a caller cannot see could not be supplied at all.
    /// </summary>
    /// <remarks>
    /// This is the narrowed rule a reviewer's objection produced: excluding <em>every</em> hidden field from
    /// every request schema would silently drop a field the caller is required to send, so the create becomes
    /// unperformable by anyone reading only the document. The fixture's field is called
    /// <c>activation_secret</c> — a mandatory activation token, exactly the "must be supplied, never returned"
    /// shape the ruling is written for.
    /// </remarks>
    [Fact]
    public async Task A_required_hidden_field_is_published_in_write_schemas_only()
    {
        var declared = DescriptorFieldDescription("products", "activation_secret");
        await using var world = await StoreAsync();

        var document = await world.OpenApiDocumentAsync();

        declared.ShouldNotBeNullOrWhiteSpace(
            "the fixture's required-and-hidden field must carry a description, or this fact proves nothing");
        foreach (var schema in new[] { "productsCreate", "productsPatch" })
        {
            Property(document, schema, "activation_secret")["description"]!.GetValue<string>().ShouldBe(
                declared, $"'{schema}' must publish the required hidden field, since a caller must know its "
                + "name to supply it");
        }

        Component(document, "schemas", "productsCreate")["required"]!.AsArray()
            .Select(value => value!.GetValue<string>()).ShouldContain(
                "activation_secret", "a hidden field the descriptor requires must still be required to create");
        Component(document, "schemas", "products")["properties"]!.AsObject()
            .ContainsKey("activation_secret").ShouldBeFalse(
                "a required hidden field must still carry no value in any response — 'required' governs a "
                + "write, not what a read returns");
        ListParameterNames(document, "products").ShouldNotContain(
            "activation_secret", "a required hidden field must still contribute no filter parameter");
    }

    /// <summary>
    /// The problem document is one component, and every refusal response resolves to it.
    /// </summary>
    /// <remarks>
    /// One shape for every refusal is what makes <c>type</c> the thing a client branches on: a per-status shape
    /// would have a generated client emit one type per status code and branch on the status it already read off
    /// the response line. The <c>type</c> enumeration is compared with <see cref="AlvoProblemTypes.All"/>, which
    /// <c>ProblemDetailsTests</c> separately holds to being exactly what the framework emits — so the document
    /// cannot enumerate a classification no refusal carries.
    /// </remarks>
    [Fact]
    public async Task The_problem_details_shape_is_a_component_referenced_by_every_error_response()
    {
        await using var world = await StoreAsync();
        var document = await world.OpenApiDocumentAsync();

        var refusals = Refusals(document).ToList();

        refusals.Count.ShouldBe(
            44,
            "twenty-two per entity — three on a list and a read, five on a create, six on an update and five "
            + "on a delete, the last two having each gained the 409 #138 made reachable");
        foreach (var (route, status, response) in refusals)
        {
            var content = Resolve(document, response)["content"]!.AsObject();
            content.ContainsKey("application/problem+json").ShouldBeTrue(
                $"{route} {status} must be an RFC 9457 problem document");
            content["application/problem+json"]!["schema"]!["$ref"]!.GetValue<string>().ShouldBe(
                "#/components/schemas/" + ProblemComponents.DocumentId,
                $"{route} {status} must reference the one problem shape, not a copy of it");
        }

        Component(document, "schemas", ProblemComponents.DocumentId)["properties"]!["type"]!["enum"]!
            .AsArray().Select(value => value!.GetValue<string>()).Order(StringComparer.Ordinal).ShouldBe(
                AlvoProblemTypes.All.Select(AlvoProblemTypes.UriOf).Order(StringComparer.Ordinal),
                "the enumeration is the whole catalogue, so a client needs to parse no prose");
    }

    /// <summary>
    /// Every list route documents the whole query surface: the reserved parameters and one per filterable field.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expected field set is the entity's own read schema, which is the other half of the document — so the
    /// two halves have to agree, and neither can be the source of its own expectation. The reserved names come
    /// from <see cref="ReservedQueryKeys.All"/>, so a keyword added to the parser without a parameter fails
    /// here.
    /// </para>
    /// <para>
    /// The two published bounds are asserted against the option and the parser constant that enforce them,
    /// because a documented bound that drifted from the enforced one refuses a value the document promised.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_filter_sort_and_paging_parameters_are_documented_per_list_route()
    {
        await using var world = await StoreAsync();
        var document = await world.OpenApiDocumentAsync();

        foreach (var entity in _entities)
        {
            var declared = Component(document, "schemas", entity)["properties"]!.AsObject()
                .Select(property => property.Key);
            var expected = ReservedQueryKeys.All.Except([ReservedQueryKeys.Not], StringComparer.Ordinal)
                .Concat(declared).Order(StringComparer.Ordinal);

            ListParameterNames(document, entity).Order(StringComparer.Ordinal).ShouldBe(
                expected, $"'{entity}' must document every reserved parameter and every filterable field");
        }

        ListParameter(document, "products", ReservedQueryKeys.Limit)["schema"]!["maximum"]!.GetValue<int>()
            .ShouldBe(new AlvoApiOptions().MaxPageSize, "the published maximum is the one the parser enforces");
        ListParameter(document, "products", ReservedQueryKeys.After)["schema"]!["maxLength"]!.GetValue<int>()
            .ShouldBe(QueryStringParser.MaxCursorLength, "the published cursor bound is the one the parser enforces");
    }

    /// <summary>
    /// The list is the one operation that reads <c>Prefer</c>, and the one whose 200 can answer with
    /// <c>Preference-Applied</c>. Both are published, because an opt-in nothing announces is one no agent
    /// finds — and neither appears on an operation that would ignore them.
    /// </summary>
    [Fact]
    public async Task The_count_preference_is_documented_on_the_list_and_nowhere_else()
    {
        await using var world = await StoreAsync();
        var document = await world.OpenApiDocumentAsync();

        ListParameter(document, "products", PreferHeader.Name)["in"]!.GetValue<string>().ShouldBe("header");
        ResponseHeaders(document, "/api/products", "get", "200").ShouldContain(PreferHeader.AppliedName);

        ResponseHeaders(document, "/api/products", "post", "201").ShouldNotContain(PreferHeader.AppliedName);
        Parameters(document, "/api/products", "post").ShouldNotContain(PreferHeader.Name);
    }

    /// <summary>The envelope publishes the count as a required, nullable member, exactly like <c>next</c>.</summary>
    [Fact]
    public async Task The_page_envelope_publishes_the_count_as_a_required_nullable_member()
    {
        await using var world = await StoreAsync();
        var document = await world.OpenApiDocumentAsync();
        var page = Component(document, "schemas", "productsPage");

        page["required"]!.AsArray().Select(name => name!.GetValue<string>())
            .ShouldBe(["items", "next", "count"], ignoreOrder: true);
        page["properties"]!["count"]!["type"]!.AsArray().Select(type => type!.GetValue<string>())
            .ShouldBe(["integer", "null"], ignoreOrder: true);
    }

    private static IEnumerable<string> ResponseHeaders(
        JsonObject document, string path, string verb, string status) =>
        (document["paths"]![path]![verb]!["responses"]![status]!["headers"]?.AsObject()
            ?? new JsonObject()).Select(header => header.Key);

    private static IEnumerable<string> Parameters(JsonObject document, string path, string verb) =>
        (document["paths"]![path]![verb]!["parameters"]?.AsArray() ?? [])
        .Select(parameter => Dereference(document, parameter!.AsObject())["name"]!.GetValue<string>());

    /// <summary>
    /// A shared parameter or header no mapped operation could ever reference is not published — an orphan
    /// component is the same defect the <c>ProducesProblem</c> deviation avoids for a schema.
    /// </summary>
    /// <remarks>
    /// This fixture has an audited entity (<c>products</c>) and no tenant-scoped one, so it proves one
    /// direction of each pair: the <c>ifNoneMatch</c> parameter and the <c>ETag</c> header component must be
    /// published (something references them), and the <c>tenant</c> parameter must not be (nothing does). The
    /// other direction — a tenant-scoped entity publishing <c>tenant</c> and, having no audited entity,
    /// publishing no <c>ifNoneMatch</c> — is <see cref="A_shared_parameter_reaches_the_entity_that_references_it_and_no_orphan_is_published"/>,
    /// over the project's own <c>tenant-notes</c> fixture rather than a further-loaded copy of this one.
    /// </remarks>
    [Fact]
    public async Task A_shared_parameter_or_header_unused_by_any_entity_is_not_published()
    {
        await using var world = await StoreAsync();
        var document = await world.OpenApiDocumentAsync();

        HasComponent(document, "parameters", "ifNoneMatch").ShouldBeTrue(
            "'products' is audited, so the shared 'ifNoneMatch' parameter must be published");
        HasComponent(document, "headers", "ETag").ShouldBeTrue(
            "'products' is audited, so the shared 'ETag' header must be published");
        HasComponent(document, "parameters", "tenant").ShouldBeFalse(
            "no entity in this fixture is tenant-scoped, so the shared 'tenant' parameter must not be an orphan");
    }

    /// <summary>
    /// The reciprocal of <see cref="A_shared_parameter_or_header_unused_by_any_entity_is_not_published"/>: a
    /// shared parameter really is published once some operation references it.
    /// </summary>
    /// <remarks>
    /// Over the project's own <c>tenant-notes</c> fixture — one tenant-scoped, non-audited entity — rather
    /// than a tenant-scoped entity added to <c>documented-store</c>: that fixture and this test class's forty
    /// pinned counts and one 1517-line snapshot are already load-bearing for six other facts, and
    /// <c>tenant-notes</c> already exists, purpose-built, and is exercised elsewhere
    /// (<c>DataApiAuthTests</c>). Reusing it proves the same production behaviour — <c>Reusable</c> publishing
    /// a shared component if and only if something in the document references it — at a fraction of the risk
    /// a bigger fixture would add for the same fact.
    /// </remarks>
    [Fact]
    public async Task A_shared_parameter_reaches_the_entity_that_references_it_and_no_orphan_is_published()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "tenant-notes.alvo.json", [], new AlvoApiWorldSetup(MapOpenApiDocument: true));
        var document = await world.OpenApiDocumentAsync();

        HasComponent(document, "parameters", "tenant").ShouldBeTrue(
            "'notes' is tenant-scoped, so the shared 'tenant' parameter must be published");
        HasComponent(document, "parameters", "ifNoneMatch").ShouldBeFalse(
            "'notes' carries no version, so the shared 'ifNoneMatch' parameter must not be an orphan");
        HasComponent(document, "headers", "ETag").ShouldBeFalse(
            "'notes' carries no version, so the shared 'ETag' header must not be an orphan");
    }

    /// <summary>
    /// <b>A write is offered <c>If-Match</c> only on an entity that can issue an <c>ETag</c></b> — and is
    /// offered it on one that can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one-directional version of this fact is worthless, which is why both directions are here.</b>
    /// "<c>categories</c> declares no <c>ifMatch</c>" passes just as well on a document that advertises the
    /// parameter nowhere at all — including on one where the whole precondition surface was accidentally
    /// dropped. The <c>products</c> half is what makes the <c>categories</c> half mean "conditional on the
    /// entity" rather than "absent".
    /// </para>
    /// <para>
    /// <b>What it refuses.</b> The version conditionality was applied on the read side and on the write side
    /// nowhere: <c>ifNoneMatch</c> was entity-conditional from the start, while <c>Update</c>/<c>Delete</c>
    /// published <c>ifMatch</c> unconditionally. So the shipped document told a <c>categories</c> client to
    /// "send back one <c>ETag</c> exactly as a previous response returned it" while that entity's 200 declares
    /// no <c>ETag</c> header — correctly, since <c>RowVersionETag.For</c> mints none — and
    /// <c>AlvoPrecondition.EnsureSupported</c> refuses any version precondition on it. Every tag such a client
    /// could invent is 412 forever. §0 principle 4 makes this document the contract an agent reads, and a
    /// contract that instructs a client into a permanent refusal is worse than one that stays silent.
    /// </para>
    /// <para>
    /// <b>The 412 itself stays on both, and that is deliberate rather than an oversight this fact tolerates.</b>
    /// It is reachable on a version-less write — an <c>If-Match</c> naming a version is refused, and so is any
    /// <c>If-None-Match</c> — so removing the status would document a behaviour the endpoint has. What narrows
    /// is the sentence: the operation's own 412 entry carries a description saying the status can only mean "a
    /// precondition this entity cannot answer" there, never "the version did not match". Asserted below,
    /// because a narrowing nothing reads is a narrowing that silently stops being emitted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_write_offers_If_Match_only_on_an_entity_whose_rows_carry_a_version()
    {
        await using var world = await StoreAsync();
        var document = await world.OpenApiDocumentAsync();

        foreach (var method in _writeMethodsOnOneRow)
        {
            RowParameterNames(document, "categories", method).ShouldNotContain(
                "If-Match",
                $"'categories' mints no ETag, so its {method} must not invite a header whose every value is 412");
            RowParameterNames(document, "products", method).ShouldContain(
                "If-Match",
                $"'products' is audited, so its {method} must offer the precondition — or the assertion above "
                + "passes on a document that advertises 'If-Match' nowhere");
        }

        foreach (var method in _writeMethodsOnOneRow)
        {
            RowOperation(document, "categories", method)["responses"]!["412"]!["description"]!.GetValue<string>()
                .ShouldContain(
                    "never means",
                    Case.Sensitive,
                    $"'categories' {method}'s 412 must narrow the shared wording — only one of its two arms "
                    + "can fire on an entity with no version to compare");
            RowOperation(document, "products", method)["responses"]!["412"]!.AsObject()
                .ContainsKey("description").ShouldBeFalse(
                    $"'products' {method} can mean either arm, so it takes the shared wording unchanged");
        }
    }

    /// <summary>
    /// The row a single read, a create or an update returns requires every visible field; the page item a
    /// list's rows are requires none — because <c>select</c> can narrow only the page.
    /// </summary>
    /// <remarks>
    /// <c>GetAsync</c> takes no projection and <c>MapGet</c> parses no query for the other two operations, so
    /// none of the three can return a partial row — which is what makes <c>required</c> a real claim on
    /// <c>products</c> rather than a false one. <c>select</c> narrows only the page, so
    /// <c>productsPageItem</c> keeping no <c>required</c> list is the honest reflection of that, not an
    /// oversight the row schema happens to fix.
    /// </remarks>
    [Fact]
    public async Task The_row_schema_requires_every_visible_field_while_the_page_item_schema_requires_none()
    {
        await using var world = await StoreAsync();
        var document = await world.OpenApiDocumentAsync();

        var row = Component(document, "schemas", "products");
        var pageItem = Component(document, "schemas", "productsPageItem");
        var required = row["required"]!.AsArray().Select(value => value!.GetValue<string>()).ToList();

        required.ShouldContain("id", "a single read always carries the row's key, projection or not");
        required.ShouldContain("name", "a single read always carries a declared field's value, even if null");
        pageItem.ContainsKey("required").ShouldBeFalse(
            "select narrows a page's rows, so nothing in the page item schema can be mandatory");
        Component(document, "schemas", "productsPage")["properties"]!["items"]!["items"]!["$ref"]!
            .GetValue<string>().ShouldBe(
                "#/components/schemas/productsPageItem",
                "the page's rows must reference the unconstrained item schema, not the row schema and its "
                + "required list");
    }

    /// <summary>
    /// However many times a host registers Alvo, the overview paragraph is appended to
    /// <c>info.description</c> exactly once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fact <c>ApiSetup.cs</c>'s own remarks on <see cref="AlvoOpenApiSetup"/> name: the
    /// transformer used to be registered once per <c>AddAlvoApi</c> call, and since
    /// <c>AddOpenApi(configure)</c> is additive, the same document got enriched twice — the overview paragraph
    /// appeared verbatim in <c>info.description</c> twice over.
    /// </para>
    /// <para>
    /// <b>The world registers <c>AddAlvo</c> twice, and it has to.</b> The duplication used to come for free,
    /// because <c>AddDataApi</c> called <c>AddAlvoApi</c> a second time; now that the Data API is on by default
    /// and <c>AddDataApi</c> only configures it, a single <c>AddAlvo</c> registers the transformer exactly once
    /// whether the registration deduplicates or not — so this fact would have gone on passing while the defect
    /// it exists for was reachable again. Two <c>AddAlvo</c> calls is the shape two libraries each registering
    /// the framework produce, which <c>AddAlvo</c>'s own remarks already promise to support.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_overview_is_appended_once_however_often_alvo_is_registered()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "documented-store.alvo.json",
            [_admin, _narrow],
            new AlvoApiWorldSetup(MapOpenApiDocument: true, RegisterAlvoTwice: true));
        var document = await world.OpenApiDocumentAsync();

        var description = document["info"]!["description"]!.GetValue<string>();

        Occurrences(description, DataApiDocumentation.Overview).ShouldBe(
            1, "a host may register Alvo twice, and neither registration may enrich the document a second time");
    }

    /// <summary>
    /// A host that already wrote its own <c>info.description</c> keeps it — Alvo appends its own overview
    /// after it, rather than overwriting what the host said.
    /// </summary>
    /// <remarks>
    /// The report that shipped this feature recorded this exact gap: replacing the append with a plain
    /// overwrite of the host's <c>info.description</c> left every other fact and the snapshot green, because
    /// the fixture host wrote no description of its own — so nothing before this fact could have caught it.
    /// <see cref="AlvoApiWorldSetup.HostInfoDescription"/> is written by a document transformer the world
    /// registers <em>before</em> <c>AddAlvo</c>, so it runs before Alvo's own and really stands in for a host
    /// that documents itself first.
    /// </remarks>
    [Fact]
    public async Task The_hosts_own_description_is_kept_and_the_overview_is_appended_after_it()
    {
        const string hostDescription = "This host's own words about its own API, written before Alvo's.";
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "documented-store.alvo.json",
            [_admin, _narrow],
            new AlvoApiWorldSetup(MapOpenApiDocument: true, HostInfoDescription: hostDescription));
        var document = await world.OpenApiDocumentAsync();

        var description = document["info"]!["description"]!.GetValue<string>();

        description.ShouldStartWith(hostDescription, Case.Sensitive, "the host's own text must survive, first");
        description.ShouldContain(
            DataApiDocumentation.Overview, Case.Sensitive, "Alvo's overview must still be appended after it");
    }

    /// <summary>
    /// Every entity's document-level tag carries the descriptor's own description — not just the bare name
    /// <c>DataApiEndpoints.WithTags</c> puts there before the transformer runs.
    /// </summary>
    /// <remarks>
    /// The report that shipped this feature recorded the defect this guards: <c>OpenApiTag</c>'s equality is
    /// by name, so a naive "add a second, described tag" was silently discarded by the <see cref="HashSet{T}"/>
    /// that already held the bare one, and every tag published as name-only while the descriptor described
    /// every entity.
    /// </remarks>
    [Fact]
    public async Task Every_entity_tag_carries_the_descriptors_own_description()
    {
        await using var world = await StoreAsync();
        var document = await world.OpenApiDocumentAsync();

        foreach (var entity in _entities)
        {
            TagDescription(document, entity).ShouldBe(
                DescriptorEntityDescription(entity),
                $"'{entity}''s tag must carry the descriptor's own description, not a bare name");
        }
    }

    /// <summary>
    /// The whole document, frozen — so a change to any published byte is a reviewed event rather than a
    /// surprise for an integrator whose generated client stops compiling.
    /// </summary>
    /// <remarks>
    /// <b>It proves stability and nothing else.</b> Every substantive claim is a fact of its own above, because
    /// a snapshot cannot tell a right document from a wrong one; what it can do, and what nothing else does, is
    /// make a wording or a shape moving impossible to land silently.
    /// </remarks>
    [Fact]
    public async Task The_document_is_stable()
    {
        await using var world = await StoreAsync();

        await Verify(await world.OpenApiTextAsync());
    }

    /// <summary>The fixture: one audited entity and one that is not, and the document served over HTTP.</summary>
    private static Task<AlvoApiWorld> StoreAsync() =>
        AlvoApiWorld.FromDescriptorAsync(
            "documented-store.alvo.json",
            [_admin, _narrow],
            new AlvoApiWorldSetup(MapOpenApiDocument: true));

    /// <summary>Every route Alvo mapped, as <c>METHOD path</c> in the document's own path spelling.</summary>
    /// <remarks>
    /// The route constraint is stripped here independently of the production code that strips it: OpenAPI has no
    /// notion of one, so <c>/api/products/{id:guid}</c> as mapped is <c>/api/products/{id}</c> as documented. A
    /// constraint this does not know about leaves the pattern unchanged and the comparison fails, which is the
    /// loud outcome.
    /// </remarks>
    private static SortedSet<string> MappedRoutes(AlvoApiWorld world) =>
        [.. world.Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<DataApiOperationMetadata>() is not null)
            .SelectMany(endpoint => endpoint.Metadata
                .GetMetadata<IHttpMethodMetadata>()!.HttpMethods
                .Select(method => $"{method} {DocumentPath(endpoint)}"))];

    private static string DocumentPath(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText!.Replace(":guid", string.Empty, StringComparison.Ordinal);

    /// <summary>Every <c>METHOD path</c> the document describes.</summary>
    private static SortedSet<string> DocumentedRoutes(JsonObject document) =>
        [.. document["paths"]!.AsObject().SelectMany(
            path => path.Value!.AsObject().Select(
                operation => $"{operation.Key.ToUpperInvariant()} {path.Key}"))];

    /// <summary>Every <c>&lt;operationId&gt; &lt;status&gt;</c> pair the document lists.</summary>
    private static SortedSet<string> DocumentedStatuses(JsonObject document) =>
        [.. Operations(document).SelectMany(
            operation => operation["responses"]!.AsObject().Select(
                response => $"{operation["operationId"]!.GetValue<string>()} {response.Key}"))];

    private static IEnumerable<JsonObject> Operations(JsonObject document) =>
        document["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject())
            .Select(operation => operation.Value!.AsObject());

    /// <summary>Every refusal response the document lists, as (route, status, response object).</summary>
    private static IEnumerable<(string Route, string Status, JsonObject Response)> Refusals(JsonObject document) =>
        from path in document["paths"]!.AsObject()
        from method in path.Value!.AsObject()
        from response in method.Value!.AsObject()["responses"]!.AsObject()
        where int.Parse(response.Key, CultureInfo.InvariantCulture) >= 400
        select ($"{method.Key.ToUpperInvariant()} {path.Key}", response.Key, response.Value!.AsObject());

    /// <summary>
    /// A response object, following a <c>$ref</c> into <c>components.responses</c> when it is one.
    /// </summary>
    /// <remarks>
    /// The refusals are published once and referenced, so a fact that only understood inline objects would
    /// assert nothing at all about them.
    /// </remarks>
    private static JsonObject Resolve(JsonObject document, JsonObject response) =>
        response["$ref"] is { } reference
            ? Component(document, "responses", reference.GetValue<string>().Split('/')[^1])
            : response;

    private static JsonObject Component(JsonObject document, string map, string id) =>
        document["components"]![map]![id]?.AsObject()
        ?? throw new InvalidOperationException($"The document declares no '{map}/{id}' component.");

    /// <summary>Whether the document declares a component under <c>components/&lt;map&gt;/&lt;id&gt;</c>.</summary>
    /// <remarks>
    /// Unlike <see cref="Component"/>, absence is the expected answer half the time this is called — an
    /// orphan-avoidance fact needs to assert "not published" as much as "published" — so this reports rather
    /// than throws.
    /// </remarks>
    private static bool HasComponent(JsonObject document, string map, string id) =>
        document["components"]?[map]?.AsObject()?.ContainsKey(id) == true;

    /// <summary>How many times <paramref name="needle"/> occurs in <paramref name="haystack"/>, non-overlapping.</summary>
    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>One entity's document-level tag description, or <see langword="null"/> if it carries none.</summary>
    private static string? TagDescription(JsonObject document, string entity) =>
        document["tags"]!.AsArray()
            .Select(tag => tag!.AsObject())
            .FirstOrDefault(tag => string.Equals(tag["name"]!.GetValue<string>(), entity, StringComparison.Ordinal))
            ?["description"]?.GetValue<string>();

    private static JsonObject Property(JsonObject document, string schema, string field) =>
        Component(document, "schemas", schema)["properties"]![field]?.AsObject()
        ?? throw new InvalidOperationException($"Schema '{schema}' declares no '{field}' property.");

    /// <summary>
    /// Every name the document declares anywhere a field's name could surface: a schema property or a
    /// parameter. A response header is not one of them — every header this document publishes
    /// (<c>ETag</c>, <c>Location</c>, <c>Cache-Control</c>, <c>WWW-Authenticate</c>) is a fixed, non-field
    /// name, so there is nothing for this walk to find there.
    /// </summary>
    /// <remarks>
    /// Structural rather than textual on purpose — see the confidentiality fact's own remarks for why a
    /// substring search over this document is the wrong instrument. Absence of the schema map throws with an
    /// explanation, exactly as <see cref="Component"/> does for one component inside it, rather than the bare
    /// NRE a direct <c>document["components"]!["schemas"]!</c> would produce.
    /// </remarks>
    private static HashSet<string> EveryDeclaredName(JsonObject document)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var schemas = document["components"]?["schemas"]?.AsObject()
            ?? throw new InvalidOperationException("The document declares no 'components/schemas' map.");
        foreach (var schema in schemas)
        {
            foreach (var property in schema.Value!.AsObject()["properties"]?.AsObject() ?? [])
            {
                names.Add(property.Key);
            }
        }

        foreach (var parameter in AllParameters(document))
        {
            names.Add(parameter["name"]!.GetValue<string>());
        }

        return names;
    }

    /// <summary>Every parameter the document declares, whether shared or inlined on an operation.</summary>
    /// <remarks>
    /// Absence of the parameter map throws with an explanation rather than the bare NRE a
    /// <c>document["components"]!["parameters"]!</c> would produce — the same reason, and the same shape, as
    /// <see cref="EveryDeclaredName"/>. Unreachable while every generated document declares the shared paging
    /// parameters; a walk over this document is a confidentiality control, and a control that fails as an NRE
    /// tells its reader nothing about which invariant broke.
    /// </remarks>
    private static IEnumerable<JsonObject> AllParameters(JsonObject document) =>
        (document["components"]?["parameters"]?.AsObject()
            ?? throw new InvalidOperationException("The document declares no 'components/parameters' map."))
        .Select(parameter => parameter.Value!.AsObject())
            .Concat(Operations(document)
                .SelectMany(operation => operation["parameters"]?.AsArray() ?? [])
                .Select(parameter => parameter!.AsObject())
                .Where(parameter => parameter["$ref"] is null));

    /// <summary>
    /// One list route's <b>query</b> parameter names, with every <c>$ref</c> followed. Scoped to the query
    /// string on purpose: a list also carries a <c>Prefer</c> header parameter, and the fact this feeds is
    /// that the filter/sort/paging grammar is published in full, not that a list takes no headers.
    /// </summary>
    private static IEnumerable<string> ListParameterNames(JsonObject document, string entity) =>
        ListParameters(document, entity)
            .Where(parameter => string.Equals(parameter["in"]!.GetValue<string>(), "query", StringComparison.Ordinal))
            .Select(parameter => parameter["name"]!.GetValue<string>());

    private static JsonObject ListParameter(JsonObject document, string entity, string name) =>
        ListParameters(document, entity).FirstOrDefault(
            parameter => string.Equals(parameter["name"]!.GetValue<string>(), name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"The list route for '{entity}' documents no '{name}' parameter.");

    private static IEnumerable<JsonObject> ListParameters(JsonObject document, string entity) =>
        (document["paths"]![$"/api/{entity}"]!["get"]!["parameters"]?.AsArray() ?? [])
        .Select(parameter => Dereference(document, parameter!.AsObject()));

    /// <summary>The two verbs that write one addressed row, and so the two that can be conditioned.</summary>
    /// <remarks>
    /// A create is deliberately not one of them: it addresses a collection and refuses <em>both</em>
    /// precondition headers on every entity, audited or not, because there is no stored record for a version to
    /// be compared against — so its 412 needs no per-entity narrowing.
    /// </remarks>
    private static readonly string[] _writeMethodsOnOneRow = ["patch", "delete"];

    /// <summary>
    /// The header and path parameter <em>names</em> one single-row operation documents, with every
    /// <c>$ref</c> followed.
    /// </summary>
    /// <remarks>
    /// The names rather than the component ids, because a name is what a caller puts on the wire — and the two
    /// differ (<c>ifMatch</c> publishes <c>If-Match</c>), so asserting the id would let a component keep its id
    /// while publishing a different header.
    /// </remarks>
    private static IEnumerable<string> RowParameterNames(JsonObject document, string entity, string method) =>
        (RowOperation(document, entity, method)["parameters"]?.AsArray() ?? [])
        .Select(parameter => Dereference(document, parameter!.AsObject())["name"]!.GetValue<string>());

    /// <summary>One operation on the <c>/api/&lt;entity&gt;/{id}</c> path.</summary>
    private static JsonObject RowOperation(JsonObject document, string entity, string method) =>
        document["paths"]![$"/api/{entity}/{{id}}"]![method]?.AsObject()
        ?? throw new InvalidOperationException($"The document declares no '{method} /api/{entity}/{{id}}'.");

    private static JsonObject Dereference(JsonObject document, JsonObject parameter) =>
        parameter["$ref"] is { } reference
            ? Component(document, "parameters", reference.GetValue<string>().Split('/')[^1])
            : parameter;

    /// <summary>A field's <c>description</c> as the fixture descriptor itself declares it.</summary>
    /// <remarks>
    /// Read from the file so "the descriptor's description reaches the document" is a claim about the descriptor
    /// and not about a literal repeated in this test beside the one in the JSON.
    /// </remarks>
    private static string? DescriptorFieldDescription(string entity, string field) =>
        DescriptorField(entity, field)["description"]?.GetValue<string>();

    private static IReadOnlyList<string> DescriptorEnumValues(string entity, string field) =>
        [.. DescriptorField(entity, field)["values"]!.AsArray().Select(value => value!.GetValue<string>())];

    private static JsonObject DescriptorField(string entity, string field) =>
        JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "descriptors", "documented-store.alvo.json")))!
        ["entities"]![entity]!["fields"]![field]!.AsObject();

    /// <summary>One entity's own <c>description</c> as the fixture descriptor declares it.</summary>
    private static string? DescriptorEntityDescription(string entity) =>
        JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "descriptors", "documented-store.alvo.json")))!
        ["entities"]![entity]!["description"]?.GetValue<string>();

    private static JsonObject CategoryBody() => new() { ["name"] = "Hand tools" };

    private static JsonObject ProductBody(Guid categoryId) => new()
    {
        ["name"] = "Claw hammer",
        ["status"] = "draft",
        ["category_id"] = categoryId.ToString(),
        ["activation_secret"] = "tok-abcdef0123456789",
    };

    private static JsonObject Body(string entity, Guid categoryId) =>
        string.Equals(entity, "categories", StringComparison.Ordinal) ? CategoryBody() : ProductBody(categoryId);

    /// <summary>
    /// A body the entity's declared shape refuses — an over-long <c>name</c> on both, since both declare one.
    /// </summary>
    private static JsonObject RefusedBody(string entity, Guid categoryId)
    {
        var body = Body(entity, categoryId);
        body["name"] = new string('n', 500);
        return body;
    }

    /// <summary>Creates one row and returns its id, failing loudly if the seed itself did not work.</summary>
    private static async Task<Guid> CreateAsync(AlvoApiWorld world, string entity, JsonObject body)
    {
        using var response = await world.SendAsync(HttpMethod.Post, $"/api/{entity}", _admin, body: body);
        response.StatusCode.ShouldBe(
            HttpStatusCode.Created, $"the '{entity}' seed must succeed, or every fact built on it is vacuous");
        return (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    /// <summary>
    /// Drives one request per status this entity's five operations can answer with, asserting each got what it
    /// went for, and returns the <c>&lt;operationId&gt; &lt;status&gt;</c> pairs observed.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ProvokeEveryStatusAsync(
        AlvoApiWorld world, string entity, Guid categoryId)
    {
        var observed = new List<string>();
        foreach (var probe in await ProbesAsync(world, entity, categoryId))
        {
            using var response = await world.SendRawAsync(
                probe.Method,
                probe.Path,
                probe.Key,
                content: probe.Body is null ? null : AlvoApiWorld.RawJson(probe.Body.ToJsonString()),
                headers: probe.Headers);

            ((int)response.StatusCode).ShouldBe(
                probe.Status, $"{probe.Method} {probe.Path} no longer reaches the {probe.Status} it was written for");
            observed.Add($"{entity}.{probe.Operation} {probe.Status}");
        }

        return observed;
    }

    /// <summary>
    /// The request per documented status, for one entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is <c>async</c> because three probes are statements about a request that came before: the 200/204 need
    /// a row, the 304 needs that row's current entity tag, and the 409 needs a key already spent on a different
    /// body. Two rows are created — one for the reads and updates, one for the delete to consume — so no probe
    /// depends on the order of another.
    /// </para>
    /// <para>
    /// A 304 is listed only for an entity whose rows carry a version, which is the same condition the document
    /// publishes it under; on a version-less entity there is no tag to send and the status is unreachable.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<Probe>> ProbesAsync(
        AlvoApiWorld world, string entity, Guid categoryId)
    {
        var collection = $"/api/{entity}";
        var row = $"{collection}/{await CreateAsync(world, entity, Body(entity, categoryId))}";
        var doomed = $"{collection}/{await CreateAsync(world, entity, Body(entity, categoryId))}";
        var absent = $"{collection}/{Guid.NewGuid()}";
        var spent = Header("Idempotency-Key", await SpendAnIdempotencyKeyAsync(world, entity, categoryId));
        var stale = Header("If-Match", StaleTag);
        var body = Body(entity, categoryId);
        var taken = await TakeAUniqueValueAsync(world, entity, categoryId);
        var referenced = await ReferencedRowAsync(world, entity, categoryId);

        return
        [
            .. Gated(collection, "list", HttpMethod.Get),
            new("list", 200, HttpMethod.Get, collection, _admin, null),
            new("list", 422, HttpMethod.Get, $"{collection}?limit=0", _admin, null),

            .. Gated(row, "get", HttpMethod.Get),
            new("get", 200, HttpMethod.Get, row, _admin, null),
            new("get", 404, HttpMethod.Get, absent, _admin, null),
            .. await NotModifiedAsync(world, row),

            .. Gated(collection, "create", HttpMethod.Post, body),
            new("create", 201, HttpMethod.Post, collection, _admin, body),
            new("create", 422, HttpMethod.Post, collection, _admin, RefusedBody(entity, categoryId)),
            new("create", 412, HttpMethod.Post, collection, _admin, body, stale),
            new("create", 409, HttpMethod.Post, collection, _admin, RenamedBody(entity, categoryId), spent),

            .. Gated(row, "update", HttpMethod.Patch, body),
            new("update", 200, HttpMethod.Patch, row, _admin, Rename()),
            new("update", 404, HttpMethod.Patch, absent, _admin, Rename()),
            new("update", 422, HttpMethod.Patch, row, _admin, Overlong()),
            new("update", 412, HttpMethod.Patch, row, _admin, Rename(), stale),
            new("update", 409, HttpMethod.Patch, row, _admin, TakenUniqueValue(entity, taken)),

            .. Gated(row, "delete", HttpMethod.Delete),
            new("delete", 412, HttpMethod.Delete, row, _admin, null, stale),
            new("delete", 404, HttpMethod.Delete, absent, _admin, null),
            new("delete", 409, HttpMethod.Delete, referenced, _admin, null),
            new("delete", 204, HttpMethod.Delete, doomed, _admin, null),
        ];
    }

    /// <summary>
    /// The value one row of <paramref name="entity"/> already holds on its <c>unique</c> field, patched onto a
    /// different row — the only way an update reaches a 409.
    /// </summary>
    /// <remarks>
    /// Each entity has its own such field, deliberately: <c>categories</c> earned <c>code</c> for this, because
    /// otherwise the document would list a status one of the two entities could never answer, and this fact
    /// compares the document and the observed set in both directions.
    /// </remarks>
    /// <param name="entity">The entity being patched.</param>
    /// <param name="value">A value some other row of it already holds.</param>
    private static JsonObject TakenUniqueValue(string entity, string value) =>
        string.Equals(entity, "categories", StringComparison.Ordinal)
            ? new JsonObject { ["code"] = value }
            : new JsonObject { ["sku"] = value };

    /// <summary>
    /// Creates a row of <paramref name="entity"/> holding a fresh value on its <c>unique</c> field, and returns
    /// that value for <see cref="TakenUniqueValue"/> to collide with.
    /// </summary>
    /// <param name="world">The running API.</param>
    /// <param name="entity">The entity to seed.</param>
    /// <param name="categoryId">The category a product must belong to.</param>
    private static async Task<string> TakeAUniqueValueAsync(AlvoApiWorld world, string entity, Guid categoryId)
    {
        var isCategory = string.Equals(entity, "categories", StringComparison.Ordinal);
        var value = isCategory ? "TAKEN" : "AAA-9999";
        var body = Body(entity, categoryId);
        body[isCategory ? "code" : "sku"] = value;

        await CreateAsync(world, entity, body);
        return value;
    }

    /// <summary>
    /// Creates a row of <paramref name="entity"/> that another row references through a <c>ref</c> declaring
    /// <c>onDelete: "restrict"</c>, and returns its route — the only way a delete reaches a 409.
    /// </summary>
    /// <remarks>
    /// A product's <c>parent_id</c> is a self-reference for exactly this: without it the document would list a
    /// 409 on <c>products.delete</c> that nothing could provoke, and a third entity would only move the gap to
    /// whatever nothing referenced in turn.
    /// </remarks>
    /// <param name="world">The running API.</param>
    /// <param name="entity">The entity whose row must end up referenced.</param>
    /// <param name="categoryId">The category a product must belong to.</param>
    private static async Task<string> ReferencedRowAsync(AlvoApiWorld world, string entity, Guid categoryId)
    {
        if (string.Equals(entity, "categories", StringComparison.Ordinal))
        {
            // A category a product points at through `category_id`.
            var category = await CreateAsync(world, "categories", CategoryBody());
            await CreateAsync(world, "products", ProductBody(category));
            return $"/api/categories/{category}";
        }

        var parent = await CreateAsync(world, "products", ProductBody(categoryId));
        var child = ProductBody(categoryId);
        child["parent_id"] = parent.ToString();
        await CreateAsync(world, "products", child);
        return $"/api/products/{parent}";
    }

    /// <summary>
    /// The two refusals the gate answers on every operation: a credential that cannot be resolved, and a key
    /// whose scopes do not cover the entity.
    /// </summary>
    /// <remarks>
    /// Both are provoked per operation rather than once, because the document lists them per operation — a
    /// single probe would leave four of the five unevidenced, which is exactly the shape of coverage
    /// <c>DataApiRoutingTests</c>' own marker fact exists to avoid.
    /// </remarks>
    private static IEnumerable<Probe> Gated(string path, string operation, HttpMethod method, JsonObject? body = null) =>
    [
        new(operation, 401, method, path, _ghost, body),
        new(operation, 403, method, path, _narrow, body),
    ];

    /// <summary>The 304 probe, for an entity whose rows carry a version — and nothing for one whose do not.</summary>
    private static async Task<IEnumerable<Probe>> NotModifiedAsync(AlvoApiWorld world, string row)
    {
        using var read = await world.SendAsync(HttpMethod.Get, row, _admin);
        return read.Headers.Contains("ETag")
            ? [new("get", 304, HttpMethod.Get, row, _admin, null, Header("If-None-Match", read.ETagOf()))]
            : [];
    }

    /// <summary>Uses one idempotency key for one body, so the 409 probe can reuse it for another.</summary>
    private static async Task<string> SpendAnIdempotencyKeyAsync(AlvoApiWorld world, string entity, Guid categoryId)
    {
        var key = $"spent-on-{entity}";
        using var response = await world.SendAsync(
            HttpMethod.Post, $"/api/{entity}", _admin, body: Body(entity, categoryId),
            headers: Header("Idempotency-Key", key));

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created, "the key must really be recorded, or the 409 probe is answered 201");
        return key;
    }

    /// <summary>The one field both entities declare, changed — the smallest legal patch body.</summary>
    private static JsonObject Rename() => new() { ["name"] = "Renamed" };

    /// <summary>A patch body the declared maximum length refuses, on either entity.</summary>
    private static JsonObject Overlong() => new() { ["name"] = new string('n', 500) };

    /// <summary>A body that differs from <see cref="Body"/> only in a value, so a reused key really conflicts.</summary>
    private static JsonObject RenamedBody(string entity, Guid categoryId)
    {
        var body = Body(entity, categoryId);
        body["name"] = "A different name";
        return body;
    }

    /// <summary>
    /// An entity tag this API could have minted and no row carries — <c>"1"</c> is one tick past
    /// <see cref="DateTimeOffset.MinValue"/>.
    /// </summary>
    /// <remarks>
    /// It has to be <em>parseable</em> rather than merely wrong: an unparseable tag is refused by the request
    /// layer, which is a 412 too, and would leave the port's own version comparison unexercised on the audited
    /// entity. On the version-less entity the port refuses the precondition itself, which is the same 412 by a
    /// different route — and the document makes no distinction, because a caller cannot either.
    /// </remarks>
    private const string StaleTag = "\"1\"";

    private static Dictionary<string, string> Header(string name, string value) =>
        new(StringComparer.Ordinal) { [name] = value };

    /// <summary>One request that must earn one status on one operation.</summary>
    /// <param name="Operation">The operation's wire name, which is the second half of its operation id.</param>
    /// <param name="Status">The status this request must be answered with.</param>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Path">The request path.</param>
    /// <param name="Key">The key to present, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="Body">The body to send, or <see langword="null"/> for none.</param>
    /// <param name="Headers">Any further request headers the status needs.</param>
    private sealed record Probe(
        string Operation,
        int Status,
        HttpMethod Method,
        string Path,
        TestApiKey? Key,
        JsonObject? Body,
        IReadOnlyDictionary<string, string>? Headers = null);
}
