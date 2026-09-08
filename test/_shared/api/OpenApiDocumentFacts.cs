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
/// document and <c>MMLib.Alvo.Api.Invariants.Tests.Integration</c> runs them over the four
/// <c>examples/</c> descriptors and sixteen generated ones (#26). A second copy is how the fixture and the
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
        EveryRefusalIsAProblemDocument(document, paths);
        OnlyTheBatchRouteCarriesADeleteBody(paths);
        TheSchemaKeysAreTheEntitySchemasAndTheFrameworksOwn(document, entities);
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
                var content = Resolve(document, refusal.Value!.AsObject())["content"]!.AsObject();
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
    private static void OnlyTheBatchRouteCarriesADeleteBody(JsonObject paths)
    {
        var withBody = Operations(paths)
            .Where(operation => operation.Method == "delete" && operation.Operation.ContainsKey("requestBody"))
            .Select(operation => operation.Path)
            .ToList();

        withBody.ShouldAllBe(
            path => path.EndsWith("/batch", StringComparison.Ordinal),
            $"only the batch route may carry a DELETE body (#206); these do: {string.Join(", ", withBody)}");
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
