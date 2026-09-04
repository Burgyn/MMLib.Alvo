using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Maps the six minimal-API delegates one entity gets, onto the five <c>IAlvoData</c> members.
/// </summary>
/// <remarks>
/// <para>
/// <b>The entity name is a route literal, never a <c>{entity}</c> parameter.</b> A catch-all would map a
/// route for an entity the descriptor does not declare and answer it from the store — turning a routing
/// question into a port question, and making the OpenAPI document unable to list real paths. With
/// literals, "this entity does not exist" is a 404 that routing produces before anything is resolved.
/// </para>
/// <para>
/// <b>Six delegates, five port members: the two collection reads are one read.</b>
/// <c>POST {prefix}/{entity}/query</c> takes the same parameters in a JSON body, for a filter a request
/// line cannot carry (#107), and reaches <c>QueryStringParser</c> through the same
/// <see cref="PageAsync"/> tail — so there is exactly one place the projection, the count preference and
/// the page envelope are assembled, and no second grammar for the two to disagree over.
/// </para>
/// <para>
/// <b>PATCH, not PUT.</b> <c>UpdateAsync</c> is partial by contract — "a field this dictionary does not
/// mention keeps its stored value" — so a PUT would advertise whole-resource replacement the port does
/// not perform.
/// </para>
/// <para>
/// Every delegate reads its caller from <see cref="IAlvoContextAccessor"/>, which
/// <see cref="AlvoContextFilter"/> published, and hands it to the port explicitly: the port takes
/// <see cref="AlvoContext"/> as a parameter on purpose.
/// </para>
/// <para>
/// <b>Five of the six delegates resolve the operation's decision before doing any work, and none of them
/// is the authority for it.</b> The distinction is the whole of this layer's relationship with
/// authorization, and it is worth stating precisely rather than as "this layer never re-checks a decision",
/// which the code contradicts at four call sites. What each delegate does is refuse, up front, exactly what
/// the port would refuse anyway — same engine, same catalog, same context — and then call the port, which
/// resolves again and remains the authority. So nothing is admitted here that the port would refuse, and
/// nothing is refused here that the port would admit. The one exception, <c>GET {id}</c>, has no such call because
/// there it would be observationally inert; <see cref="EnsureOperationIsAllowed"/> carries both the reason
/// and the trigger for adding it.
/// </para>
/// </remarks>
internal static class DataApiEndpoints
{
    /// <summary>Maps one entity's six routes under <paramref name="prefix"/>.</summary>
    /// <param name="endpoints">The route builder to map onto.</param>
    /// <param name="entity">The entity as the applied schema declares it.</param>
    /// <param name="prefix">The normalized route prefix, with no trailing slash.</param>
    /// <param name="options">The API options the delegates read paging defaults from.</param>
    /// <param name="filters">Builds the authorization filter each endpoint carries.</param>
    /// <param name="formats">The applied descriptor's compiled field formats, shared by every endpoint.</param>
    /// <param name="conventions">The conventions the host attached to <c>MapAlvoDataApi()</c>.</param>
    internal static void Map(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string prefix,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        FormatCatalog formats,
        AlvoDataApiConventions conventions)
    {
        var collection = $"{prefix}/{entity.Name}";
        var item = $"{collection}/{{id:guid}}";
        var query = $"{collection}/query";

        MapList(endpoints, entity, collection, options, filters, conventions);
        MapQuery(endpoints, entity, query, options, filters, conventions);
        MapGet(endpoints, entity, item, filters, conventions);
        MapCreate(endpoints, entity, collection, options, filters, formats, conventions);
        MapUpdate(endpoints, entity, item, options, filters, formats, conventions);
        MapDelete(endpoints, entity, item, options, filters, conventions);
    }

    private static void MapList(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        AlvoDataApiConventions conventions) =>
        endpoints.MapGet(pattern, (
                    HttpContext http,
                    IAlvoData data,
                    IPolicyEngine policies,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                ProblemResultFactory.GuardAsync(async () =>
                {
                    var context = Caller(caller);
                    var decision = EnsureOperationIsAllowed(
                        policies, entity.Name, DataApiEndpointKind.List.ToDataOperation(), context);

                    return await PageAsync(
                        http, data, entity, options, decision, http.Request.Query, context, ct)
                        .ConfigureAwait(false);
                }))
            .Protect(entity, DataApiEndpointKind.List, filters, conventions);

    /// <summary>
    /// Maps the body-shaped collection read: the same parameters, the same parser and the same page, for a
    /// filter a request line cannot carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Gated as <c>list</c>, and it resolves that decision before reading a byte of the body</b> — the
    /// precedence a create keeps, for the same three reasons <see cref="EnsureOperationIsAllowed"/> gives:
    /// a denied caller must be told they are denied rather than that their body is malformed; parsing up to
    /// <see cref="AlvoApiOptions.MaxRequestBodyBytes"/> for a caller who cannot succeed is the amplifier the
    /// payload bounds exist against; and the allow decision's <see cref="PolicyDecision.HiddenFields"/> is
    /// the mask the parser needs, so the resolve replaces the one the mask already required.
    /// </para>
    /// <para>
    /// The operation it gates as is read from the kind rather than named a second time here. Two encodings
    /// of one mapping is how a route comes to be filtered as one operation and refused as another.
    /// </para>
    /// <para>
    /// <b>A POST that reads is not a cross-site vector here</b>, and the reason is worth stating so the
    /// absence of a token reads as a decision: a credential is presented in an explicit request header
    /// (<see cref="Auth.AlvoAuthOptions.HeaderName"/>) and never in a cookie, so a cross-site form POST
    /// carries none and is judged anonymous — which default-deny answers exactly as it answers any other
    /// credential-less caller.
    /// </para>
    /// <para>
    /// <c>Idempotency-Key</c> is accepted and ignored here. It is a read: no second row exists to prevent
    /// and nothing could be replayed. It is declared in the operation's own prose rather than left to be
    /// discovered, because <c>POST</c> is the verb that triggers the blanket-attach habit several SDKs have.
    /// </para>
    /// </remarks>
    private static void MapQuery(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        AlvoDataApiConventions conventions) =>
        endpoints.MapPost(pattern, (
                    HttpContext http,
                    IAlvoData data,
                    IPolicyEngine policies,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                ProblemResultFactory.GuardAsync(async () =>
                {
                    var context = Caller(caller);
                    var decision = EnsureOperationIsAllowed(
                        policies, entity.Name, DataApiEndpointKind.Query.ToDataOperation(), context);

                    var body = await QueryBodyReader.ReadAsync(http.Request, options, ct).ConfigureAwait(false);
                    if (body.Parameters is not { } parameters)
                    {
                        return ProblemResultFactory.MalformedQuery(body.Violations);
                    }

                    return await PageAsync(http, data, entity, options, decision, parameters, context, ct)
                        .ConfigureAwait(false);
                }))
            .Protect(entity, DataApiEndpointKind.Query, filters, conventions);

    /// <summary>
    /// Parses one set of list parameters and answers the page they describe — the whole of what the two
    /// collection reads have in common, which is everything after the parameters have been obtained.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One method rather than two, and that is the design rather than an economy.</b> #107's binding
    /// constraint is that the body must not become a second grammar; a second copy of this tail would be a
    /// second place for the projection, the count preference and the envelope to be assembled, which is
    /// where the two surfaces would begin to differ without any fact noticing.
    /// </para>
    /// <para>
    /// <b>The parser is handed the caller's mask, which is why the decision is resolved by the caller and
    /// passed in.</b> A filter over a hidden field must be refused exactly as one over an undeclared field
    /// is, and a denied decision carries an <em>empty</em> mask — see
    /// <see cref="EnsureOperationIsAllowed"/> for the oracle that closes and why the refusal has to come
    /// first.
    /// </para>
    /// </remarks>
    /// <param name="http">The request, read for its <c>Prefer</c> header and written for the applied one.</param>
    /// <param name="data">The port the page is read through.</param>
    /// <param name="entity">The entity being listed, as the applied schema declares it.</param>
    /// <param name="options">The API options the paging defaults and bounds come from.</param>
    /// <param name="decision">The caller's allow decision, whose mask the parser is handed.</param>
    /// <param name="parameters">The list parameters, from the query string or from the body.</param>
    /// <param name="context">The caller the read is performed as.</param>
    /// <param name="ct">A token to cancel the read.</param>
    private static async Task<IResult> PageAsync(
        HttpContext http,
        IAlvoData data,
        EntitySchema entity,
        AlvoApiOptions options,
        PolicyDecision decision,
        IQueryCollection parameters,
        AlvoContext context,
        CancellationToken ct)
    {
        if (!QueryStringParser.TryParse(
                parameters, entity, decision.HiddenFields, options, out var request, out var violations))
        {
            return ProblemResultFactory.MalformedQuery(violations);
        }

        var counted = PreferHeader.Count(http.Request.Headers[PreferHeader.Name]);
        var query = request!.Query with { IncludeTotalCount = counted is not null };
        var page = await data.QueryAsync(query, context, ct).ConfigureAwait(false);
        ApplyCountPreference(http.Response, counted);
        return Json(DataApiPage.From(page, request.Select));
    }

    /// <summary>
    /// Reports what was done with the caller's <c>count</c> preference, per RFC 7240 §3 — and always
    /// <c>count=exact</c>, because <c>planned</c> and <c>estimated</c> degrade to a real count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The degradation is the reason this header is sent at all.</b> A caller who asked for an estimate
    /// and silently received an exact count would have no way to know their preference was not honoured, and
    /// RFC 7240 gives exactly this channel for saying so. A preference this server does not recognise sets no
    /// header, which is how the standard says "ignored" is reported.
    /// </para>
    /// <para>
    /// <b>No <c>Vary: Prefer</c>.</b> RFC 7240 suggests it where a response varies by the header, and this
    /// one does — but every generated response already carries <c>Cache-Control: no-store</c> from
    /// <see cref="NoStoreResponseFilter"/>, so no cache may store the representation and a <c>Vary</c> has no
    /// addressee. Stated so its absence does not read as an oversight.
    /// </para>
    /// </remarks>
    private static void ApplyCountPreference(HttpResponse response, CountPreference? counted)
    {
        if (counted is not null)
        {
            response.Headers[PreferHeader.AppliedName] = "count=exact";
        }
    }

    private static void MapGet(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoContextFilterFactory filters,
        AlvoDataApiConventions conventions) =>
        endpoints.MapGet(pattern, (
                    Guid id,
                    HttpContext http,
                    IAlvoData data,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                ProblemResultFactory.GuardAsync(async () =>
                {
                    // The one delegate with no EnsureOperationIsAllowed call, and it takes no IPolicyEngine at
                    // all so the absence is visible in the signature. A guard here would be indistinguishable:
                    // GetAsync resolves the same decision and raises the same exception, so a denied reader sees
                    // the same 403 either way and no test can tell the two builds apart. A control nothing can
                    // distinguish is worse than absent, because it reads as a security check.
                    //
                    // ADD THE GUARD (and the parameter back) the moment this delegate interprets caller input
                    // before the port call. Two such changes are already sketched in this file, and each makes
                    // the parse-before-decide oracle reachable here: a `select` projection would need
                    // decision.HiddenFields and, unguarded, is MapList's oracle verbatim — see
                    // EnsureOperationIsAllowed; and honouring If-Match on a read, which Representation puts at
                    // about three lines, would answer 412 before 403, the defect
                    // ConcurrencyTests.A_denied_caller_is_refused_before_their_precondition_header_is pins on
                    // DELETE.
                    var record = await data.GetAsync(entity.Name, id, Caller(caller), ct).ConfigureAwait(false);

                    // A row the caller's policy excludes reads exactly like one that was never there, so
                    // this 404 is the same 404 AlvoRecordNotFoundException produces.
                    return record is null
                        ? ProblemResultFactory.NotFound()
                        : Representation(http.Request, record, entity);
                }))
            .Protect(entity, DataApiEndpointKind.Get, filters, conventions);

    private static void MapCreate(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        FormatCatalog formats,
        AlvoDataApiConventions conventions) =>
        endpoints.MapPost(pattern, (
                    HttpContext http,
                    IAlvoData data,
                    IPolicyEngine policies,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                ProblemResultFactory.GuardAsync(async () =>
                {
                    var context = Caller(caller);
                    var decision = EnsureOperationIsAllowed(
                        policies, entity.Name, DataApiEndpointKind.Create.ToDataOperation(), context);
                    EnsureUnconditional(http.Request);
                    var key = IdempotencyKey(http.Request, context, options);

                    var (body, violations) = await ReadAndValidateAsync(
                        http, entity, options, decision, isCreate: true, formats, data, context, ct)
                        .ConfigureAwait(false);
                    if (violations.Count > 0)
                    {
                        return ProblemResultFactory.Validation(violations);
                    }

                    var token = Idempotency(
                        key, http.Request.Method, entity, id: null, precondition: null, body.Document);
                    var record = await data.CreateAsync(entity.Name, body.Values, context, token, ct)
                        .ConfigureAwait(false);
                    return Created(pattern, record, entity);
                }))
            .Protect(entity, DataApiEndpointKind.Create, filters, conventions);

    private static void MapUpdate(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        FormatCatalog formats,
        AlvoDataApiConventions conventions) =>
        endpoints.MapPatch(pattern, (
                    Guid id,
                    HttpContext http,
                    IAlvoData data,
                    IPolicyEngine policies,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                ProblemResultFactory.GuardAsync(async () =>
                {
                    var context = Caller(caller);
                    var decision = EnsureOperationIsAllowed(
                        policies, entity.Name, DataApiEndpointKind.Update.ToDataOperation(), context);
                    var precondition = Precondition(http.Request);
                    var key = IdempotencyKey(http.Request, context, options);

                    var (body, violations) = await ReadAndValidateAsync(
                        http, entity, options, decision, isCreate: false, formats, data, context, ct)
                        .ConfigureAwait(false);
                    if (violations.Count > 0)
                    {
                        return ProblemResultFactory.Validation(violations);
                    }

                    var token = Idempotency(
                        key, http.Request.Method, entity, id, precondition, body.Document);
                    var record = await data
                        .UpdateAsync(entity.Name, id, body.Values, context, precondition, token, ct)
                        .ConfigureAwait(false);
                    return Row(record, entity);
                }))
            .Protect(entity, DataApiEndpointKind.Update, filters, conventions);

    private static void MapDelete(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        AlvoDataApiConventions conventions) =>
        endpoints.MapDelete(pattern, (
                    Guid id,
                    HttpContext http,
                    IAlvoData data,
                    IPolicyEngine policies,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                ProblemResultFactory.GuardAsync(async () =>
                {
                    var context = Caller(caller);
                    _ = EnsureOperationIsAllowed(
                        policies, entity.Name, DataApiEndpointKind.Delete.ToDataOperation(), context);

                    var precondition = Precondition(http.Request);
                    var key = IdempotencyKey(http.Request, context, options);

                    var token = Idempotency(
                        key, http.Request.Method, entity, id, precondition, document: null);
                    await data.DeleteAsync(entity.Name, id, context, precondition, token, ct)
                        .ConfigureAwait(false);
                    return Results.NoContent();
                }))
            .Protect(entity, DataApiEndpointKind.Delete, filters, conventions);

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
    /// <param name="kind">
    /// Which endpoint this is. The operation it is gated as comes from
    /// <see cref="DataApiEndpointKinds.ToDataOperation"/> rather than from a second parameter, so a route
    /// cannot be marked one kind and gated as another operation's.
    /// </param>
    /// <param name="filters">Builds the filter for that entity and operation.</param>
    /// <param name="conventions">
    /// The host's own conventions, applied <b>last</b> and in this same call. A route that is gated therefore
    /// also carries whatever the host attached to <c>MapAlvoDataApi()</c>, so "some endpoints were
    /// rate-limited and some were not" is unrepresentable — the same construction argument the authorization
    /// filter and the operation marker already rest on. Last, so a host's convention observes Alvo's own
    /// metadata and can override what it means to.
    /// </param>
    private static RouteHandlerBuilder Protect(
        this RouteHandlerBuilder builder,
        EntitySchema entity,
        DataApiEndpointKind kind,
        AlvoContextFilterFactory filters,
        AlvoDataApiConventions conventions)
    {
        var route = builder
            // First, so it wraps the authorization filter and stamps the 401 and 403 that filter answers
            // with too — see NoStoreResponseFilter for why an uncacheable response is what pays for a
            // strong entity tag minted over a row version rather than over the response bytes.
            .AddEndpointFilter(NoStoreResponseFilter.Instance)
            .AddEndpointFilter(filters.For(entity.Name, kind.ToDataOperation()))
            .WithMetadata(new DataApiOperationMetadata(entity.Name, kind))
            .Documenting(entity, kind);

        conventions.ApplyTo(route);

        return route;
    }

    /// <summary>
    /// Declares, as endpoint metadata, exactly the statuses this endpoint can answer with — so ApiExplorer, and
    /// therefore any OpenAPI document built over it, lists those and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read from <see cref="DataApiDocumentation.ResponsesFor"/>, which is the same table
    /// <see cref="AlvoDocumentTransformer"/> reads.</b> One authority, two consumers: a second hand-written
    /// list here is how the document comes to advertise a status no delegate produces, or to omit one it does.
    /// </para>
    /// <para>
    /// <b>Why declare it at all, when the transformer rewrites <c>responses</c> anyway.</b> A host that serves a
    /// document the transformer is not attached to (see <c>ApiSetup.AddAlvoApi</c>) still gets the right status
    /// codes from this metadata, where ApiExplorer's own inference for an <c>IResult</c>-returning delegate is
    /// a bare untyped 200.
    /// </para>
    /// <para>
    /// <b>No response type is named, deliberately — not even <c>ProducesProblem</c>'s.</b> A generated endpoint
    /// has no CLR payload type to name (its row is a dictionary), and <c>ProducesProblem</c> would have
    /// ApiExplorer generate a <c>ProblemDetails</c> component that Alvo's own <see cref="ProblemComponents"/>
    /// then supersedes — leaving an orphaned schema in the document that nothing references and that omits the
    /// <c>violations</c> array Alvo's refusals actually carry.
    /// </para>
    /// </remarks>
    /// <param name="builder">The route just mapped.</param>
    /// <param name="entity">The entity the endpoint serves, which decides whether a 304 is reachable.</param>
    /// <param name="kind">Which endpoint this is.</param>
    private static RouteHandlerBuilder Documenting(
        this RouteHandlerBuilder builder, EntitySchema entity, DataApiEndpointKind kind)
    {
        builder.WithTags(entity.Name);
        foreach (var response in DataApiDocumentation.ResponsesFor(kind, entity))
        {
            builder.Produces(response.Status, contentType: MediaTypeOf(response.Body));
        }

        return builder;
    }

    /// <summary>The media type a response of this kind is written with, or <see langword="null"/> for no body.</summary>
    private static string? MediaTypeOf(DataApiDocumentation.ResponseBody body) => body switch
    {
        DataApiDocumentation.ResponseBody.Problem => AlvoDocumentTransformer.ProblemMediaType,
        DataApiDocumentation.ResponseBody.None => null,
        _ => AlvoDocumentTransformer.JsonMediaType,
    };

    /// <summary>
    /// Refuses an operation whose policy decision is already a denial, <b>before</b> this delegate does any
    /// work of its own — reads the body, parses the query string, or looks at a precondition header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called on every generated route but one, and the uniformity is load-bearing rather than tidiness.</b> It started
    /// on the two write verbs, for two reasons that only applied there. First, resource cost: parsing up to
    /// <see cref="AlvoApiOptions.MaxRequestBodyBytes"/> on behalf of a caller who cannot succeed is a
    /// denial-of-service amplifier, and it is the same reasoning the payload bounds exist for. Second,
    /// precedence: an unauthorized caller must be told they are unauthorized, not that their body was
    /// malformed — the second answer sends an agent to fix the wrong thing.
    /// </para>
    /// <para>
    /// <b>On <c>list</c> a third reason applies, and it is a confidentiality one — leaving it off was a live
    /// oracle.</b> A denied decision carries an <em>empty</em>
    /// <see cref="PolicyDecision.HiddenFields"/> (<see cref="PolicyDecision.Deny"/> says so), so a denied
    /// lister used to reach <see cref="QueryStringParser"/> with no mask at all: a filter over a
    /// declared-but-hidden field parsed cleanly and earned the port's 403, while a filter over an undeclared
    /// field was refused by the parser as a 422. That one-bit difference answers "does this entity have a
    /// field called X" for exactly the caller most likely to be asking — and it is the leak §2.1 warns about
    /// and the entire reason the mask is threaded through the parser at all. Resolving here first makes both
    /// answers the same 403, and it costs nothing: the decision this returns is the mask the parser needs, so
    /// the resolve replaces the one the mask used to need rather than adding to it.
    /// </para>
    /// <para>
    /// On <c>delete</c> it buys precedence: a denied caller sending an unusable <c>If-Match</c> is answered
    /// 403 rather than the 412 <see cref="Precondition"/> would raise about a header they were never going to
    /// get to use.
    /// </para>
    /// <para>
    /// <b>Called on five of the six, not all six — <c>MapGet</c> deliberately has no such call.</b> There it
    /// would be indistinguishable: <c>GetAsync</c> resolves the same decision and raises the same exception, so
    /// a denied reader sees the same 403 either way and deleting the call fails nothing. It was added for
    /// uniformity and removed again for a better reason than symmetry — <b>a control no test can distinguish is
    /// worse than an absent one, because it reads as a security check</b>, and the next reader budgets trust
    /// against it.
    /// </para>
    /// <para>
    /// <b>The trigger for putting it back</b>, since "get is exempt" is true only of today's delegate: add it
    /// the moment <c>MapGet</c> interprets caller input before the port call. Two such changes are already
    /// sketched in this file. A <c>select</c> projection on a single row would need
    /// <see cref="PolicyDecision.HiddenFields"/> and, unguarded, would be the <c>list</c> oracle above verbatim.
    /// Honouring <c>If-Match</c> on a read — which <see cref="Representation"/> puts at about three lines —
    /// would answer 412 before 403, which is exactly the <c>delete</c> defect the paragraph above describes
    /// fixing. Neither is hypothetical; both are written down as things a later task may want.
    /// </para>
    /// <para>
    /// <b>This does not become a second authorization authority.</b> It refuses only what the port would
    /// refuse anyway, from the same engine, catalog and context — and the port resolves the decision again
    /// and remains the authority, so nothing is admitted here that the port would refuse. It cannot
    /// pre-empt a <c>WITH CHECK</c> or tenant-scope failure, or a row-level <c>USING</c> exclusion, all of
    /// which need a row and stay where they belong.
    /// </para>
    /// <para>
    /// It raises the port's own exception rather than composing a result, so the refusal a caller sees is
    /// byte-for-byte the one the port produces — a distinct wording here would be a way to tell "refused
    /// before the body" from "refused after it".
    /// </para>
    /// </remarks>
    /// <returns>
    /// The allow decision: <see cref="PolicyDecision.ReadOnlyFields"/> for a write's validator,
    /// <see cref="PolicyDecision.HiddenFields"/> for the list's query parser. Returned rather than resolved a
    /// second time, because both are per-caller CEL results and two resolutions of the same triple are two
    /// chances to judge a request against a mask the port will not apply.
    /// </returns>
    /// <exception cref="AlvoAuthorizationException">No policy allows this operation for this caller.</exception>
    private static PolicyDecision EnsureOperationIsAllowed(
        IPolicyEngine policies, string entity, DataOperation operation, AlvoContext context)
    {
        var decision = policies.Resolve(entity, operation, context);
        return decision.IsDenied ? throw new AlvoAuthorizationException(decision.DenyReason!) : decision;
    }

    /// <summary>
    /// Reads the request body and validates it against the entity's declared shape, returning what the port
    /// would be called with plus <b>every</b> reason it must not be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One helper for both write verbs, because the two differ in exactly one bit — whether an absent
    /// required field is a missing value (a create) or an unchanged one (a partial update) — and that bit is
    /// <c>IAlvoData.UpdateAsync</c>'s own contract rather than a judgement made here.
    /// </para>
    /// <para>
    /// The reader's <em>field-level</em> violations are carried into the validation rather than
    /// short-circuiting it, so a body with a mistyped value <em>and</em> a missing required field is answered
    /// once. The fields the reader already refused are excluded, because a value that never bound cannot be
    /// measured against the rules it never reached. A <em>body-level</em> refusal does short-circuit — see
    /// <see cref="JsonPayloadReader.Payload.BoundAsAnObject"/> for why validating a body that is not an object
    /// answers with advice about a request the caller never sent.
    /// </para>
    /// </remarks>
    private static async Task<(JsonPayloadReader.Payload Body, IReadOnlyList<AlvoViolation> Violations)>
        ReadAndValidateAsync(
            HttpContext http,
            EntitySchema entity,
            AlvoApiOptions options,
            PolicyDecision decision,
            bool isCreate,
            FormatCatalog formats,
            IAlvoData data,
            AlvoContext context,
            CancellationToken ct)
    {
        var payload = await JsonPayloadReader
            .ReadAsync(http.Request, entity, options, ct).ConfigureAwait(false);
        if (!payload.BoundAsAnObject)
        {
            return (payload, payload.Violations);
        }

        var validated = await RecordValidator.ValidateAsync(
            new RecordValidationRequest(
                entity,
                payload.Values,
                isCreate,
                decision.ReadOnlyFields,
                RefusedFields(payload.Violations),
                formats,
                data,
                context),
            ct).ConfigureAwait(false);

        return (payload, [.. payload.Violations, .. validated]);
    }

    /// <summary>The field names the body reader already refused, read back off its own violations' pointers.</summary>
    /// <remarks>
    /// Derived from the violations rather than tracked beside them, so the two cannot disagree: a reader that
    /// stops reporting a field also stops suppressing the validator's checks for it, which is the direction
    /// that fails loudly rather than silently. An unrecognised key's pointer lands in this set too and is
    /// simply inert — it names no field the entity declares, so there is nothing for it to suppress.
    /// </remarks>
    private static HashSet<string> RefusedFields(IReadOnlyList<AlvoViolation> violations) =>
        violations
            .Select(violation => PayloadViolations.FieldOf(violation.Pointer))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

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
    /// The id the store assigned. Asserted rather than interpolated: <c>IAlvoData</c>'s contract is that
    /// a returned record carries every framework-managed column, <c>id</c> included, so a missing one is
    /// a broken invariant (family 5, rendered 500) — not a reason to emit a <c>Location</c> header ending
    /// in a slash and call the create a success.
    /// </summary>
    private static Guid AssignedId(AlvoRecord record) =>
        record[AlvoManagedColumns.Id] as Guid?
        ?? throw new InvalidOperationException(
            "The created record carries no 'id'. IAlvoData guarantees every framework-managed column on a "
            + "returned record, so this is an implementation invariant, not a caller error.");

    /// <summary>
    /// The row version a write is conditional on: the caller's <c>If-Match</c>, read into the port's own
    /// precondition channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called before the request body is read</b>, for the reason
    /// <see cref="EnsureOperationIsAllowed"/> is: a precondition this layer cannot evaluate can never
    /// succeed, so refusing it first spends none of <see cref="AlvoApiOptions.MaxRequestBodyBytes"/> on
    /// it — and, the part that is a contract rather than an economy, it keeps a failed precondition from
    /// being reported as the <c>422</c> a body that also happens to be invalid would earn. "Only if
    /// unchanged" must never be answered with advice about a field.
    /// </para>
    /// <para>
    /// <b>Every outcome that is not a version is a <c>412</c>, never a <c>422</c> and never a shrug.</b> A
    /// header this API cannot compare cannot possibly match, and the caller's intent — "only if
    /// unchanged" — must not be reinterpreted as "unconditionally": that reinterpretation is precisely the
    /// lost update <c>If-Match</c> exists to prevent, and the caller would read a <c>200</c> as proof it
    /// did not happen. It raises the port's own exception so the refusal is rendered by the one authority
    /// that renders <see cref="AlvoProblemTypes.PreconditionFailed"/>.
    /// </para>
    /// <para>
    /// <b>A list of more than one tag is refused too.</b> RFC 9110 §13.1.1 lets <c>If-Match</c> carry
    /// several tags and succeed if <em>any</em> matches, and <see cref="AlvoPrecondition"/> carries exactly
    /// one version — so the disjunction cannot be expressed on the channel that does the comparing. The
    /// alternative, picking one tag from the list, would answer a question the caller did not ask.
    /// </para>
    /// <para>
    /// <b>A weak tag is refused explicitly, and it has to be.</b> RFC 9110 §13.1.1 requires the
    /// <em>strong</em> comparison function here, and <see cref="EntityTagHeaderValue"/> lifts the <c>W/</c>
    /// prefix into <see cref="EntityTagHeaderValue.IsWeak"/> — so what reaches
    /// <see cref="RowVersionETag.TryParse"/> is the opaque part alone, indistinguishable from a strong tag.
    /// Leaving the check to the parser's own leading-quote guard was a measured defect: <c>W/"…"</c> was
    /// accepted as a version, and the request then failed or succeeded on whether that version happened to
    /// be current, rather than on the header being uncomparable.
    /// </para>
    /// <para>
    /// <c>*</c> yields no precondition, and that is not a shrug either: it asks only that the row exist,
    /// which the port's own <see cref="AlvoRecordNotFoundException"/> already answers for every write.
    /// </para>
    /// </remarks>
    /// <param name="request">The request whose precondition headers to read.</param>
    /// <exception cref="AlvoPreconditionFailedException">The request carries a precondition this API cannot evaluate.</exception>
    private static AlvoPrecondition? Precondition(HttpRequest request)
    {
        var header = request.Headers.IfMatch;
        var precondition = header.Count == 0 ? null : IfMatch(header);

        // Last, so If-Match is evaluated first: RFC 9110 §13.2.2 fixes that order, and it is the order that
        // reports the more specific problem when a request carries both headers.
        EnsureNoIfNoneMatch(request, sentIfMatch: header.Count > 0);
        return precondition;
    }

    /// <summary>The one row version an <c>If-Match</c> names, or <see langword="null"/> for <c>*</c>.</summary>
    /// <param name="header">The <c>If-Match</c> field values, already known to be non-empty.</param>
    /// <exception cref="AlvoPreconditionFailedException">The header is not one tag this API can compare.</exception>
    private static AlvoPrecondition? IfMatch(StringValues header)
    {
        if (!EntityTagHeaderValue.TryParseStrictList(header, out var tags) || tags.Count != 1 || tags[0].IsWeak)
        {
            throw new AlvoPreconditionFailedException(UnusableIfMatch);
        }

        return tags[0].Equals(EntityTagHeaderValue.Any) ? null
            : RowVersionETag.TryParse(tags[0].Tag.Value, out var precondition) ? precondition
            : throw new AlvoPreconditionFailedException(UnusableIfMatch);
    }

    /// <summary>
    /// Refuses a write that carries <c>If-None-Match</c>, rather than ignoring the header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a write, RFC 9110 §13.1.2 makes <c>If-None-Match</c> a precondition like any other — "act only if
    /// the row is <em>not</em> at this version" — and <see cref="AlvoPrecondition"/> expresses only the
    /// positive form. Passing the request through would be the silently-ignored precondition
    /// <see cref="AlvoPrecondition.EnsureSupported"/> exists to refuse, one header along: the caller sent a
    /// condition, read a <c>200</c>, and overwrote a concurrent writer anyway.
    /// </para>
    /// <para>
    /// <b>Labelled deviation from RFC 9110 §13.1.2.</b> The spec would have a <em>non-matching</em>
    /// <c>If-None-Match</c> on a write simply succeed, and only a matching one answer 412; Alvo refuses the
    /// header outright, matching or not. The reason is that Alvo cannot evaluate it at all — there is no
    /// negative form on the port's channel — so "it did not match" is not something this API knows, and a
    /// conforming success would be indistinguishable from a precondition that was never checked. Alvo's rule
    /// that a standard is adopted rather than varied silently is why this is written down rather than left to
    /// be discovered from a 412.
    /// </para>
    /// </remarks>
    /// <param name="request">The request to inspect.</param>
    /// <param name="sentIfMatch">
    /// Whether the same request also carried a usable <c>If-Match</c>, which decides only the fix suggestion:
    /// telling a caller to send the header they already sent is worse than saying nothing.
    /// </param>
    /// <exception cref="AlvoPreconditionFailedException">The request carries <c>If-None-Match</c>.</exception>
    private static void EnsureNoIfNoneMatch(HttpRequest request, bool sentIfMatch)
    {
        if (request.Headers.IfNoneMatch.Count > 0)
        {
            throw new AlvoPreconditionFailedException(
                "'If-None-Match' is evaluated on a read and cannot condition a write here: this API compares a "
                + "record against the version a caller holds, never against a version it must not have. "
                + (sentIfMatch
                    ? "Drop 'If-None-Match' and keep the 'If-Match' you already sent, which is the precondition "
                        + "this write will honour."
                    : "Drop 'If-None-Match'; to make the write conditional, send 'If-Match' with the 'ETag' a "
                        + "previous response returned."));
        }
    }

    /// <summary>
    /// Refuses a create that carries a precondition header at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no stored record for a version to be compared against, and this API assigns the new record's
    /// id itself — so neither header can be evaluated, and the rule the rest of this feature is built on says
    /// what to do about a precondition that cannot be evaluated: refuse it. A <c>201</c> would tell a caller
    /// their condition held.
    /// </para>
    /// <para>
    /// Every <em>write</em> verb therefore either evaluates a precondition header or refuses it — this method
    /// for the create, <see cref="Precondition"/> and <see cref="EnsureNoIfNoneMatch"/> for the update and
    /// delete — because ignoring one on a write lets a caller believe they conditioned a change they did not.
    /// What the <em>read</em> side does with these headers, and why, is stated once on
    /// <see cref="Representation"/>.
    /// </para>
    /// </remarks>
    /// <param name="request">The request to inspect.</param>
    /// <exception cref="AlvoPreconditionFailedException">The create carries <c>If-Match</c> or <c>If-None-Match</c>.</exception>
    private static void EnsureUnconditional(HttpRequest request)
    {
        if (request.Headers.IfMatch.Count > 0 || request.Headers.IfNoneMatch.Count > 0)
        {
            throw new AlvoPreconditionFailedException(
                "A create carries no precondition: there is no stored record yet whose version could be "
                + "compared, and this API assigns the new record's id itself. Remove 'If-Match' and "
                + "'If-None-Match' from the create, and send 'If-Match' on the update that follows it.");
        }
    }

    /// <summary>
    /// The header a caller makes a create retry-safe with — the one from the IETF
    /// <c>httpapi-idempotency-key-header</c> draft, spelled as every BaaS and payment API spells it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A literal rather than an option: unlike <see cref="Auth.AlvoAuthOptions.HeaderName"/>, which an
    /// embedded host may have to move out of the way of its own credential header, this one is a published
    /// convention an agent already knows from its training data. Making it configurable would buy a host
    /// nothing and cost every client the ability to assume it. Internal, and the generated OpenAPI document
    /// advertises it by reading this constant (<see cref="DataApiParameters"/>) rather than spelling the header
    /// name a second time.
    /// </para>
    /// <para>
    /// <b>Read on the create only. On the other two write verbs it is accepted and <em>ignored</em> — a known
    /// limitation, not a claim that nothing is lost.</b> An earlier version of this remark said an ignored key
    /// "costs nothing" there, and that overstates it in a way worth correcting precisely, because the
    /// difference is the whole retry story:
    /// </para>
    /// <para>
    /// <b>The row's end state is unaffected; the outcome the client observes is not.</b> <c>UpdateAsync</c>
    /// assigns absolute values to named fields and <c>DeleteAsync</c> removes one row, so applying either twice
    /// leaves exactly the state applying it once leaves — no duplicate row exists to prevent, which is why this
    /// is not the lost-update rule <see cref="EnsureUnconditional"/> enforces for a precondition. But consider
    /// the case §2.1 wants keys for: a caller sends <c>PATCH … If-Match: "v1"</c>, <b>the 200 is lost</b> (a
    /// dropped connection, a timeout), and they retry the identical request. The write landed, so the row is at
    /// <c>v2</c>, so the retry is <b>412 — and the caller cannot attribute it</b>: "my own write landed" and
    /// "somebody else changed the row" are the same answer. The usual resolution for a 412 is to re-read,
    /// re-merge and re-apply, which in the second case clobbers a genuinely concurrent change. A key would have
    /// answered "this is your own write, here is its result". <c>DELETE</c> has the same shape (404 or 412 on
    /// the retry, indistinguishable from someone else's delete). So what is lost is exactly retrying without
    /// knowing whether the first attempt landed.
    /// </para>
    /// <para>
    /// <b>Why it is still ignored rather than refused or honoured.</b> Honouring it needs a third widening of
    /// <c>IAlvoData</c> plus a stored <em>replayable result</em> for an update, which is not this PR's shape.
    /// Refusing it would break the widespread client habit — Stripe's SDKs among them — of attaching the header
    /// to every mutating request, and would refuse requests that are perfectly serviceable. Ignoring is the
    /// least-bad third option, and it is only defensible <em>declared</em>: it is published in
    /// <see cref="DataApiDocumentation"/>'s update and delete prose, in these words, and neither operation
    /// lists the header as a parameter — a parameter is an invitation to send something.
    /// </para>
    /// </remarks>
    internal const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>
    /// The caller's idempotency key, or <see langword="null"/> when they sent no such header — refusing, up
    /// front, every header this API could not honour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called before the body is read</b>, for the reason <see cref="EnsureUnconditional"/> and
    /// <see cref="Precondition"/> are: a request carrying a key this API cannot serve cannot succeed whatever
    /// its body says, so it must not be answered with advice about a field — an agent told to fix its payload
    /// will fix the payload, resend, and be refused again. It also spends none of
    /// <see cref="AlvoApiOptions.MaxRequestBodyBytes"/> on a request that was already decided.
    /// </para>
    /// <para>
    /// <b>Every rule about the key itself is the port's, applied here by calling the port's own guard.</b>
    /// <see cref="AlvoIdempotency.EnsureUsableKey"/> is the authority for all three of blank, over-long and
    /// anonymous — it has to be, because an embedded host reaches <c>CreateAsync</c> with no HTTP in front of
    /// it — and calling it rather than restating its wording is what keeps "refused before the body"
    /// indistinguishable from "refused after it", exactly as <see cref="EnsureOperationIsAllowed"/> raises the
    /// port's own exception. It takes the <em>key</em> and not a token, which is why nothing here has to invent
    /// a fingerprint for a body it deliberately runs before reading.
    /// </para>
    /// <para>
    /// <b>What is left for this layer is the part that is about HTTP</b>, and it is exactly two things: a
    /// <em>repeated</em> header field, which the port cannot see because a token carries one key; and a bound
    /// <em>narrower</em> than the port's, which a host may configure
    /// (<see cref="AlvoApiOptions.MaxIdempotencyKeyBytes"/>) and which is counted in UTF-8 bytes for the reason
    /// stated there.
    /// </para>
    /// <para>
    /// <b>A 422, not a 401.</b> Nothing failed authentication — no credential was presented and rejected —
    /// so a 401 would owe a <c>WWW-Authenticate</c> challenge for a request that never attempted to
    /// authenticate, and would blur the anonymous-versus-unusable-credential line the auth filter keeps
    /// disjoint. What the caller sent is a well-formed request asking for a facility that needs a stable
    /// identity to scope by, which is the port's malformed-request family and this layer's 422.
    /// </para>
    /// <para>
    /// <b>A repeated header is refused rather than resolved.</b> Two field values are two keys and this create
    /// can only be recorded under one — picking either answers a question the caller did not ask, the same
    /// reason a multi-tag <c>If-Match</c> is refused.
    /// </para>
    /// </remarks>
    /// <param name="request">The request whose header to read.</param>
    /// <param name="context">The caller the create is performed as.</param>
    /// <param name="options">The API options the key's byte bound is read from.</param>
    /// <exception cref="ArgumentException">The header is one this API cannot honour for this caller.</exception>
    private static string? IdempotencyKey(HttpRequest request, AlvoContext context, AlvoApiOptions options)
    {
        var header = request.Headers[IdempotencyKeyHeader];
        if (header.Count == 0)
        {
            return null;
        }

        if (header.Count > 1)
        {
            throw new ArgumentException(RepeatedIdempotencyKey);
        }

        var key = header[0];
        AlvoIdempotency.EnsureUsableKey(key, context);
        EnsureWithinConfiguredBound(key!, options);
        return key;
    }

    /// <summary>
    /// Refuses a key past <see cref="AlvoApiOptions.MaxIdempotencyKeyBytes"/> — the bound a host narrowed
    /// below the port's own.
    /// </summary>
    /// <remarks>
    /// Runs <em>after</em> the port's guard, so a key that breaks a rule both layers hold is refused in the
    /// port's wording rather than this layer's: the startup validator keeps this option at or below
    /// <see cref="AlvoIdempotency.MaxKeyBytes"/>, so the only keys this method can refuse are the ones only the
    /// host objects to. Bytes, matching the port, for the reason the option's own remarks give.
    /// </remarks>
    /// <param name="key">The caller's key, already known to be non-blank.</param>
    /// <param name="options">The options carrying the host's bound.</param>
    /// <exception cref="ArgumentException">The key is longer than this host allows.</exception>
    private static void EnsureWithinConfiguredBound(string key, AlvoApiOptions options)
    {
        if (Encoding.UTF8.GetByteCount(key) > options.MaxIdempotencyKeyBytes)
        {
            throw new ArgumentException(
                $"The '{IdempotencyKeyHeader}' header must be at most {options.MaxIdempotencyKeyBytes} bytes "
                + "when encoded as UTF-8. It is refused rather than shortened, because two keys that differ "
                + "only past that length would become one key and the second create would be answered with "
                + "the first create's row.");
        }
    }

    /// <summary>
    /// The token this write is performed under: the caller's key plus the fingerprint of the request it
    /// belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built <b>after</b> validation, because the fingerprint covers the body and a body that was refused
    /// never reaches the port at all — so a fingerprint over it would digest a request that was never
    /// performed and reserve the key against it.
    /// </para>
    /// <para>
    /// <b>A create's <paramref name="document"/> is non-null by construction and a delete's is null by
    /// contract</b>, so the invariant this used to assert — "a write reached the port with no parsed body" —
    /// now holds only for a create, and is asserted only there.
    /// </para>
    /// </remarks>
    /// <param name="key">The caller's key, or <see langword="null"/> when they sent none.</param>
    /// <param name="method">The request method, for the digest.</param>
    /// <param name="entity">The entity being written.</param>
    /// <param name="id">The row the write addresses, or <see langword="null"/> for a create.</param>
    /// <param name="precondition">The version the write is conditional on, or <see langword="null"/>.</param>
    /// <param name="document">The body as it was parsed, or <see langword="null"/> for a delete.</param>
    private static AlvoIdempotency? Idempotency(
        string? key,
        string method,
        EntitySchema entity,
        Guid? id,
        AlvoPrecondition? precondition,
        JsonObject? document)
    {
        if (key is null)
        {
            return null;
        }

        EnsureACreateParsedItsBody(id, document);

        return new AlvoIdempotency(
            key, IdempotencyFingerprint.Of(method, entity.Name, id, precondition, document));
    }

    /// <summary>
    /// Asserts the invariant that a create with no violations bound as an object — family 5, rendered 500,
    /// the same reasoning as <see cref="AssignedId"/>.
    /// </summary>
    /// <remarks>
    /// Scoped to a create, because it is only a create's invariant: a delete legitimately carries no body,
    /// and an update's is reported through the same violation path a create's is.
    /// </remarks>
    /// <param name="id">The row the write addresses, or <see langword="null"/> for a create.</param>
    /// <param name="document">The body as it was parsed.</param>
    private static void EnsureACreateParsedItsBody(Guid? id, JsonObject? document)
    {
        if (id is null && document is null)
        {
            throw new InvalidOperationException(
                "A create reached the port with no parsed body. JsonPayloadReader reports a body that is not "
                + "an object as a violation, and a violation is answered before this point.");
        }
    }

    /// <summary>The refusal for a request carrying the idempotency header more than once.</summary>
    /// <remarks>
    /// The one rule about the header that is this layer's rather than the port's: a token carries one key, so
    /// two field values are a question only HTTP can see. The caller's key is never echoed back — it is
    /// caller-supplied text, and nothing here reflects any.
    /// </remarks>
    private static string RepeatedIdempotencyKey =>
        $"The '{IdempotencyKeyHeader}' header must be sent at most once. Two values are two keys and a create "
        + "is recorded under one, so picking either would answer a question the caller did not ask.";

    /// <summary>The refusal for an <c>If-Match</c> this API cannot turn into exactly one row version.</summary>
    private const string UnusableIfMatch =
        "The 'If-Match' header is not one entity tag this API can compare. Send back a single 'ETag' exactly as "
        + "a previous response returned it, or '*' to require only that the record still exist. A precondition "
        + "that cannot be evaluated is refused rather than ignored, because ignoring it would be the lost "
        + "update the header exists to prevent.";

    /// <summary>
    /// The representation of a row a read produced: its values and entity tag, or the <c>304</c> when the
    /// caller's <c>If-None-Match</c> shows they already hold this version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a read goes through here. A conditional <em>read</em> is a bandwidth optimization whose answer is
    /// "you already have it"; on a write the same header would be a precondition, which
    /// <see cref="EnsureNoIfNoneMatch"/> refuses rather than answers.
    /// </para>
    /// <para>
    /// Comparison is ordinal over the opaque part and ignores the caller's <c>W/</c> prefix, which is exactly
    /// the <em>weak</em> comparison RFC 9110 §13.1.2 prescribes for <c>If-None-Match</c> — and deliberately
    /// not the strong one <see cref="Precondition"/> applies to <c>If-Match</c>. The two headers really do
    /// have different comparison functions in the spec.
    /// </para>
    /// <para>
    /// <b>The read side's two gaps, stated once and here.</b> <c>If-Match</c> is not honoured on a read, and
    /// neither header is honoured on a <em>list</em> (which has no version of its own to compare). The honest
    /// reason is <b>not</b> that a read cannot afford to evaluate them: the tag is minted two lines below and
    /// <see cref="IsAlreadyHeld"/> already parses the sibling header, so honouring <c>If-Match</c> on a single
    /// row would be about three lines. It is that doing so is <em>pointless</em> — RFC 9110 §13.1.1 on a
    /// <c>GET</c> means "send me the body only if it is still this version", and answering 412 instead of a
    /// body saves a caller nothing they cannot get by comparing the tag they were sent. So the gap is
    /// deliberate and cheap to close if a caller ever wants it, not deferred because it is expensive.
    /// </para>
    /// <para>
    /// That is also why the asymmetry with the write side is safe: an unhonoured header on a read costs a
    /// response body the caller said they already had, whereas an unhonoured one on a write costs somebody
    /// their change. <b>Both gaps are client-observable, so a code remark is not where they belong</b> — and both
    /// are now published: <see cref="DataApiDocumentation"/> states the ignored <c>If-Match</c> on the read
    /// operation and the two ignored precondition headers on the list, because a caller who cannot read this
    /// file has no other way to learn that a header they sent did nothing.
    /// </para>
    /// </remarks>
    /// <param name="request">The request whose <c>If-None-Match</c> to honour.</param>
    /// <param name="record">The row the port returned.</param>
    /// <param name="entity">The entity as the applied schema declares it.</param>
    private static IResult Representation(HttpRequest request, AlvoRecord record, EntitySchema entity)
    {
        var entityTag = RowVersionETag.For(record, entity);
        return entityTag is not null && IsAlreadyHeld(request, entityTag)
            ? new NotModifiedResult(entityTag)
            : new RecordResult(record, entityTag, StatusCodes.Status200OK, created: null);
    }

    /// <summary>Whether the caller's <c>If-None-Match</c> covers <paramref name="entityTag"/>.</summary>
    /// <remarks>
    /// A header this API cannot parse means "no match" rather than a refusal — see
    /// <see cref="Representation"/> for why a read may ignore what a write must refuse.
    /// </remarks>
    /// <param name="request">The request to read.</param>
    /// <param name="entityTag">The tag of the representation about to be written.</param>
    private static bool IsAlreadyHeld(HttpRequest request, string entityTag) =>
        EntityTagHeaderValue.TryParseStrictList(request.Headers.IfNoneMatch, out var tags)
        && tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any)
            || string.Equals(tag.Tag.Value, entityTag, StringComparison.Ordinal));

    /// <summary>
    /// Writes a response with Alvo's own serializer options rather than the host's — see
    /// <see cref="DataApiJson"/> for why a row's field names are a contract and not presentation.
    /// </summary>
    private static IResult Json<T>(T value) => Results.Json(value, DataApiJson.Options);

    /// <summary>The <c>200</c> for one row: its values plus the entity tag a later <c>If-Match</c> can carry.</summary>
    /// <param name="record">The row the port returned.</param>
    /// <param name="entity">The entity as the applied schema declares it.</param>
    private static RecordResult Row(AlvoRecord record, EntitySchema entity) =>
        new RecordResult(record, RowVersionETag.For(record, entity), StatusCodes.Status200OK, created: null);

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
    /// <param name="pattern">
    /// The collection route this endpoint was mapped with. Neither the request's <c>PathBase</c> nor a route
    /// group's prefix is applied here — both are request-time facts, resolved where the header is written
    /// (<see cref="CreatedRow"/>).
    /// </param>
    /// <param name="record">The created row.</param>
    /// <param name="entity">The entity as the applied schema declares it.</param>
    private static RecordResult Created(string pattern, AlvoRecord record, EntitySchema entity) =>
        new RecordResult(
            record,
            RowVersionETag.For(record, entity),
            StatusCodes.Status201Created,
            new CreatedRow(pattern, AssignedId(record)));

    /// <summary>
    /// The created row's <c>Location</c>, resolved against the request that created it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The collection path is read off the matched endpoint, not off the literal this was mapped with</b>
    /// (#121's second half). <c>app.MapGroup("/backend").MapAlvoDataApi()</c> is a supported call, and the
    /// mapped literal knows nothing about the group: a create under it advertised <c>/api/owners/&lt;id&gt;</c>
    /// and a client that followed the header got a 404. A grouped endpoint's own
    /// <see cref="RouteEndpoint.RoutePattern"/> is the <em>combined</em> one, so asking the endpoint is asking
    /// the router — there is no second place for the two to disagree.
    /// </para>
    /// <para>
    /// <b>Not <c>LinkGenerator</c>, deliberately.</b> Generating by route name would mean naming every one of
    /// every entity's routes, and route names are process-global: two <c>MapAlvoDataApi()</c> calls under two
    /// groups — the very shape this fixes — would then collide at startup. The create endpoint's pattern
    /// <em>is</em> the collection path and carries no parameter to substitute, so appending the id needs no
    /// generator.
    /// </para>
    /// <para>
    /// The mapped literal remains the fallback for a context with no <see cref="RouteEndpoint"/>, which is the
    /// pre-fix behaviour rather than a throw: a 201 whose row is already committed must not become a 500 over
    /// the shape of its own header.
    /// </para>
    /// </remarks>
    /// <param name="MappedPattern">The collection route literal this endpoint was mapped with.</param>
    /// <param name="Id">The created row's id.</param>
    private sealed record CreatedRow(string MappedPattern, Guid Id)
    {
        /// <summary>The header value: path base, route group, collection route and id, percent-encoded.</summary>
        /// <param name="httpContext">The request that created the row.</param>
        internal string Header(HttpContext httpContext) =>
            httpContext.Request.PathBase.Add($"{Collection(httpContext)}/{Id}").ToUriComponent();

        /// <summary>
        /// The matched endpoint's own collection path, or the mapped literal when there is no route endpoint.
        /// </summary>
        /// <remarks>
        /// A pattern with no leading <c>/</c> is normalized rather than trusted: <c>PathString</c> refuses one,
        /// and <c>MapGroup("backend")</c> is a spelling a host may well write.
        /// </remarks>
        /// <param name="httpContext">The request that created the row.</param>
        private string Collection(HttpContext httpContext)
        {
            var matched = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            var collection = string.IsNullOrEmpty(matched) ? MappedPattern : matched;
            return collection.StartsWith('/') ? collection : $"/{collection}";
        }
    }

    /// <summary>
    /// One row on the wire: its <c>ETag</c>, its <c>Location</c> on a create, and its values under
    /// <see cref="DataApiJson"/>'s options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <c>ETag</c> is set here rather than at each call site</b>, so a row cannot be written without
    /// one: every 200 and 201 of a row goes through this type, and the only way to omit the header is for
    /// <see cref="RowVersionETag.For"/> to answer that the row has no version to tag. A create is included
    /// because the write already re-read the row, so a 201 has a stored version exactly like a 200 does —
    /// and a caller who had to issue a GET before their first conditional write would race the very window
    /// the tag closes.
    /// </para>
    /// <para>
    /// <b>The <c>Location</c> is written with <c>ToUriComponent()</c>, never <c>PathString.Value</c>.</b>
    /// <c>Value</c> is the decoded form and a header field is not the place for one. Two reachable failures,
    /// neither hypothetical: a non-ASCII path base — <c>/účty</c>, and this repository's own fixtures put rows
    /// in "Košice" — makes Kestrel throw while encoding the response header as Latin-1, so a create that
    /// <em>already committed the row</em> answers 500; and a proxy-set <c>X-Forwarded-Prefix: /my%20app</c>
    /// comes back decoded, as a value no client can parse as a URI reference. RFC 9110 §10.2.2 asks for the
    /// encoded form either way.
    /// </para>
    /// </remarks>
    /// <param name="record">The row to write.</param>
    /// <param name="entityTag">The row's entity tag, or <see langword="null"/> when it has no version.</param>
    /// <param name="statusCode">The status to answer with.</param>
    /// <param name="created">
    /// The row a 201 is announcing, or <see langword="null"/> on any other status. Everything request-scoped
    /// about its <c>Location</c> — the <c>PathBase</c>, a route group's prefix — is resolved when the header is
    /// written rather than at the call site, because a route template carries neither and a create behind
    /// <c>UsePathBase</c>, behind a proxy that sets <c>X-Forwarded-Prefix</c>, or under a
    /// <c>MapGroup</c> would otherwise answer a URL that 404s (#121).
    /// </param>
    private sealed class RecordResult(
        AlvoRecord record, string? entityTag, int statusCode, CreatedRow? created) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            if (created is not null)
            {
                httpContext.Response.Headers.Location = created.Header(httpContext);
            }

            if (entityTag is not null)
            {
                httpContext.Response.Headers.ETag = entityTag;
            }

            return Results.Json(record.Values, DataApiJson.Options, statusCode: statusCode)
                .ExecuteAsync(httpContext);
        }
    }

    /// <summary>
    /// The <c>304</c> for a caller who already holds this version: the status, the tag, and no body at all.
    /// </summary>
    /// <remarks>
    /// The <c>ETag</c> is repeated because RFC 9110 §15.4.5 asks a <c>304</c> to carry the header fields that
    /// would have been sent in a <c>200</c> — a client that dropped its stored tag on a 304 would have to
    /// re-read the row to get one back, which is the round trip the <c>304</c> just saved.
    /// </remarks>
    /// <param name="entityTag">The tag of the representation the caller already holds.</param>
    private sealed class NotModifiedResult(string entityTag) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers.ETag = entityTag;
            httpContext.Response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }
    }
}
