using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Every claim <see cref="OpenApiDocumentFacts"/> makes can be seen to fail, and fails as <em>itself</em>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> <see cref="OpenApiDocumentFacts"/> is asserted over twenty documents —
/// this fixture, three <c>examples/</c> descriptors and sixteen generated ones (#26) — and a claim that
/// silently matches nothing would pass all twenty. That is not a hypothetical worry in this area: the
/// Vacuum ruleset the same issue ships has a rule form (<c>field: "@key"</c> with <c>pattern</c>) that reports
/// zero violations for a deliberately impossible expression, and <c>RulesetTests</c> exists for the same
/// reason. A gate whose failure nobody has witnessed is a gate nobody should trust.
/// </para>
/// <para>
/// <b>Each case asserts the message, not merely that something threw.</b> The first version of this file
/// asserted <c>Should.Throw</c> alone and passed seven times out of seven — including for the numeric-segment
/// mutation, which was in fact tripping the <em>path-set</em> claim, because a numeric path is also a path
/// nobody generated. Matching the message is what caught that, and it is why
/// <c>NoPathSegmentIsNumeric</c> now runs before the set is compared.
/// </para>
/// <para>
/// The mutations are applied to the <em>real</em> served document rather than to a hand-written fixture, so
/// nothing here can rot into describing a document shape Alvo stopped producing.
/// </para>
/// </remarks>
public class OpenApiDocumentFactsMutationTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>The two entities <c>documented-store</c> declares, pinned as every other count is.</summary>
    private static readonly string[] _entities = ["categories", "products"];

    /// <summary>
    /// One mutation per claim, each paired with a phrase only the violated claim's own message contains.
    /// </summary>
    /// <remarks>
    /// A table rather than <c>[InlineData]</c> so the completeness fact below can read the same set the
    /// theory runs — the point being that the two cannot drift apart.
    /// </remarks>
    private static readonly (string Mutation, string Expected)[] _cases =
    [
        ("numeric-segment", "spells a segment as an integer"),
        ("path-set", "every generated route"),
        ("operation-count", "ten operations"),
        ("paging-parameters", "does not publish"),
        ("page-envelope", "page has no next cursor"),
        ("problem-document", "must reference the one problem schema"),
        ("delete-body", "only the batch route may carry a DELETE body"),
        ("schema-keys", "one schema per entity shape"),
        ("response-headers", "declares no headers at all"),
        ("component-content", "declares no content"),
        ("minted-type", "declares no type"),
    ];

    /// <summary>The table as theory data.</summary>
    public static TheoryData<string, string> Cases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (mutation, expected) in _cases)
            {
                data.Add(mutation, expected);
            }

            return data;
        }
    }

    /// <summary>Each mutation is reported by the claim it violates, and by that claim's own message.</summary>
    /// <param name="mutation">The mutation to apply to the served document.</param>
    /// <param name="expected">A phrase only the violated claim's own message contains.</param>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_generic_claim_fails_on_its_own_mutation(string mutation, string expected)
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "documented-store.alvo.json", [_admin], new AlvoApiWorldSetup(MapOpenApiDocument: true));
        var document = await world.OpenApiDocumentAsync();
        Mutate(document, mutation);

        var failure = Should.Throw<ShouldAssertException>(
            () => OpenApiDocumentFacts.AssertShape(document, _entities));

        failure.Message.ShouldContain(
            expected,
            Case.Insensitive,
            $"'{mutation}' must be reported by the claim it violates, and it said: {failure.Message}");
    }

    /// <summary>Every claim the facts make has a mutation here, and every mutation names a real claim.</summary>
    /// <remarks>
    /// Both directions, and the expected set comes from <see cref="OpenApiDocumentFacts.ClaimNames"/> rather
    /// than from this file — a claim added to <c>AssertShape</c> cannot arrive unproven, and a mutation for a
    /// claim that no longer exists is a case that can never fail. Same construction as
    /// <c>RulesetTests.Every_alvo_rule_has_a_mutation_that_proves_it_fires</c>, which reads its ids out of
    /// the ruleset YAML.
    /// </remarks>
    [Fact]
    public void Every_claim_has_a_mutation_that_proves_it_fires() =>
        _cases.Select(entry => entry.Mutation).ToHashSet(StringComparer.Ordinal).ShouldBe(
            OpenApiDocumentFacts.ClaimNames.ToHashSet(StringComparer.Ordinal),
            ignoreOrder: true,
            "a claim with no mutation is a claim nothing holds");

    /// <summary>An unmutated document passes, so the mutations above are what the failures are about.</summary>
    [Fact]
    public async Task The_unmutated_document_passes_every_claim()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "documented-store.alvo.json", [_admin], new AlvoApiWorldSetup(MapOpenApiDocument: true));

        OpenApiDocumentFacts.AssertShape(await world.OpenApiDocumentAsync(), _entities);
    }

    /// <summary>Breaks exactly one claim, structurally.</summary>
    /// <param name="document">The served document, mutated in place.</param>
    /// <param name="mutation">Which claim to break.</param>
    private static void Mutate(JsonObject document, string mutation)
    {
        var paths = document["paths"]!.AsObject();
        switch (mutation)
        {
            case "path-set":
                paths.Remove("/api/categories/query");
                break;
            case "operation-count":
                paths["/api/categories"]!.AsObject().Remove("get");
                break;
            case "numeric-segment":
                var moved = paths["/api/categories"]!.DeepClone();
                paths.Remove("/api/categories");
                paths["/api/2"] = moved;
                break;
            case "paging-parameters":
                // Index 3 is `limit` — the one parameter without which a page cannot be sized at all.
                paths["/api/categories"]!["get"]!["parameters"]!.AsArray().RemoveAt(3);
                break;
            case "page-envelope":
                document["components"]!["schemas"]!["categoriesPage"]!["properties"]!.AsObject().Remove("next");
                break;
            case "problem-document":
                document["components"]!["responses"]!["forbidden"]!["content"]!["application/problem+json"]!
                    ["schema"]!.AsObject()["$ref"] = "#/components/schemas/categories";
                break;
            case "schema-keys":
                document["components"]!["schemas"]!.AsObject().Remove("categoriesPageItem");
                break;
            case "response-headers":
                // The PARENT, not the Cache-Control inside it: that is the shape the Vacuum rule cannot
                // see, and the reason this claim exists at all.
                paths["/api/categories"]!["get"]!["responses"]!["200"]!.AsObject().Remove("headers");
                break;
            case "component-content":
                document["components"]!["responses"]!["forbidden"]!.AsObject().Remove("content");
                break;
            case "minted-type":
                document["components"]!["schemas"]!["categoriesPage"]!["properties"]!["next"]!.AsObject()
                    .Remove("type");
                break;
            case "delete-body":
                paths["/api/categories/{id}"]!["delete"]!.AsObject()["requestBody"] =
                    paths["/api/categories/batch"]!["delete"]!["requestBody"]!.DeepClone();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "no such mutation");
        }
    }
}
