using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Invariants;

/// <summary>One deliberate violation of one rule, applied to a real served document.</summary>
/// <param name="RuleId">The rule the mutation violates, and the <c>code</c> Vacuum must report.</param>
/// <param name="Apply">Breaks the document in place.</param>
internal sealed record RulesetMutation(string RuleId, Action<JsonObject> Apply)
{
    /// <summary>
    /// One mutation per rule the ruleset declares, plus one that proves the extended set is still live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every mutation is structural, never textual, and that was learned the hard way.</b> Renaming a
    /// component key with a string replace leaves every <c>$ref</c> to it dangling, and vacuum 0.30.3
    /// answers a dangling reference with <c>unable to build unresolved model</c> — or, in one shape, with a
    /// panic. Either way the mutation would be measuring the reference resolver rather than the rule it
    /// claims to prove. <see cref="Rekey"/> therefore moves the key <em>and</em> every reference to it.
    /// </para>
    /// <para>
    /// <b><c>operation-operationId-unique</c> is deliberately not an <c>alvo-</c> rule.</b> The ruleset turns
    /// six built-ins off, and a mistake in that block — a stray <c>extends</c> edit, a typo that vacuum
    /// ignores — could quietly disable the whole recommended set while every Alvo rule still passed. One
    /// mutation of a built-in rule is what makes that visible.
    /// </para>
    /// <para>
    /// The mutations name <c>owners</c> and <c>vehicles</c> because the battery runs over
    /// <c>examples/vehicle-registry</c> — a descriptor the compose stack actually serves, rather than a
    /// fixture written to make a linter happy.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<RulesetMutation> All { get; } =
    [
        new("alvo-operation-id-shape", document =>
            document["paths"]!["/api/owners"]!["get"]!.AsObject()["operationId"] = "OwnersList"),

        new("alvo-parameter-key-casing", document => Rekey(document, "parameters", "ifMatch", "if_match")),

        new("alvo-response-key-casing", document => Rekey(document, "responses", "forbidden", "not_allowed")),

        new("alvo-response-described", document =>
            document["paths"]!["/api/owners"]!["get"]!["responses"]!["200"]!.AsObject().Remove("description")),

        new("alvo-response-no-store", document =>
            document["paths"]!["/api/owners"]!["get"]!["responses"]!["200"]!["headers"]!.AsObject()
                .Remove("Cache-Control")),

        new("alvo-problem-media-type", document =>
        {
            var content = document["components"]!["responses"]!["forbidden"]!["content"]!.AsObject();
            var problem = content["application/problem+json"]!.DeepClone();
            content.Remove("application/problem+json");
            content["application/json"] = problem;
        }),

        new("operation-operationId-unique", document =>
            document["paths"]!["/api/vehicles"]!["get"]!.AsObject()["operationId"] =
                document["paths"]!["/api/owners"]!["get"]!["operationId"]!.GetValue<string>()),
    ];

    /// <summary>Renames a component and rewrites every reference to it, so the document stays resolvable.</summary>
    /// <param name="document">The document to mutate in place.</param>
    /// <param name="kind">The component map — <c>schemas</c>, <c>parameters</c>, <c>responses</c>.</param>
    /// <param name="from">The existing key.</param>
    /// <param name="to">The key to move it to.</param>
    private static void Rekey(JsonObject document, string kind, string from, string to)
    {
        var components = document["components"]![kind]!.AsObject();
        Retarget(document, $"#/components/{kind}/{from}", $"#/components/{kind}/{to}");

        var moved = components[from]!.DeepClone();
        components.Remove(from);
        components[to] = moved;
    }

    /// <summary>Rewrites every <c>$ref</c> equal to <paramref name="from"/>, and only those.</summary>
    /// <remarks>
    /// Compared by equality rather than by prefix: <c>ownersPage</c> is a prefix of <c>ownersPageItem</c>,
    /// and a prefix rewrite silently renamed the second one too — which produced a dangling reference and a
    /// mutation that proved nothing.
    /// </remarks>
    private static void Retarget(JsonNode? node, string from, string to)
    {
        switch (node)
        {
            case JsonObject json:
                foreach (var member in json.ToList())
                {
                    if (member.Key == "$ref" && member.Value?.GetValue<string>() == from)
                    {
                        json[member.Key] = to;
                        continue;
                    }

                    Retarget(member.Value, from, to);
                }

                break;

            case JsonArray array:
                foreach (var element in array)
                {
                    Retarget(element, from, to);
                }

                break;
        }
    }
}
