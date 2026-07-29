using Microsoft.OpenApi;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The RFC 9457 problem document every refusal carries, as the two components the whole document shares.
/// </summary>
/// <remarks>
/// <para>
/// <b>One component referenced by every error response, not a shape repeated per status.</b> A refusal has one
/// wire shape whichever endpoint produced it — <see cref="ProblemResultFactory"/> is the only writer — so
/// inlining it per response would be dozens of copies of one contract, and a generated client would grow one
/// type per status code instead of branching on <c>type</c>.
/// </para>
/// <para>
/// <b>The <c>type</c> member is published as an enumeration of the whole catalogue.</b> That is the single most
/// useful thing this component can say: RFC 9457 §3.1.1 makes <c>type</c> the classification a client is
/// allowed to branch on and <c>detail</c> prose it ought not parse, so a client that knows every value
/// <c>type</c> can take needs to parse no prose at all. Enumerating it is only honest because
/// <see cref="AlvoProblemTypes.All"/> is held to being exactly what the framework emits by
/// <c>ProblemDetailsTests</c>.
/// </para>
/// <para>
/// <b><c>instance</c> is deliberately absent.</b> RFC 9457 defines it, and Alvo never writes one — so
/// documenting it would describe a member no response carries, which is the same defect as an unreachable
/// status. A host that adds one through its own <c>IProblemDetailsService</c> is describing its own extension.
/// </para>
/// </remarks>
internal static class ProblemComponents
{
    /// <summary>The component id of the problem document itself.</summary>
    /// <remarks>
    /// Lower camel case, so it cannot collide with an entity's component id: the descriptor's entity grammar
    /// (<c>^[a-z][a-z0-9_]{0,62}$</c>) admits no upper-case letter, so no entity can be called
    /// <c>problemDetails</c>. Same reasoning as <see cref="SchemaComponentBuilder.RowId"/>'s suffixes.
    /// </remarks>
    internal const string DocumentId = "problemDetails";

    /// <summary>The component id of one itemised reason inside a problem document.</summary>
    internal const string ViolationId = "problemViolation";

    /// <summary>Registers both components on <paramref name="document"/>.</summary>
    /// <param name="document">The document being built.</param>
    internal static void AddTo(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.AddComponent(ViolationId, Violation());
        document.AddComponent(DocumentId, Document(document));
    }

    private static OpenApiSchema Document(OpenApiDocument document) => new()
    {
        Type = JsonSchemaType.Object,
        Title = DocumentId,
        Description =
            "An RFC 9457 problem document. Branch on `type`; `detail` is prose and, per §3.1.1, ought not be "
            + "parsed. A `type` keys on the *kind* of refusal and never on its reason — one value covers every "
            + "policy refusal, and one covers both an absent row and a row the caller may not see.",
        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
        {
            ["type"] = ProblemType(),
            ["title"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "The status code's standard reason phrase. Carries no Alvo-specific information.",
            },
            ["status"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Format = "int32",
                Description = "The HTTP status code, repeated in the body as RFC 9457 §3.1.2 allows.",
            },
            ["detail"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description =
                    "What went wrong, in prose. Built from constants and server-owned values only — it never "
                    + "echoes a caller-supplied field name or value, because a refusal is answered before "
                    + "authorization on some paths and would otherwise be the cheapest oracle in the API.",
            },
            ["violations"] = Violations(document),
        },
        Required = new HashSet<string>(StringComparer.Ordinal) { "type", "title", "status", "detail" },
    };

    private static OpenApiSchema ProblemType() => new()
    {
        Type = JsonSchemaType.String,
        Format = "uri",
        Description =
            "The classification to branch on. Every value is listed here, so a client needs no prose: "
            + $"the namespace is `{AlvoProblemTypes.BaseUri}` and the slug names the kind of refusal.",
        Enum = [.. AlvoProblemTypes.All.Select(slug => (JsonNode)JsonValue.Create(AlvoProblemTypes.UriOf(slug)))],
    };

    private static OpenApiSchema Violations(OpenApiDocument document) => new()
    {
        Type = JsonSchemaType.Array,
        Description =
            "Every itemised reason the request was refused, not only the first — so one round trip is enough "
            + "to repair it. Present on a refusal that has itemised reasons (a malformed query string, a body "
            + "the entity's declared shape refuses) and absent on one that does not.",
        Items = new OpenApiSchemaReference(ViolationId, document),
    };

    private static OpenApiSchema Violation() => new()
    {
        Type = JsonSchemaType.Object,
        Title = ViolationId,
        Description = "One machine-readable reason a request was refused.",
        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
        {
            ["pointer"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description =
                    "An RFC 6901 JSON Pointer into the request body, or the *role* of the query-string "
                    + "parameter concerned (`filter`, `order`, `limit`, `offset`, `after`, `select`). A role "
                    + "rather than a name, because in this filter grammar a parameter's name is a field name.",
                Example = JsonValue.Create("/name"),
            },
            ["code"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "A stable kebab-case code for the kind of violation, safe to branch on.",
                Example = JsonValue.Create("max-length"),
            },
            ["message"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "One sentence, free of caller-supplied text.",
            },
            ["fixSuggestion"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String | JsonSchemaType.Null,
                Description =
                    "What to change. Every refusal Alvo itself raises carries one; it is nullable for a "
                    + "violation forwarded from a source that has none to offer, where an empty string would "
                    + "be indistinguishable from a blank suggestion.",
            },
        },
        Required = new HashSet<string>(StringComparer.Ordinal) { "pointer", "code", "message" },
    };
}
