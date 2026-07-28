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
        endpoints.MapGet(pattern, (
                    HttpContext http,
                    IAlvoData data,
                    IPolicyEngine policies,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                DataApiFailures.GuardAsync(async () =>
                {
                    var context = Caller(caller);
                    if (!QueryStringParser.TryParse(
                            http.Request.Query, entity, MaskedFields(policies, entity.Name, context), options,
                            out var request, out var violations))
                    {
                        return DataApiFailures.MalformedQuery(violations);
                    }

                    var page = await data.QueryAsync(request!.Query, context, ct).ConfigureAwait(false);
                    return Json(DataApiPage.From(page, request.Select));
                }))
            .Protect(entity, DataOperation.List, filters);

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
            .Protect(entity, DataOperation.Get, filters);

    private static void MapCreate(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters) =>
        endpoints.MapPost(pattern, (
                    HttpContext http,
                    IAlvoData data,
                    IPolicyEngine policies,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                DataApiFailures.GuardAsync(async () =>
                {
                    var context = Caller(caller);
                    EnsureOperationIsAllowed(policies, entity.Name, DataOperation.Create, context);

                    var (values, failure) = await JsonPayloadReader
                        .ReadAsync(http.Request, entity, options, ct).ConfigureAwait(false);
                    if (failure is not null)
                    {
                        return DataApiFailures.Malformed(failure);
                    }

                    // Task 5: schema-derived validation runs here, reporting every violation.
                    // Task 7: the Idempotency-Key header becomes the AlvoIdempotency token.
                    var record = await data.CreateAsync(entity.Name, values!, context, null, ct).ConfigureAwait(false);
                    return Created($"{pattern}/{AssignedId(record)}", record.Values);
                }))
            .Protect(entity, DataOperation.Create, filters);

    private static void MapUpdate(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters) =>
        endpoints.MapPatch(pattern, (
                    Guid id,
                    HttpContext http,
                    IAlvoData data,
                    IPolicyEngine policies,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                DataApiFailures.GuardAsync(async () =>
                {
                    var context = Caller(caller);
                    EnsureOperationIsAllowed(policies, entity.Name, DataOperation.Update, context);

                    var (values, failure) = await JsonPayloadReader
                        .ReadAsync(http.Request, entity, options, ct).ConfigureAwait(false);
                    if (failure is not null)
                    {
                        return DataApiFailures.Malformed(failure);
                    }

                    // Task 5: schema-derived validation runs here.
                    // Task 6: the If-Match header becomes the AlvoPrecondition passed below.
                    var record = await data.UpdateAsync(entity.Name, id, values!, context, null, ct).ConfigureAwait(false);
                    return Json(record.Values);
                }))
            .Protect(entity, DataOperation.Update, filters);

    private static void MapDelete(
        IEndpointRouteBuilder endpoints, EntitySchema entity, string pattern, AlvoContextFilterFactory filters) =>
        endpoints.MapDelete(pattern, (Guid id, IAlvoData data, IAlvoContextAccessor caller, CancellationToken ct) =>
                DataApiFailures.GuardAsync(async () =>
                {
                    // Task 6: the If-Match header becomes the AlvoPrecondition passed below.
                    await data.DeleteAsync(entity.Name, id, Caller(caller), null, ct).ConfigureAwait(false);
                    return Results.NoContent();
                }))
            .Protect(entity, DataOperation.Delete, filters);

    /// <summary>
    /// Attaches the authorization filter <b>and</b> the operation marker in one call, so an endpoint
    /// carrying one without the other cannot be written.
    /// </summary>
    /// <remarks>
    /// The marker exists because <c>AddEndpointFilter</c> leaves nothing in <c>Endpoint.Metadata</c>: with
    /// no marker, "every generated endpoint is gated" could only be evidenced one literal path and verb at a
    /// time, and a sixth endpoint added later would be covered by nothing. Fusing the two into one call is
    /// the same structural move the rest of this PR keeps making — make the wrong thing unrepresentable
    /// instead of documenting the trap.
    /// </remarks>
    /// <param name="builder">The route just mapped.</param>
    /// <param name="entity">The entity the endpoint serves.</param>
    /// <param name="operation">The operation the endpoint performs, and the one to gate it as.</param>
    /// <param name="filters">Builds the filter for that entity and operation.</param>
    private static RouteHandlerBuilder Protect(
        this RouteHandlerBuilder builder,
        EntitySchema entity,
        DataOperation operation,
        AlvoContextFilterFactory filters) =>
        builder
            .AddEndpointFilter(filters.For(entity.Name, operation))
            .WithMetadata(new DataApiOperationMetadata(entity.Name, operation));

    /// <summary>
    /// Refuses a write whose policy decision is already a denial, <b>before</b> the request body is read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two reasons, neither of them confidentiality. First, resource cost: parsing up to
    /// <see cref="AlvoApiOptions.MaxRequestBodyBytes"/> on behalf of a caller who cannot succeed is a
    /// denial-of-service amplifier, and it is the same reasoning the payload bounds exist for. Second,
    /// precedence: an unauthorized caller must be told they are unauthorized, not that their body was
    /// malformed — the second answer sends an agent to fix the wrong thing.
    /// </para>
    /// <para>
    /// <b>This does not become a second authorization authority.</b> It refuses only what the port would
    /// refuse anyway, from the same engine, catalog and context — and the port resolves the decision again
    /// and remains the authority, so nothing is admitted here that the port would refuse. It cannot
    /// pre-empt a <c>WITH CHECK</c> or tenant-scope failure, which need the candidate post-image and stay
    /// where they belong.
    /// </para>
    /// <para>
    /// It raises the port's own exception rather than composing a result, so the refusal a caller sees is
    /// byte-for-byte the one the port produces — a distinct wording here would be a way to tell "refused
    /// before the body" from "refused after it".
    /// </para>
    /// </remarks>
    /// <exception cref="AlvoAuthorizationException">No policy allows this operation for this caller.</exception>
    private static void EnsureOperationIsAllowed(
        IPolicyEngine policies, string entity, DataOperation operation, AlvoContext context)
    {
        var decision = policies.Resolve(entity, operation, context);
        if (decision.IsDenied)
        {
            throw new AlvoAuthorizationException(decision.DenyReason!);
        }
    }

    /// <summary>
    /// The caller <see cref="AlvoContextFilter"/> published for this request, or
    /// <see cref="AlvoContext.Anonymous"/> when it published none.
    /// </summary>
    /// <remarks>
    /// No principal means an anonymous caller — the filter deliberately publishes nothing for one, since
    /// there is no key to describe — so this is the fallback rather than a sentinel to decode. It is also
    /// the fail-<em>closed</em> direction if an endpoint were ever mapped without the filter: least
    /// privilege, judged by a default-deny policy, rather than the previous throw. That the filter really is
    /// on every endpoint is now proved two ways, and both are needed: <see cref="Protect"/> plus
    /// <c>DataApiRoutingTests.Every_generated_endpoint_carries_an_operation_marker_matching_its_verb</c> prove
    /// the gate is <em>attached</em> to whatever is mapped, and <c>DataApiAuthTests</c>' five per-verb facts
    /// prove it <em>refuses</em>.
    /// </remarks>
    private static AlvoContext Caller(IAlvoContextAccessor accessor) =>
        accessor.Principal?.Context ?? AlvoContext.Anonymous;

    /// <summary>
    /// The fields this caller may not read — the mask this caller's <c>list</c> policy resolved, so a filter, sort or projection naming a
    /// masked field is refused exactly as one naming an undeclared field is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one thing the request layer takes from a policy decision, and it takes nothing else.</b>
    /// Whether the caller may list at all stays the port's answer — resolved again inside
    /// <c>QueryAsync</c> — so the "neither re-checks nor bypasses a single authorization decision" rule holds:
    /// no request is admitted here that the port would refuse, and none is refused here that the port would
    /// admit.
    /// </para>
    /// <para>
    /// It has to happen <em>before</em> parsing because the alternative is an oracle. Leave the mask out and a
    /// filter over a masked field is refused by the port (403) while one over a field that does not exist is
    /// refused by the parser (422) — and that one-bit difference answers "does this entity have a field called
    /// X" for any caller who can compare two responses. §2.1's warning is exactly that: a filter over a hidden
    /// field leaks its value one comparison at a time.
    /// </para>
    /// <para>
    /// A denied decision carries an empty mask, which is correct rather than lax: the port refuses the whole
    /// read before any field name matters, so nothing is disclosed by parsing a query that will not be served.
    /// </para>
    /// </remarks>
    private static IReadOnlySet<string> MaskedFields(IPolicyEngine policies, string entity, AlvoContext context) =>
        policies.Resolve(entity, DataOperation.List, context).HiddenFields;

    /// <summary>
    /// The id the store assigned. Asserted rather than interpolated: <c>IAlvoData</c>'s contract is that
    /// a returned record carries every framework-managed column, <c>id</c> included, so a missing one is
    /// a broken invariant (family 3, rendered 500) — not a reason to emit a <c>Location</c> header ending
    /// in a slash and call the create a success.
    /// </summary>
    private static Guid AssignedId(AlvoRecord record) =>
        record[AlvoManagedColumns.Id] as Guid?
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
