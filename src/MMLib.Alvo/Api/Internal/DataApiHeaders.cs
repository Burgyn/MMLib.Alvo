using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The response headers a generated endpoint sets, published per response so a client knows which ones it may
/// rely on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Headers a caller has to act on are not a footnote.</b> An <c>ETag</c> is what a later <c>If-Match</c>
/// carries, a <c>Location</c> is where the created row lives, and a <c>WWW-Authenticate</c> is how an agent
/// discovers it should have authenticated at all — none of which a client can infer from a status code.
/// </para>
/// <para>
/// <b><c>Cache-Control</c> is on every response, refusals included</b>, because
/// <see cref="NoStoreResponseFilter"/> is the first filter on every generated endpoint and therefore also
/// stamps the 401 and 403 the authorization filter short-circuits with. Documenting it per response rather
/// than once in the overview is the accurate encoding: it is a property of each response, and a reader
/// checking one endpoint's entry should not have to reconstruct it from a preamble.
/// </para>
/// <para>
/// <b>An <c>ETag</c> is published only for an entity whose rows carry a version.</b>
/// <see cref="AlvoManagedColumns.VersionColumn"/> is the authority — a non-audited entity has no column the
/// framework writes on every change, so no tag is ever minted for it and promising one would be a lie a
/// client only discovers when its <c>If-Match</c> has nothing to send.
/// </para>
/// </remarks>
internal static class DataApiHeaders
{
    /// <summary>The headers <paramref name="response"/> carries on <paramref name="entity"/>.</summary>
    /// <param name="response">The response being described.</param>
    /// <param name="entity">
    /// The entity the endpoint serves, or <see langword="null"/> for a shared refusal component. A refusal
    /// carries no <c>ETag</c> — it wrote no row — so the entity is genuinely not needed rather than defaulted.
    /// </param>
    /// <param name="document">The document the shared header components are referenced from.</param>
    internal static Dictionary<string, IOpenApiHeader> For(
        DataApiDocumentation.Response response, EntitySchema? entity, OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(document);

        var headers = new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal)
        {
            [CacheControl] = new OpenApiHeaderReference(CacheControl, document),
        };

        if (CarriesEntityTag(response.Status) && entity is not null
            && AlvoManagedColumns.VersionColumn(entity) is not null)
        {
            headers[EntityTagHeader] = new OpenApiHeaderReference(EntityTagHeader, document);
        }

        if (response.Status == StatusCodes.Status201Created)
        {
            headers[LocationHeader] = new OpenApiHeaderReference(LocationHeader, document);
        }

        if (response.Status == StatusCodes.Status401Unauthorized)
        {
            headers[ChallengeHeader] = new OpenApiHeaderReference(ChallengeHeader, document);
        }

        return headers;
    }

    /// <summary>
    /// Registers the four headers as document components, so each is described once rather than once per
    /// response object.
    /// </summary>
    /// <remarks>
    /// The component id is the header's own field name, which is what a reader looking for
    /// <c>#/components/headers/ETag</c> expects to find. Header components live in their own map, so the name
    /// cannot collide with a schema or a parameter component.
    /// </remarks>
    /// <param name="document">The document being built.</param>
    internal static void AddTo(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.AddComponent(CacheControl, NoStore);
        document.AddComponent(EntityTagHeader, EntityTag);
        document.AddComponent(LocationHeader, Location);
        document.AddComponent(ChallengeHeader, Challenge);
    }

    private const string CacheControl = "Cache-Control";

    private const string EntityTagHeader = "ETag";

    private const string LocationHeader = "Location";

    private const string ChallengeHeader = "WWW-Authenticate";

    /// <summary>
    /// Which statuses carry a tag: the ones that wrote a row, plus the 304 that stands in for one.
    /// </summary>
    /// <remarks>
    /// A 201 is included because the create already re-read the row, so it has a stored version exactly as a
    /// 200 does — and a caller who had to issue a read before their first conditional write would race the
    /// window the tag closes. The 304 repeats it because RFC 9110 §15.4.5 asks a 304 to carry the fields a 200
    /// would have.
    /// </remarks>
    private static bool CarriesEntityTag(int status) => status is StatusCodes.Status200OK
        or StatusCodes.Status201Created or StatusCodes.Status304NotModified;

    private static OpenApiHeader NoStore => new()
    {
        Description =
            "Always `no-store`. These representations are policy-masked per caller and the `ETag` is minted "
            + "over the row's version rather than over the response bytes, so no cache — shared or private — "
            + "may keep the body. Keeping the tag alone is not caching a representation, which is what makes a "
            + "conditional write still possible.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        Example = JsonValue.Create("no-store"),
    };

    private static OpenApiHeader EntityTag => new()
    {
        Description =
            "The row's version as a strong entity tag. Send it back verbatim as `If-Match` to make a later "
            + "write conditional; it is compared octet-for-octet, so it must not be reformatted. Absent when "
            + "the row has no version this caller can be given.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        Example = JsonValue.Create("\"638712345678900000\""),
    };

    private static OpenApiHeader Location => new()
    {
        Description = "The path of the created row, under the same route prefix the create was sent to.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    private static OpenApiHeader Challenge => new()
    {
        Description =
            "The RFC 7235 challenge, naming the scheme and the request header a credential is read from — so "
            + "an agent can discover how to authenticate rather than guess. The header name is host "
            + "configuration, which is why the challenge states it rather than assuming a default.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };
}
