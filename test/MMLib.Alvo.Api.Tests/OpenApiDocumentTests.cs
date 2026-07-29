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
            51,
            "25 on the version-less entity and 26 on the audited one, whose read adds a 304 — pinned from "
            + "outside so the equality below cannot be satisfied by two empty sets");
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
    /// A <c>hidden</c> field appears in no schema, no parameter and no prose — <b>a confidentiality fact, not a
    /// tidiness one</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hidden field's <em>name</em> in a public document is exactly the schema oracle the query parser's
    /// refusals and the port's deny reasons are worded to avoid: it answers "does this entity have a field
    /// called X" for every caller at once, including the ones the mask exists to keep out.
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
    public async Task A_hidden_field_appears_in_no_schema_at_all()
    {
        var confidential = DescriptorFieldDescription("products", "cost");
        await using var world = await StoreAsync();

        var text = await world.OpenApiTextAsync();
        var names = EveryDeclaredName(JsonNode.Parse(text)!.AsObject());

        confidential.ShouldNotBeNullOrWhiteSpace(
            "the fixture's hidden field must carry a description, or the phrase check below asserts nothing");
        names.ShouldContain("price", "a visible decimal must be published, or this fact holds over an empty document");
        names.ShouldNotContain("cost", "a hidden field's name must appear in no schema and no parameter");
        text.ShouldNotContain(
            confidential, Case.Sensitive, "the hidden field's own description must not reach the document either");
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
            40,
            "twenty per entity — three on a list and a read, five on a create and an update, four on a delete");
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

    private static JsonObject Property(JsonObject document, string schema, string field) =>
        Component(document, "schemas", schema)["properties"]![field]?.AsObject()
        ?? throw new InvalidOperationException($"Schema '{schema}' declares no '{field}' property.");

    /// <summary>
    /// Every name the document declares anywhere a field's name could surface: a schema property, a parameter,
    /// or a response header.
    /// </summary>
    /// <remarks>
    /// Structural rather than textual on purpose — see the confidentiality fact's own remarks for why a
    /// substring search over this document is the wrong instrument.
    /// </remarks>
    private static HashSet<string> EveryDeclaredName(JsonObject document)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schema in document["components"]!["schemas"]!.AsObject())
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
    private static IEnumerable<JsonObject> AllParameters(JsonObject document) =>
        document["components"]!["parameters"]!.AsObject().Select(parameter => parameter.Value!.AsObject())
            .Concat(Operations(document)
                .SelectMany(operation => operation["parameters"]?.AsArray() ?? [])
                .Select(parameter => parameter!.AsObject())
                .Where(parameter => parameter["$ref"] is null));

    /// <summary>One list route's parameter names, with every <c>$ref</c> followed.</summary>
    private static IEnumerable<string> ListParameterNames(JsonObject document, string entity) =>
        ListParameters(document, entity).Select(parameter => parameter["name"]!.GetValue<string>());

    private static JsonObject ListParameter(JsonObject document, string entity, string name) =>
        ListParameters(document, entity).FirstOrDefault(
            parameter => string.Equals(parameter["name"]!.GetValue<string>(), name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"The list route for '{entity}' documents no '{name}' parameter.");

    private static IEnumerable<JsonObject> ListParameters(JsonObject document, string entity) =>
        (document["paths"]![$"/api/{entity}"]!["get"]!["parameters"]?.AsArray() ?? [])
        .Select(parameter => Dereference(document, parameter!.AsObject()));

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

    private static JsonObject CategoryBody() => new() { ["name"] = "Hand tools" };

    private static JsonObject ProductBody(Guid categoryId) => new()
    {
        ["name"] = "Claw hammer",
        ["status"] = "draft",
        ["category_id"] = categoryId.ToString(),
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

            .. Gated(row, "delete", HttpMethod.Delete),
            new("delete", 412, HttpMethod.Delete, row, _admin, null, stale),
            new("delete", 404, HttpMethod.Delete, absent, _admin, null),
            new("delete", 204, HttpMethod.Delete, doomed, _admin, null),
        ];
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
