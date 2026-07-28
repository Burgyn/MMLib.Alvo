using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Maps the five minimal-API delegates one entity gets, onto the five <c>IAlvoData</c> members.
/// </summary>
/// <remarks>
/// <para>
/// <b>The entity name is a route literal, never a <c>{entity}</c> parameter.</b> A catch-all would map a
/// route for an entity the descriptor does not declare and answer it from the store — turning a routing
/// question into a port question, and making the OpenAPI document unable to list real paths. With
/// literals, "this entity does not exist" is a 404 that routing produces before anything is resolved.
/// </para>
/// <para>
/// <b>PATCH, not PUT.</b> <c>UpdateAsync</c> is partial by contract — "a field this dictionary does not
/// mention keeps its stored value" — so a PUT would advertise whole-resource replacement the port does
/// not perform.
/// </para>
/// <para>
/// Every delegate reads its caller from <see cref="IAlvoContextAccessor"/>, which
/// <see cref="AlvoContextFilter"/> published, and hands it to the port explicitly: the port takes
/// <see cref="AlvoContext"/> as a parameter on purpose, and this layer neither re-checks nor bypasses a
/// single authorization decision.
/// </para>
/// </remarks>
internal static class DataApiEndpoints
{
    /// <summary>Maps one entity's five routes under <paramref name="prefix"/>.</summary>
    /// <param name="endpoints">The route builder to map onto.</param>
    /// <param name="entity">The entity as the applied schema declares it.</param>
    /// <param name="prefix">The normalized route prefix, with no trailing slash.</param>
    /// <param name="options">The API options the delegates read paging defaults from.</param>
    /// <param name="filters">Builds the authorization filter each endpoint carries.</param>
    internal static void Map(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string prefix,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters)
    {
        var collection = $"{prefix}/{entity.Name}";
        var item = $"{collection}/{{id:guid}}";

        MapList(endpoints, entity, collection, options, filters);
        MapGet(endpoints, entity, item, filters);
        MapCreate(endpoints, entity, collection, options, filters);
        MapUpdate(endpoints, entity, item, options, filters);
        MapDelete(endpoints, entity, item, filters);
    }

    private static void MapList(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters) =>
        endpoints.MapGet(pattern, (IAlvoData data, IAlvoContextAccessor caller, CancellationToken ct) =>
                DataApiFailures.GuardAsync(async () =>
                {
                    // Task 4: the query string becomes the AlvoQuery here — filter, order, limit, offset,
                    // after, select — validated against this entity's own fields before the port sees it.
                    var query = new AlvoQuery { Entity = entity.Name, Limit = options.DefaultPageSize };
                    var page = await data.QueryAsync(query, Caller(caller), ct).ConfigureAwait(false);
                    return Json(DataApiPage.From(page));
                }))
            .AddEndpointFilter(filters.For(entity.Name, DataOperation.List));

    private static void MapGet(
        IEndpointRouteBuilder endpoints, EntitySchema entity, string pattern, AlvoContextFilterFactory filters) =>
        endpoints.MapGet(pattern, (Guid id, IAlvoData data, IAlvoContextAccessor caller, CancellationToken ct) =>
                DataApiFailures.GuardAsync(async () =>
                {
                    var record = await data.GetAsync(entity.Name, id, Caller(caller), ct).ConfigureAwait(false);

                    // Task 6: the ETag for this row's version is added here.
                    // A row the caller's policy excludes reads exactly like one that was never there, so
                    // this 404 is the same 404 AlvoRecordNotFoundException produces.
                    return record is null ? DataApiFailures.NotFound() : Json(record.Values);
                }))
            .AddEndpointFilter(filters.For(entity.Name, DataOperation.Get));

    private static void MapCreate(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters) =>
        endpoints.MapPost(pattern, (HttpContext http, IAlvoData data, IAlvoContextAccessor caller, CancellationToken ct) =>
                DataApiFailures.GuardAsync(async () =>
                {
                    var (values, failure) = await JsonPayloadReader
                        .ReadAsync(http.Request, entity, options, ct).ConfigureAwait(false);
                    if (failure is not null)
                    {
                        return DataApiFailures.Malformed(failure);
                    }

                    // Task 5: schema-derived validation runs here, reporting every violation.
                    // Task 7: the Idempotency-Key header becomes the AlvoIdempotency token.
                    var record = await data.CreateAsync(entity.Name, values!, Caller(caller), null, ct).ConfigureAwait(false);
                    return Created($"{pattern}/{AssignedId(record)}", record.Values);
                }))
            .AddEndpointFilter(filters.For(entity.Name, DataOperation.Create));

    private static void MapUpdate(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters) =>
        endpoints.MapPatch(pattern, (Guid id, HttpContext http, IAlvoData data, IAlvoContextAccessor caller, CancellationToken ct) =>
                DataApiFailures.GuardAsync(async () =>
                {
                    var (values, failure) = await JsonPayloadReader
                        .ReadAsync(http.Request, entity, options, ct).ConfigureAwait(false);
                    if (failure is not null)
                    {
                        return DataApiFailures.Malformed(failure);
                    }

                    // Task 5: schema-derived validation runs here.
                    // Task 6: the If-Match header becomes the AlvoPrecondition passed below.
                    var record = await data.UpdateAsync(entity.Name, id, values!, Caller(caller), null, ct).ConfigureAwait(false);
                    return Json(record.Values);
                }))
            .AddEndpointFilter(filters.For(entity.Name, DataOperation.Update));

    private static void MapDelete(
        IEndpointRouteBuilder endpoints, EntitySchema entity, string pattern, AlvoContextFilterFactory filters) =>
        endpoints.MapDelete(pattern, (Guid id, IAlvoData data, IAlvoContextAccessor caller, CancellationToken ct) =>
                DataApiFailures.GuardAsync(async () =>
                {
                    // Task 6: the If-Match header becomes the AlvoPrecondition passed below.
                    await data.DeleteAsync(entity.Name, id, Caller(caller), null, ct).ConfigureAwait(false);
                    return Results.NoContent();
                }))
            .AddEndpointFilter(filters.For(entity.Name, DataOperation.Delete));

    /// <summary>
    /// The caller <see cref="AlvoContextFilter"/> published for this request, or
    /// <see cref="AlvoContext.Anonymous"/> when it published none.
    /// </summary>
    /// <remarks>
    /// No principal means an anonymous caller — the filter deliberately publishes nothing for one, since
    /// there is no key to describe — so this is the fallback rather than a sentinel to decode. It is also
    /// the fail-<em>closed</em> direction if an endpoint were ever mapped without the filter: least
    /// privilege, judged by a default-deny policy, rather than the previous throw. That the filter really
    /// is on every endpoint is proved per verb instead
    /// (<c>DataApiAuthTests</c>' five scope-gating facts), which is a structural claim a runtime guard
    /// could not make.
    /// </remarks>
    private static AlvoContext Caller(IAlvoContextAccessor accessor) =>
        accessor.Principal?.Context ?? AlvoContext.Anonymous;

    /// <summary>
    /// The id the store assigned. Asserted rather than interpolated: <c>IAlvoData</c>'s contract is that
    /// a returned record carries every framework-managed column, <c>id</c> included, so a missing one is
    /// a broken invariant (family 3, rendered 500) — not a reason to emit a <c>Location</c> header ending
    /// in a slash and call the create a success.
    /// </summary>
    private static Guid AssignedId(AlvoRecord record) =>
        record["id"] as Guid?
        ?? throw new InvalidOperationException(
            "The created record carries no 'id'. IAlvoData guarantees every framework-managed column on a "
            + "returned record, so this is an implementation invariant, not a caller error.");

    /// <summary>
    /// Writes a response with Alvo's own serializer options rather than the host's — see
    /// <see cref="DataApiJson"/> for why a row's field names are a contract and not presentation.
    /// </summary>
    private static IResult Json<T>(T value) => Results.Json(value, DataApiJson.Options);

    /// <summary>
    /// The 201 for a created row: the <c>Location</c> of the new row plus its representation, written with
    /// Alvo's own options like every other response.
    /// </summary>
    /// <remarks>
    /// Not <c>Results.Created</c>, which serializes through whatever <c>JsonOptions</c> the host
    /// configured — the one response path that quietly escaped <see cref="Json{T}"/> and would have put a
    /// created row's field names under a host's <c>DictionaryKeyPolicy</c> while every other path kept
    /// them verbatim.
    /// </remarks>
    /// <param name="location">The path of the created row.</param>
    /// <param name="values">The created row's field values.</param>
    private static CreatedRecordResult Created(string location, IReadOnlyDictionary<string, object?> values) =>
        new(location, values);

    private sealed class CreatedRecordResult(string location, IReadOnlyDictionary<string, object?> values) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers.Location = location;
            return Results.Json(values, DataApiJson.Options, statusCode: StatusCodes.Status201Created)
                .ExecuteAsync(httpContext);
        }
    }
}
