using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
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
    /// <param name="formats">The applied descriptor's compiled field formats, shared by every endpoint.</param>
    internal static void Map(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string prefix,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        FormatCatalog formats)
    {
        var collection = $"{prefix}/{entity.Name}";
        var item = $"{collection}/{{id:guid}}";

        MapList(endpoints, entity, collection, options, filters);
        MapGet(endpoints, entity, item, filters);
        MapCreate(endpoints, entity, collection, options, filters, formats);
        MapUpdate(endpoints, entity, item, options, filters, formats);
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
                ProblemResultFactory.GuardAsync(async () =>
                {
                    var context = Caller(caller);
                    if (!QueryStringParser.TryParse(
                            http.Request.Query, entity, MaskedFields(policies, entity.Name, context), options,
                            out var request, out var violations))
                    {
                        return ProblemResultFactory.MalformedQuery(violations);
                    }

                    var page = await data.QueryAsync(request!.Query, context, ct).ConfigureAwait(false);
                    return Json(DataApiPage.From(page, request.Select));
                }))
            .Protect(entity, DataOperation.List, filters);

    private static void MapGet(
        IEndpointRouteBuilder endpoints, EntitySchema entity, string pattern, AlvoContextFilterFactory filters) =>
        endpoints.MapGet(pattern, (
                    Guid id,
                    HttpContext http,
                    IAlvoData data,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                ProblemResultFactory.GuardAsync(async () =>
                {
                    var record = await data.GetAsync(entity.Name, id, Caller(caller), ct).ConfigureAwait(false);

                    // A row the caller's policy excludes reads exactly like one that was never there, so
                    // this 404 is the same 404 AlvoRecordNotFoundException produces.
                    return record is null
                        ? ProblemResultFactory.NotFound()
                        : Representation(http.Request, record, entity);
                }))
            .Protect(entity, DataOperation.Get, filters);

    private static void MapCreate(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        FormatCatalog formats) =>
        endpoints.MapPost(pattern, (
                    HttpContext http,
                    IAlvoData data,
                    IPolicyEngine policies,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                ProblemResultFactory.GuardAsync(async () =>
                {
                    var context = Caller(caller);
                    var decision = EnsureOperationIsAllowed(policies, entity.Name, DataOperation.Create, context);
                    EnsureUnconditional(http.Request);

                    var (values, violations) = await ReadAndValidateAsync(
                        http, entity, options, decision, isCreate: true, formats, data, context, ct)
                        .ConfigureAwait(false);
                    if (violations.Count > 0)
                    {
                        return ProblemResultFactory.Validation(violations);
                    }

                    // Task 7: the Idempotency-Key header becomes the AlvoIdempotency token.
                    var record = await data.CreateAsync(entity.Name, values, context, null, ct).ConfigureAwait(false);
                    return Created($"{pattern}/{AssignedId(record)}", record, entity);
                }))
            .Protect(entity, DataOperation.Create, filters);

    private static void MapUpdate(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        FormatCatalog formats) =>
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
                    var decision = EnsureOperationIsAllowed(policies, entity.Name, DataOperation.Update, context);
                    var precondition = Precondition(http.Request);

                    var (values, violations) = await ReadAndValidateAsync(
                        http, entity, options, decision, isCreate: false, formats, data, context, ct)
                        .ConfigureAwait(false);
                    if (violations.Count > 0)
                    {
                        return ProblemResultFactory.Validation(violations);
                    }

                    var record = await data
                        .UpdateAsync(entity.Name, id, values, context, precondition, ct).ConfigureAwait(false);
                    return Row(record, entity);
                }))
            .Protect(entity, DataOperation.Update, filters);

    private static void MapDelete(
        IEndpointRouteBuilder endpoints, EntitySchema entity, string pattern, AlvoContextFilterFactory filters) =>
        endpoints.MapDelete(pattern, (
                    Guid id,
                    HttpContext http,
                    IAlvoData data,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                ProblemResultFactory.GuardAsync(async () =>
                {
                    var precondition = Precondition(http.Request);
                    await data.DeleteAsync(entity.Name, id, Caller(caller), precondition, ct).ConfigureAwait(false);
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
            // First, so it wraps the authorization filter and stamps the 401 and 403 that filter answers
            // with too — see NoStoreResponseFilter for why an uncacheable response is what pays for a
            // strong entity tag minted over a row version rather than over the response bytes.
            .AddEndpointFilter(NoStoreResponseFilter.Instance)
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
    /// <returns>
    /// The allow decision, whose <see cref="PolicyDecision.ReadOnlyFields"/> the validator needs. Returned
    /// rather than resolved a second time: the mask is a per-caller CEL result, and two resolutions of the
    /// same triple are two chances to validate against a mask the port will not apply.
    /// </returns>
    /// <exception cref="AlvoAuthorizationException">No policy allows this operation for this caller.</exception>
    private static PolicyDecision EnsureOperationIsAllowed(
        IPolicyEngine policies, string entity, DataOperation operation, AlvoContext context)
    {
        var decision = policies.Resolve(entity, operation, context);
        return decision.IsDenied ? throw new AlvoAuthorizationException(decision.DenyReason!) : decision;
    }

    /// <summary>
    /// Reads the request body and validates it against the entity's declared shape, returning the values the
    /// port would be called with plus <b>every</b> reason it must not be.
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
    private static async Task<(Dictionary<string, object?> Values, IReadOnlyList<AlvoViolation> Violations)>
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
            return (payload.Values, payload.Violations);
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

        return (payload.Values, [.. payload.Violations, .. validated]);
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
        EnsureNoIfNoneMatch(request);
        var header = request.Headers.IfMatch;
        if (header.Count == 0)
        {
            return null;
        }

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
    /// On a write, RFC 9110 §13.1.2 makes <c>If-None-Match</c> a precondition like any other — "act only if
    /// the row is <em>not</em> at this version" — and <see cref="AlvoPrecondition"/> expresses only the
    /// positive form. Passing the request through would be the silently-ignored precondition
    /// <see cref="AlvoPrecondition.EnsureSupported"/> exists to refuse, one header along: the caller sent a
    /// condition, read a <c>200</c>, and overwrote a concurrent writer anyway. So the refusal names the
    /// header the caller almost certainly meant.
    /// </remarks>
    /// <param name="request">The request to inspect.</param>
    /// <exception cref="AlvoPreconditionFailedException">The request carries <c>If-None-Match</c>.</exception>
    private static void EnsureNoIfNoneMatch(HttpRequest request)
    {
        if (request.Headers.IfNoneMatch.Count > 0)
        {
            throw new AlvoPreconditionFailedException(
                "'If-None-Match' is evaluated on a read and cannot condition a write here: this API compares a "
                + "record against the version a caller holds, never against a version it must not have. Send "
                + "'If-Match' with the 'ETag' a previous response returned, or send the write with neither.");
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
    /// <b>Which headers are refused and which are ignored is split by what a wrong answer costs.</b> On a
    /// write, ignoring one lets a caller believe they conditioned a change they did not, so every write verb
    /// either evaluates the header or refuses it — this method for the create, <see cref="Precondition"/> and
    /// <see cref="EnsureNoIfNoneMatch"/> for the update and delete. On a read the worst outcome is a response
    /// body the caller said they already had, so a read ignores what it cannot use: <c>If-Match</c> is not
    /// honoured on a read at all today, and neither header is honoured on a list, which has no version of its
    /// own. Both are gaps in coverage rather than in safety.
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
    /// have different comparison functions in the spec, and the reason the asymmetry is safe is the same
    /// reason <see cref="IsAlreadyHeld"/> refuses nothing: the worst a wrong answer here costs is a response
    /// body the caller already had.
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
            : new RecordResult(record, entityTag, StatusCodes.Status200OK, location: null);
    }

    /// <summary>Whether the caller's <c>If-None-Match</c> covers <paramref name="entityTag"/>.</summary>
    /// <remarks>
    /// A header this API cannot parse means "no match" rather than a refusal, and the asymmetry with
    /// <c>If-Match</c> is deliberate: failing to honour <c>If-None-Match</c> costs the caller a response body
    /// they already had, while failing to honour <c>If-Match</c> costs someone their write.
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
        new RecordResult(record, RowVersionETag.For(record, entity), StatusCodes.Status200OK, location: null);

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
    /// <param name="record">The created row.</param>
    /// <param name="entity">The entity as the applied schema declares it.</param>
    private static RecordResult Created(string location, AlvoRecord record, EntitySchema entity) =>
        new RecordResult(record, RowVersionETag.For(record, entity), StatusCodes.Status201Created, location);

    /// <summary>
    /// One row on the wire: its <c>ETag</c>, its <c>Location</c> on a create, and its values under
    /// <see cref="DataApiJson"/>'s options.
    /// </summary>
    /// <remarks>
    /// <b>The <c>ETag</c> is set here rather than at each call site</b>, so a row cannot be written without
    /// one: every 200 and 201 of a row goes through this type, and the only way to omit the header is for
    /// <see cref="RowVersionETag.For"/> to answer that the row has no version to tag. A create is included
    /// because the write already re-read the row, so a 201 has a stored version exactly like a 200 does —
    /// and a caller who had to issue a GET before their first conditional write would race the very window
    /// the tag closes.
    /// </remarks>
    /// <param name="record">The row to write.</param>
    /// <param name="entityTag">The row's entity tag, or <see langword="null"/> when it has no version.</param>
    /// <param name="statusCode">The status to answer with.</param>
    /// <param name="location">The created row's path, on a 201; <see langword="null"/> otherwise.</param>
    private sealed class RecordResult(
        AlvoRecord record, string? entityTag, int statusCode, string? location) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            if (location is not null)
            {
                httpContext.Response.Headers.Location = location;
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
