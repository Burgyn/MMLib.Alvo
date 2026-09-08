using System.Globalization;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The claims every Alvo OpenAPI document must satisfy, whatever descriptor produced it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are C# and not Vacuum rules.</b> <c>schema/openapi-ruleset.yaml</c> is the authority on
/// <em>node-local</em> shape — a casing, a pattern, a key that must be present — and it is genuinely better at
/// it. Everything here is conditional ("<em>if</em> an operation pages, <em>then</em> its 200 is an envelope")
/// or cross-referential ("the path set is exactly the entities' routes"), and vacuum 0.30.3 can express
/// neither: a JSONPath filter expression does not parse at all, and the <c>schema</c> function's
/// <c>if</c>/<c>then</c> reported <c>``, is missing and is required</c> for every operation in the document.
/// Splitting the two that way also puts each claim where it produces a usable failure message.
/// </para>
/// <para>
/// <b>One implementation, every caller.</b> <c>OpenApiDocumentTests</c> runs these over the fixture
/// document and <c>MMLib.Alvo.Api.Invariants.Tests.Integration</c> runs them over three
/// <c>examples/</c> descriptors and sixteen generated ones (#26) — twenty documents in all. A second copy is how the fixture and the
/// generated documents would come to be judged by two different notions of "well shaped".
/// </para>
/// <para>
/// <b>Every enumerating claim pins its count from outside the document.</b> "Each list route documents
/// <c>limit</c>" over a document with no list routes passes trivially, so the operation count is asserted
/// against <c>entities × 10</c> — a literal — before anything is walked.
/// </para>
/// </remarks>
internal static class OpenApiDocumentFacts
{
    /// <summary>The four path keys every entity gets, and the ten operations spread across them.</summary>
    private static readonly string[] _suffixes = ["", "/query", "/{id}", "/batch"];

    /// <summary>Seven single-row operations plus the three the batch path answers.</summary>
    private const int OperationsPerEntity = 10;

    /// <summary>
    /// The parameter <b>names</b> — not component keys — a paged read has to publish.
    /// </summary>
    /// <remarks>
    /// <c>Prefer</c> is the header that opts into <c>count</c>, and the other five are the query surface
    /// §2.1 requires of a list. The names differ from the component keys that carry them
    /// (<c>prefer</c> → <c>Prefer</c>, <c>orGroup</c> → <c>or</c>), and it is the name a caller sends, so
    /// the name is what is asserted.
    /// </remarks>
    private static readonly string[] _pagingParameters = ["Prefer", "select", "order", "limit", "offset", "after"];

    /// <summary>The twelve schemas an entity publishes: the row, the four write shapes, the page, the batch set.</summary>
    private static readonly string[] _schemaShapes =
    [
        "", "Create", "Patch", "Replace", "Query", "Page", "PageItem",
        "BatchCreate", "BatchUpdate", "BatchPatch", "BatchDelete", "BatchResult",
    ];

    /// <summary>The two schemas that belong to the framework rather than to an entity.</summary>
    private static readonly string[] _frameworkSchemas = ["problemDetails", "problemViolation"];

    /// <summary>
    /// The per-entity schemas whose <em>properties</em> Alvo mints rather than the descriptor.
    /// </summary>
    /// <remarks>
    /// A page envelope's members are <c>items</c>/<c>next</c>/<c>count</c> and a batch result's are Alvo's
    /// own, so every one of them must declare a type. The row and write shapes are excluded because their
    /// properties ARE the descriptor's fields, one of which may legitimately be a type-free <c>json</c>.
    /// </remarks>
    private static readonly string[] _mintedShapes = ["Page", "BatchResult", "BatchDelete"];

    /// <summary>
    /// Every claim <see cref="AssertShape"/> makes, by name.
    /// </summary>
    /// <remarks>
    /// <b>So the mutation battery's completeness is pinned from outside, not hand-maintained.</b>
    /// <c>OpenApiDocumentFactsMutationTests</c> asserts its cases cover exactly this list, which is the
    /// arrangement <c>RulesetTests</c> already has for the Vacuum rules (it reads their ids out of the YAML).
    /// A claim added below without a mutation would otherwise arrive unproven — and an unproven claim over
    /// twenty documents is the failure mode #26 exists to close.
    /// </remarks>
    internal static IReadOnlyList<string> ClaimNames { get; } =
    [
        "numeric-segment", "path-set", "operation-count", "paging-parameters", "page-envelope",
        "problem-document", "delete-body", "schema-keys", "response-headers", "component-content",
        "minted-type",
    ];

    /// <summary>Asserts every generic claim, throwing on the first one the document breaks.</summary>
    /// <param name="document">The served OpenAPI document.</param>
    /// <param name="entities">The entities the applied descriptor declares — the set the counts are pinned against.</param>
    /// <param name="prefix">The Data API's route prefix.</param>
    internal static void AssertShape(
        JsonObject document, IReadOnlyCollection<string> entities, string prefix = "/api")
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(entities);

        var paths = document["paths"]!.AsObject();

        // Before the path-set claim, deliberately: a numeric segment is *also* a path-set violation, so
        // asserting the set first would make this claim unreachable — and an unreachable claim cannot be
        // shown to fail, which is the one thing #26 insists every check here can do.
        NoPathSegmentIsNumeric(paths);
        ThePathSetIsExactlyTheEntitiesRoutes(paths, entities, prefix);
        TheOperationCountIsTenPerEntity(paths, entities);
        EveryListOperationDocumentsThePagingParameters(document, paths, entities, prefix);
        EveryListResponseIsAPageEnvelope(document, paths, entities, prefix);
        // Before the refusal claim, for the same reason NoPathSegmentIsNumeric runs first: a component
        // response with no `content` breaks both, and the dedicated claim is the one whose message says so.
        EveryComponentResponseCarriesContent(document);
        EveryRefusalIsAProblemDocument(document, paths);
        OnlyTheBatchRouteCarriesADeleteBody(paths, entities);
        TheSchemaKeysAreTheEntitySchemasAndTheFrameworksOwn(document, entities);
        EveryResponseCarriesTheHeadersTheLintReadsThrough(document, paths);
        EveryFrameworkMintedPropertyDeclaresAType(document, entities);
    }

    /// <summary>The document describes each entity's four path keys, and no path nobody generated.</summary>
    private static void ThePathSetIsExactlyTheEntitiesRoutes(
        JsonObject paths, IReadOnlyCollection<string> entities, string prefix)
    {
        var expected = entities
            .SelectMany(entity => _suffixes.Select(suffix => $"{prefix}/{entity}{suffix}"))
            .ToHashSet(StringComparer.Ordinal);

        expected.Count.ShouldBe(
            entities.Count * _suffixes.Length, "or the expected set collapsed and proves nothing");
        paths.Select(path => path.Key).ToHashSet(StringComparer.Ordinal).ShouldBe(
            expected, ignoreOrder: true, "the document must describe every generated route, and nothing else");
    }

    /// <summary>Ten operations per entity — the count the rest of these claims are measured against.</summary>
    private static void TheOperationCountIsTenPerEntity(JsonObject paths, IReadOnlyCollection<string> entities) =>
        Operations(paths).Count.ShouldBe(
            entities.Count * OperationsPerEntity,
            "an entity gets ten operations; a document with fewer would satisfy every 'every operation' claim below for the wrong reason");

    /// <summary>
    /// No path segment is a number, so no identifier is ever spelled as an integer in a URL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spec §308 asks for this and <b>no Vacuum rule covers it</b> — measured: a document whose path keys
    /// were rewritten to <c>/api/2</c> raised not one violation under the recommended set, and the
    /// <c>field: "@key"</c> + <c>pattern</c> form that would express it is silently vacuous. A row is
    /// addressed by a uuid in this framework; a numeric segment would mean either a sequential key
    /// (enumerable by an attacker) or a version in the path, and Alvo versions by <c>apiVersion</c> in the
    /// descriptor instead.
    /// </para>
    /// <para>
    /// <b>For a descriptor the schema accepts, this is implied by the claim below</b> — an entity name matches
    /// <c>^[a-z][a-z0-9_]{0,62}$</c> and so is never all digits, and the path set is pinned to those names.
    /// What it actually guards is a change to the <em>route template</em>: a <c>/v2/</c> segment, or a batch
    /// route addressed by an ordinal, would move the expected set and this claim with it. That is why it runs
    /// first — asserted after the set, it could never be seen to fail.
    /// </remarks>
    private static void NoPathSegmentIsNumeric(JsonObject paths)
    {
        foreach (var path in paths)
        {
            foreach (var segment in path.Key.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                segment.All(char.IsAsciiDigit).ShouldBeFalse(
                    $"'{path.Key}' spells a segment as an integer, which no URL this framework generates may do");
            }
        }
    }

    /// <summary>A paged read publishes every parameter a caller needs to page it.</summary>
    private static void EveryListOperationDocumentsThePagingParameters(
        JsonObject document, JsonObject paths, IReadOnlyCollection<string> entities, string prefix)
    {
        foreach (var entity in entities)
        {
            var operation = paths[$"{prefix}/{entity}"]!["get"]!.AsObject();
            var names = operation["parameters"]!.AsArray()
                .Select(parameter => Resolve(document, parameter!.AsObject()))
                .Select(parameter => parameter["name"]!.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal);

            foreach (var expected in _pagingParameters)
            {
                names.ShouldContain(expected, $"'{entity}' does not publish '{expected}' on its list route");
            }
        }
    }

    /// <summary>
    /// A list answers the envelope and never a bare array, and all three of its members are present.
    /// </summary>
    /// <remarks>
    /// <c>next</c> and <c>count</c> are declared even when null — an agent that has to tell "absent" from
    /// "null" cannot page reliably, and the document is the only place that promise can be made.
    /// </remarks>
    private static void EveryListResponseIsAPageEnvelope(
        JsonObject document, JsonObject paths, IReadOnlyCollection<string> entities, string prefix)
    {
        foreach (var entity in entities)
        {
            var page = Resolve(
                document,
                paths[$"{prefix}/{entity}"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!
                    .AsObject());

            page["type"]!.GetValue<string>().ShouldBe("object", $"'{entity}' must page an envelope, never an array");
            var members = page["properties"]!.AsObject().Select(member => member.Key).ToHashSet(StringComparer.Ordinal);
            members.ShouldContain("items", $"'{entity}' page has no items");
            members.ShouldContain("next", $"'{entity}' page has no next cursor");
            members.ShouldContain("count", $"'{entity}' page has no count");
        }
    }

    /// <summary>Every refusal is an RFC 9457 problem document under the framework's own schema.</summary>
    /// <remarks>
    /// Spec §308 says "RFC 7807"; the implementation answers its successor, RFC <b>9457</b>, which is what
    /// <c>AlvoProblemTypes</c> and the document already do — so the claim follows the code and not the older
    /// RFC number. The reference is compared <em>unresolved</em> on purpose: that one schema is the whole
    /// point, and a response carrying an identically-shaped copy would be a second contract to keep in step.
    /// </remarks>
    private static void EveryRefusalIsAProblemDocument(JsonObject document, JsonObject paths)
    {
        foreach (var (path, method, operation) in Operations(paths))
        {
            var refusals = operation["responses"]!.AsObject()
                .Where(response => int.TryParse(response.Key, CultureInfo.InvariantCulture, out var status) && status >= 400)
                .ToList();

            refusals.ShouldNotBeEmpty($"{method.ToUpperInvariant()} {path} documents no refusal at all");
            foreach (var refusal in refusals)
            {
                var content = Resolve(document, refusal.Value!.AsObject())["content"]
                    .ShouldNotBeNull(
                        $"{method.ToUpperInvariant()} {path}'s {refusal.Key} declares no content at all")
                    .AsObject();
                content.ContainsKey("application/problem+json").ShouldBeTrue(
                    $"{method.ToUpperInvariant()} {path} answers {refusal.Key} with something other than a problem document");
                content["application/problem+json"]!["schema"]!["$ref"]!.GetValue<string>().ShouldBe(
                    "#/components/schemas/problemDetails",
                    $"{method.ToUpperInvariant()} {path}'s {refusal.Key} must reference the one problem schema");
            }
        }
    }

    /// <summary>
    /// A <c>DELETE</c> carries a request body on the batch route and on no other.
    /// </summary>
    /// <remarks>
    /// This is the pin that keeps a muted lint rule from spreading. RFC 9110 §9.3.5 gives content on a
    /// DELETE no defined semantics, so <c>no-request-body</c> fires on <c>DELETE /{entity}/batch</c> —
    /// which takes <c>{"ids":[…]}</c> — and the ruleset turns the rule off while #206 decides whether the
    /// verb changes. Off for the whole document is more than that decision needs, so the exemption is
    /// bounded here instead: a body on a single-row delete would fail this claim even though the linter
    /// would not notice it.
    /// </remarks>
    private static void OnlyTheBatchRouteCarriesADeleteBody(
        JsonObject paths, IReadOnlyCollection<string> entities)
    {
        var withBody = Operations(paths)
            .Where(operation => operation.Method == "delete" && operation.Operation.ContainsKey("requestBody"))
            .Select(operation => operation.Path)
            .ToList();

        // Both halves, negative first. `ShouldAllBe` catches a body somewhere it does not belong; it is also
        // trivially true over an empty list, so a regression that dropped the body from EVERY delete —
        // including the batch route, which cannot name its rows without one — would satisfy it alone. The
        // count closes that, pinned from outside at one per entity.
        withBody.ShouldAllBe(
            path => path.EndsWith("/batch", StringComparison.Ordinal),
            $"only the batch route may carry a DELETE body (#206); these do: {string.Join(", ", withBody)}");
        withBody.Count.ShouldBe(
            entities.Count, "each entity's batch route carries a DELETE body, and it is how a batch delete names its rows");
    }

    /// <summary>
    /// The component schema map holds exactly one schema per entity per shape, plus the framework's two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the claim the Vacuum ruleset cannot make.</b> A casing rule over
    /// <c>components.schemas</c> can only judge a whole key, and an entity schema's key carries the entity's
    /// own identifier — <c>work_ordersPage</c> is neither camel nor snake, and requiring camel there rejected
    /// every multi-word entity in <c>examples/</c>. What is actually worth asserting needs the declared
    /// entity list: the key set is <em>exactly</em> the twelve shapes per entity plus
    /// <c>problemDetails</c>/<c>problemViolation</c>, and nothing else.
    /// </para>
    /// <para>
    /// Stronger than the casing rule it replaces, and in the direction this repository cares about: it is
    /// pinned from outside the document, so a renamed, duplicated or vanished schema fails here, while a
    /// casing rule would have passed all three.
    /// </para>
    /// </remarks>
    private static void TheSchemaKeysAreTheEntitySchemasAndTheFrameworksOwn(
        JsonObject document, IReadOnlyCollection<string> entities)
    {
        var expected = entities
            .SelectMany(entity => _schemaShapes.Select(shape => $"{entity}{shape}"))
            .Concat(_frameworkSchemas)
            .ToHashSet(StringComparer.Ordinal);

        expected.Count.ShouldBe(
            (entities.Count * _schemaShapes.Length) + _frameworkSchemas.Length,
            "or the expected set collapsed and proves nothing");
        document["components"]!["schemas"]!.AsObject()
            .Select(schema => schema.Key)
            .ToHashSet(StringComparer.Ordinal)
            .ShouldBe(expected, ignoreOrder: true, "the document must publish one schema per entity shape, and no other");
    }

    /// <summary>
    /// Every response carries a <c>headers</c> object with <c>Cache-Control</c> in it.
    /// </summary>
    /// <remarks>
    /// <b>This exists because the Vacuum rule that makes the same claim can be evaded.</b>
    /// <c>alvo-response-no-store</c> is <c>given: $.paths[*][*].responses[*].headers</c> — the very object
    /// that carries the claim — so deleting <c>headers</c> wholesale matches nothing and the rule reports
    /// zero violations. A rule that tests nothing looks exactly like a rule that passes, which the ruleset's
    /// own header warns about; the mutation battery mutates the child and could not have caught it. Asserted
    /// here instead, where the parent's absence is the failure.
    /// </remarks>
    private static void EveryResponseCarriesTheHeadersTheLintReadsThrough(JsonObject document, JsonObject paths)
    {
        foreach (var (path, method, operation) in Operations(paths))
        {
            foreach (var response in operation["responses"]!.AsObject())
            {
                var headers = Resolve(document, response.Value!.AsObject())["headers"]
                    .ShouldNotBeNull($"{method.ToUpperInvariant()} {path}'s {response.Key} declares no headers at all")
                    .AsObject();

                headers.ContainsKey("Cache-Control").ShouldBeTrue(
                    $"{method.ToUpperInvariant()} {path}'s {response.Key} does not document Cache-Control");
            }
        }
    }

    /// <summary>Every declared component response carries a <c>content</c> object.</summary>
    /// <remarks>
    /// The same evasion as above, one component map over: <c>alvo-problem-media-type</c> is
    /// <c>given: $.components.responses[*].content</c>, so a response with no <c>content</c> is invisible to
    /// it. The media type inside is the rule's business; that there is something for it to read is this
    /// claim's.
    /// </remarks>
    private static void EveryComponentResponseCarriesContent(JsonObject document)
    {
        var responses = document["components"]!["responses"]!.AsObject();

        responses.Count.ShouldBeGreaterThan(0, "a document with no component responses proves nothing here");
        foreach (var response in responses)
        {
            response.Value!.AsObject().ContainsKey("content").ShouldBeTrue(
                $"the '{response.Key}' response declares no content, so nothing constrains its media type");
        }
    }

    /// <summary>
    /// Every property of a framework-minted schema declares a <c>type</c>.
    /// </summary>
    /// <remarks>
    /// <b>The bound on a muted rule.</b> <c>oas-missing-type</c> is off because a <c>json</c>-typed field is
    /// deliberately type-free — in draft 2020-12 an absent <c>type</c> means "any", which is what the
    /// descriptor said. That reason covers a field the descriptor declared and nothing else, so the
    /// exemption is bounded to the entity schemas here: a type-less property in a page envelope, a batch
    /// result or a problem document is Alvo's own bug and no longer goes unreported. The two consequential
    /// mutes each get a bound like this — <c>no-request-body</c> gets
    /// <see cref="OnlyTheBatchRouteCarriesADeleteBody"/>, <c>camel-case-properties</c> gets
    /// <see cref="TheSchemaKeysAreTheEntitySchemasAndTheFrameworksOwn"/>.
    /// </remarks>
    private static void EveryFrameworkMintedPropertyDeclaresAType(
        JsonObject document, IReadOnlyCollection<string> entities)
    {
        var minted = _frameworkSchemas
            .Concat(entities.SelectMany(entity => _mintedShapes.Select(shape => $"{entity}{shape}")))
            .ToList();

        foreach (var name in minted)
        {
            var properties = document["components"]!["schemas"]![name]?["properties"]?.AsObject();
            foreach (var property in properties ?? [])
            {
                property.Value!.AsObject().ContainsKey("type").ShouldBeTrue(
                    $"'{name}.{property.Key}' declares no type, and only a descriptor's json field may (#26)");
            }
        }
    }

    /// <summary>Every operation in the document, with the path and method that name it.</summary>
    private static List<(string Path, string Method, JsonObject Operation)> Operations(JsonObject paths) =>
    [
        .. from path in paths
           from entry in path.Value!.AsObject()
           where entry.Value is JsonObject candidate && candidate.ContainsKey("responses")
           select (path.Key, entry.Key, entry.Value!.AsObject())
    ];

    /// <summary>Follows a component reference one level; returns anything else unchanged.</summary>
    /// <remarks>
    /// One level is all the document ever uses, and a loop that followed references transitively would
    /// hide a chain the published document does not actually contain.
    /// </remarks>
    private static JsonObject Resolve(JsonObject document, JsonObject node)
    {
        if (node["$ref"]?.GetValue<string>() is not { } reference)
        {
            return node;
        }

        var segments = reference.Split('/');
        segments.Length.ShouldBe(4, $"'{reference}' is not a component reference this document should contain");

        return document["components"]![segments[2]]![segments[3]]!.AsObject();
    }
}
