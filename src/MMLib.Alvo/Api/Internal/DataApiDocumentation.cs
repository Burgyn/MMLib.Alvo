using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// <b>The one authority on the prose and the status catalogue the generated OpenAPI document publishes.</b>
/// Every sentence a caller reads about a generated endpoint — what it does, which headers it honours, and
/// which of them it deliberately does not — is written here and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists because a code remark is not a contract.</b> Four behaviours of this feature are
/// client-observable and unguessable, and every one of them was deferred to this task with a note saying so:
/// <c>If-Match</c> is ignored on a read and neither precondition header is honoured on a list
/// (<see cref="DataApiEndpoints"/>' <c>Representation</c>); a create carrying either one is refused with 412
/// (<c>EnsureUnconditional</c>); <c>Idempotency-Key</c> is honoured on a create and ignored on an update and
/// a delete (<c>IdempotencyKeyHeader</c>); and where a <see langword="null"/> sorts on a nullable sort key,
/// which is a choice the caller makes and the server never guesses (<c>SortSqlRenderer</c>,
/// <c>KeysetSqlRenderer</c>). An integrator reads none of those files. §0 principle 4 makes the published document the contract an agent reads, so this is where
/// they belong.
/// </para>
/// <para>
/// <b>The status catalogue lives here too, beside the prose, and is read by both sides.</b>
/// <see cref="DataApiEndpoints"/> attaches it as endpoint metadata — so ApiExplorer, and therefore the
/// document, lists exactly these statuses — and <see cref="AlvoDocumentTransformer"/> reads the same table to
/// describe each one and attach its body and headers. Two hand-written lists would be how the document comes
/// to advertise a status no endpoint answers with, which is the same defect as an unreachable entry in
/// <see cref="AlvoProblemTypes"/>.
/// </para>
/// <para>
/// <b>What the catalogue deliberately omits is a 500.</b> <c>IAlvoData</c>'s fifth failure family propagates
/// past <see cref="ProblemResultFactory"/> untouched, so the response a caller receives is composed by the
/// <em>host</em>, in whatever shape its own exception handling produces — Alvo cannot describe a body it does
/// not write. That is the same reasoning that keeps a 500 slug out of the problem-type catalogue.
/// </para>
/// </remarks>
internal static class DataApiDocumentation
{
    /// <summary>What a response body carries, so one table answers both the schema and the media type.</summary>
    internal enum ResponseBody
    {
        /// <summary>No body at all — a 204 or a 304.</summary>
        None,

        /// <summary>One row of the entity, as the read schema declares it.</summary>
        Row,

        /// <summary>The <c>{ items, next }</c> page envelope.</summary>
        Page,

        /// <summary>An RFC 9457 problem document.</summary>
        Problem,
    }

    /// <summary>One status a generated endpoint can answer with, and what it means when it does.</summary>
    /// <param name="Status">The HTTP status code.</param>
    /// <param name="Body">What the response carries.</param>
    /// <param name="Description">What this status means on this operation, for the published document.</param>
    /// <param name="SharedId">
    /// The <c>components.responses</c> id this response is published under, or <see langword="null"/> when it
    /// is inlined per operation.
    /// <para>
    /// Every refusal has one and every success has none, and the split is not arbitrary: a refusal's shape and
    /// wording are identical on every route — <see cref="ProblemResultFactory"/> is the only writer — whereas a
    /// success carries that entity's own schema and, on an audited entity only, an <c>ETag</c>. Inlining the
    /// refusals instead cost 60% of the document's bytes in six sentences repeated per operation, which is the
    /// difference between a baseline a reviewer reads and one they scroll past.
    /// </para>
    /// </param>
    /// <param name="SharedNarrowing">
    /// Prose that replaces <paramref name="Description"/> on <em>this</em> operation only, while the response
    /// stays the one shared component — or <see langword="null"/> when the shared wording is the whole truth.
    /// <para>
    /// It exists for one case: a version-less entity's 412 can only ever mean "a precondition was supplied that
    /// this entity cannot answer", never "the version did not match", and a caller who is told both is told to
    /// consider a comparison that cannot happen. OpenAPI 3.1's Reference Object takes a sibling
    /// <c>description</c> that "SHOULD override that of the referenced component", so the narrowing costs one
    /// line beside the <c>$ref</c> rather than an inlined copy of the whole response — which is what keeps
    /// <paramref name="SharedId"/>'s bargain intact for the other thirty-nine refusals.
    /// </para>
    /// </param>
    internal sealed record Response(
        int Status,
        ResponseBody Body,
        string Description,
        string? SharedId = null,
        string? SharedNarrowing = null);

    /// <summary>
    /// Every status <paramref name="operation"/> on <paramref name="entity"/> can actually answer with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Each entry is a claim that a request can reach it</b>, and
    /// <c>OpenApiDocumentTests.Every_documented_status_code_is_one_the_endpoint_can_actually_return</c> drives
    /// one such request per entry. The three omissions are as deliberate as the entries:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///   <b>No 404 on a list or a create.</b> Both address a collection whose route literal came from the
    ///   applied schema, so "no such thing" is answered by routing before either delegate runs — and a
    ///   404 in the document would tell a caller to check an id they never sent.
    ///   </item>
    ///   <item>
    ///   <b>No 422 on a read, a delete, or any operation that parses nothing.</b> A single-row read and a
    ///   delete take an <c>id</c> routing already constrained to a GUID, read no body, and parse no query
    ///   string — so the malformed-request channel is unreachable from them.
    ///   </item>
    ///   <item>
    ///   <b>No 409 on a read, and no 412 on one either.</b> A 409 is a write colliding with stored state — a
    ///   reused idempotency key on a create, a <c>unique</c> value another record holds on a create or an
    ///   update, a <c>restrict</c>-ed reference on a delete — and none of the three is reachable from a read,
    ///   which changes nothing. The read side ignores <c>If-Match</c> rather than refusing it (see
    ///   <see cref="ReadOne"/>), so it has no 412 either.
    ///   </item>
    /// </list>
    /// <para>
    /// <b>A 304 is listed only for an entity that has a row version</b>, because
    /// <see cref="AlvoManagedColumns.VersionColumn"/> answering <see langword="null"/> means no <c>ETag</c> is
    /// ever minted, so <c>If-None-Match</c> can never match and the status is unreachable for that entity. A
    /// document listing it anyway would describe a behaviour that does not exist.
    /// </para>
    /// </remarks>
    /// <param name="operation">The operation the endpoint performs.</param>
    /// <param name="entity">The entity it serves, consulted for whether a row of it can be versioned.</param>
    internal static IReadOnlyList<Response> ResponsesFor(DataOperation operation, EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return operation switch
        {
            DataOperation.List =>
                [Ok(ResponseBody.Page, "A page of rows the caller's policy admits."), .. Refusals(Malformed)],
            DataOperation.Get =>
                [Ok(ResponseBody.Row, "The row."), .. NotModified(entity), .. Refusals(Absent)],
            DataOperation.Create =>
                [Created(), .. Refusals(Malformed, Precondition, Conflict)],
            DataOperation.Update =>
                [Ok(ResponseBody.Row, "The row as it now stands."),
                 .. Refusals(Malformed, Absent, PreconditionOn(entity), Conflict)],
            DataOperation.Delete =>
                [NoContent(), .. Refusals(Absent, PreconditionOn(entity), Conflict)],
            _ => throw new InvalidOperationException($"No response catalogue for operation '{operation}'."),
        };
    }

    /// <summary>The two refusals <em>every</em> generated endpoint can answer with, plus the ones it can.</summary>
    /// <remarks>
    /// The 401 and the 403 are unconditional because <c>DataApiEndpoints.Protect</c> attaches the same gate to
    /// every route: a presented credential that cannot be used is a 401 there, and a key whose scopes exclude
    /// the operation — or a policy that admits nobody — is a 403. Listing them per operation instead would be
    /// five chances to forget one.
    /// </remarks>
    /// <param name="others">The refusals this particular operation adds.</param>
    private static IEnumerable<Response> Refusals(params Response[] others) => [Unauthenticated, Forbidden, .. others];

    /// <summary>
    /// Every refusal, once — the set published as <c>components.responses</c> and referenced from each
    /// operation.
    /// </summary>
    /// <remarks>
    /// It is the same six records <see cref="ResponsesFor"/> hands out, so a refusal cannot be published under
    /// one wording and referenced under another. A reviewer reads each sentence here exactly once.
    /// </remarks>
    internal static IReadOnlyList<Response> SharedRefusals { get; } =
        [Unauthenticated, Forbidden, Absent, Precondition, Conflict, Malformed];

    private static Response Ok(ResponseBody body, string description) =>
        new(StatusCodes.Status200OK, body, description);

    private static Response Created() => new(
        StatusCodes.Status201Created,
        ResponseBody.Row,
        "The created row. 'Location' names it, and 'ETag' carries the version a later conditional write may "
        + "send as 'If-Match' — so a first conditional write needs no read of its own.");

    private static Response NoContent() => new(
        StatusCodes.Status204NoContent, ResponseBody.None, "The row was deleted. No body.");

    /// <summary>The 304, for an entity whose rows carry a version — and nothing at all for one whose do not.</summary>
    private static IEnumerable<Response> NotModified(EntitySchema entity) =>
        AlvoManagedColumns.VersionColumn(entity) is null
            ? []
            : [new(
                StatusCodes.Status304NotModified,
                ResponseBody.None,
                "The caller's 'If-None-Match' covers the current version, so the body is omitted. The 'ETag' is "
                + "repeated (RFC 9110 §15.4.5) so a client need not re-read the row to get its tag back.")];

    private static Response Unauthenticated => new(
        StatusCodes.Status401Unauthorized,
        ResponseBody.Problem,
        "A credential was presented and cannot be used — unknown, revoked, expired, malformed, or issued for "
        + "another tenant. One wording for all of them, so key ids cannot be enumerated one request at a time. "
        + "'WWW-Authenticate' names the scheme and the header to send. Presenting no credential at all is not "
        + "a 401: an anonymous caller is judged by policy like any other.",
        SharedId: "unauthenticated");

    private static Response Forbidden => new(
        StatusCodes.Status403Forbidden,
        ResponseBody.Problem,
        "The operation is refused. Two kinds, told apart by the problem 'type' and never by its prose: "
        + "'out-of-scope' means the presented key's scopes do not cover this entity and operation (grant the "
        + "key the scope), and 'forbidden' means policy refused it (change a rule). A policy refusal here is "
        + "an operation-level one — the operation is unconfigured, the entity is unknown to the applied "
        + "descriptor, the caller has no tenant on a tenant-scoped entity, or the policy reads a caller value "
        + "this caller does not carry. It is never 'your rule excluded these rows'; see the 200.",
        SharedId: "forbidden");

    private static Response Absent => new(
        StatusCodes.Status404NotFound,
        ResponseBody.Problem,
        "No such row — or one the caller's policy excludes. The two are indistinguishable on purpose, in the "
        + "problem 'type' as much as in the prose, so a 404 cannot be used to prove a row exists.",
        SharedId: "notFound");

    private static Response Precondition => new(
        StatusCodes.Status412PreconditionFailed,
        ResponseBody.Problem,
        "A precondition this API cannot evaluate, or one that did not hold. A header it cannot compare is "
        + "refused rather than ignored: ignoring it would be the lost update 'If-Match' exists to prevent, "
        + "and the caller would read the success as proof it did not happen.",
        SharedId: "preconditionFailed");

    /// <summary>
    /// The 412 as a write of <paramref name="entity"/> can actually mean it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The status stays on a version-less entity; only the sentence narrows.</b> It really is reachable
    /// there — an <c>If-Match</c> naming a version and any <c>If-None-Match</c> are both refused — so removing
    /// the entry would document a behaviour the endpoint has. But of the shared wording's two arms only the
    /// first can fire: there is no stored version, so "one that did not hold" describes a comparison this
    /// entity cannot perform, and a caller resolving a 412 the usual way (re-read the <c>ETag</c>, retry) would
    /// be chasing a tag no response ever carries.
    /// </para>
    /// <para>
    /// The read side needs no such narrowing: <see cref="ResponsesFor"/> lists no 412 on a read at all, because
    /// the read side ignores <c>If-Match</c> rather than refusing it.
    /// </para>
    /// </remarks>
    /// <param name="entity">The entity, consulted for whether a row of it can be versioned.</param>
    private static Response PreconditionOn(EntitySchema entity) =>
        AlvoManagedColumns.VersionColumn(entity) is null
            ? Precondition with
            {
                SharedNarrowing =
                    "A precondition was supplied that this entity cannot answer. Its rows carry no version — "
                    + "'audit: true' is what mints one — so an 'If-Match' naming a version, and any "
                    + "'If-None-Match', are refused here rather than ignored: there is nothing stored for "
                    + "either to be compared against. On this entity the status never means 'the version did "
                    + "not match', because no comparison is possible. 'If-Match: *' is accepted and is not "
                    + "this refusal.",
            }
            : Precondition;

    /// <summary>
    /// The 409, covering both of its kinds — the shape <see cref="Forbidden"/> already uses for its two, and
    /// the only shape available: OpenAPI keys a response by status, so one sentence has to describe every way
    /// an operation can answer with it, and the problem <c>type</c> is what tells them apart.
    /// </summary>
    private static Response Conflict => new(
        StatusCodes.Status409Conflict,
        ResponseBody.Problem,
        "The request conflicts with what is already stored. Two kinds, told apart by the problem 'type': "
        + "'idempotency-conflict' means the 'Idempotency-Key' was already used by this caller for a request "
        + "with a different body (retry with the same key and the same body to replay the first result, or "
        + "send a fresh key); 'conflict' means a constraint the database enforces refused the write — a value "
        + "another record already holds on a field declared unique, or a delete another record still "
        + "references through a 'ref' declaring onDelete: restrict. The 'violations' array names the field "
        + "for the first of those and carries a fix suggestion for both.",
        SharedId: "conflict");

    private static Response Malformed => new(
        StatusCodes.Status422UnprocessableEntity,
        ResponseBody.Problem,
        "The request could not be acted on: a query string or body that is malformed, a body the entity's "
        + "declared shape refuses, or a header this API cannot honour. The 'violations' array carries every "
        + "reason at once — a pointer, a stable code and a fix suggestion each — never only the first, so one "
        + "round trip is enough to repair the request.",
        SharedId: "unprocessable");

    /// <summary>The one-line summary of an operation, as the document's <c>summary</c>.</summary>
    /// <remarks>
    /// The entity name is used verbatim rather than singularised. A descriptor's entity names are the author's
    /// (<c>owners</c>, <c>inspections</c>), and guessing a singular form would invent a word the descriptor
    /// does not contain — which is exactly what an agent then cannot map back to anything.
    /// </remarks>
    /// <param name="operation">The operation.</param>
    /// <param name="entity">The entity name, as the applied schema declares it.</param>
    internal static string SummaryOf(DataOperation operation, string entity) => operation switch
    {
        DataOperation.List => $"List '{entity}' rows",
        DataOperation.Get => $"Read one '{entity}' row",
        DataOperation.Create => $"Create one '{entity}' row",
        DataOperation.Update => $"Update one '{entity}' row",
        DataOperation.Delete => $"Delete one '{entity}' row",
        _ => throw new InvalidOperationException($"No summary for operation '{operation}'."),
    };

    /// <summary>
    /// The operation's own <c>description</c>: what it does, and every header behaviour a caller cannot infer.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="entity">The entity it serves, consulted for whether a row of it can be versioned.</param>
    internal static string DescriptionOf(DataOperation operation, EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return operation switch
        {
            DataOperation.List => List,
            DataOperation.Get => ReadOne(entity),
            DataOperation.Create => Create,
            DataOperation.Update => Update(entity),
            DataOperation.Delete => Delete(entity),
            _ => throw new InvalidOperationException($"No description for operation '{operation}'."),
        };
    }

    /// <summary>
    /// The list operation's prose, carrying two of the four gaps this type exists for — preconditions on a
    /// list, and where nulls sort on a nullable key — plus the 200-not-403 behaviour a reader otherwise
    /// misreads.
    /// </summary>
    private static string List =>
        "Reads a page of rows the caller's policy admits.\n\n"
        + Grammar + "\n\n"
        + "The response is an envelope — `{ \"items\": [ … ], \"next\": <cursor or null> }` — and never a bare "
        + "array. `next` is the cursor for the page after this one, and it is the *only* place that cursor "
        + "appears: there is deliberately no `Link` or `Content-Range` header, so an agent reading the body "
        + "never has to parse HTTP headers to keep paging.\n\n"
        + "**A caller whose rule excludes every row is answered 200 with an empty page, not 403.** A rule "
        + "compiles to a row-level `USING` predicate, so a caller who fails it receives an *allow* carrying a "
        + "predicate that matches nothing. A 403 here means something else entirely: the operation is "
        + "unconfigured for this entity, the entity is unknown to the applied descriptor, the caller has no "
        + "tenant on a tenant-scoped entity, or the policy reads a caller value this caller does not carry.\n\n"
        + "**Neither precondition header is honoured on a list.** A page has no version of its own to compare, "
        + "so `If-Match` and `If-None-Match` are ignored here — not refused, as they would be on a write. "
        + "Condition a single row's read or write instead.\n\n"
        + "**A nullable field is a sort key like any other, and `nullslast` is what it gets if you do not say "
        + "otherwise.** Where a null sorts is never left to the database: SQLite and PostgreSQL disagree on "
        + "the default for a given direction, so the placement is always explicit in the statement Alvo emits "
        + "and `nullsfirst`/`nullslast` are how you change it. Paging honours the same placement, so a cursor "
        + "walks the null-keyed rows too — which was not true before: such a read used to be refused with 422 "
        + "rather than answered, because a keyset boundary that compared the value alone dropped rows "
        + "silently.\n\n"
        + "**Sorting by a nullable field costs more than sorting by a required one.** The null placement is "
        + "emitted as a `CASE` expression over the key, which an index on that key cannot serve. Page by a "
        + "required column where latency matters.";

    /// <summary>
    /// The filter, sort and paging grammar, stated once on the list operation rather than repeated on each of
    /// the per-field query parameters — which would be the same paragraph N times over in one document.
    /// </summary>
    /// <remarks>
    /// The operator list is read from <see cref="FilterOperators.AsList"/> and the reserved parameter names
    /// from <see cref="ReservedQueryKeys.AsList"/>, so the published grammar cannot describe a spelling the
    /// parser does not accept. Both are derived from the port's own enum and the parser's own table; a
    /// hand-written list here is how a document comes to advertise a dialect.
    /// </remarks>
    private static string Grammar =>
        "**Filtering** follows PostgREST's spelling: every query parameter that is not one of the reserved "
        + $"names ({ReservedQueryKeys.AsList}) names a field, and its value is `<operator>.<operand>` — "
        + $"`?year=gte.2020`. The operators are {FilterOperators.AsList}. `in` takes a bracketed candidate "
        + "list (`in.(skoda,vw)`) and `is` takes exactly `null`, `true` or `false`; `like`/`ilike` take a "
        + "pattern. An unrecognised operator is refused with 422, never quietly read as equality, and an "
        + "ordering operator applied to a type this API defines no total order over is refused too — so one "
        + "filter means the same thing on every engine.\n\n"
        + "Several parameters are one **conjunction** (AND), which is the only reading that keeps a filter "
        + "narrowing as terms are added. `or=(…)` and `and=(…)` group terms explicitly and may nest, and "
        + "prefixing any parameter name or group keyword with `not.` negates it (`not.color=eq.red`, "
        + "`not.or=(…)`).\n\n"
        + "**A field that is unavailable to the caller is refused exactly like one that does not exist**, in "
        + "`filter`, `order` and `select` alike — the refusal names the parameter's role and never the field, "
        + "so filtering, ordering or selecting cannot be used to discover a hidden field's name. The one "
        + "exception is a field the descriptor also marks `required`: its name is published in the write "
        + "schemas below, because a mandatory field a caller cannot see could not be supplied — see the "
        + "overview.";

    /// <summary>
    /// The single-row read's prose. The <c>If-None-Match</c> paragraph is conditional because a
    /// <em>version-less</em> entity mints no <c>ETag</c>, so promising a 304 for it would be a lie.
    /// </summary>
    /// <param name="entity">The entity, consulted for whether a row of it can be versioned.</param>
    private static string ReadOne(EntitySchema entity) =>
        "Reads one row by id.\n\n"
        + "A row the caller's policy excludes reads exactly like one that was never there: 404, with the same "
        + "problem `type` and the same prose, so the status cannot be used to prove a row exists.\n\n"
        + (AlvoManagedColumns.VersionColumn(entity) is null
            ? "**This entity's rows carry no version, so no `ETag` is returned** and no conditional request "
            + "against one is possible. A row version comes from `audit: true` in the descriptor; without it "
            + "there is no column the framework writes on every change, and a version a caller can rewrite is "
            + "not a version. `If-None-Match` therefore never matches here, and `If-Match` is ignored.\n\n"
            : "`If-None-Match` **is** honoured: a caller who already holds the current version is answered 304 "
            + "with the `ETag` and no body. Comparison is RFC 9110 §13.1.2's *weak* one, so a `W/` prefix is "
            + "ignored — deliberately not the strong comparison `If-Match` gets on a write.\n\n")
        + "**`If-Match` is ignored on a read.** On a `GET`, RFC 9110 §13.1.1 means \"send the body only if it is "
        + "still this version\", which saves a caller nothing they cannot get by comparing the `ETag` they were "
        + "sent — so the header is neither honoured nor refused here. That is the read side's asymmetry with the "
        + "write side, and it is safe for one reason: an unhonoured precondition on a read costs a response body "
        + "the caller said they already had, where on a write it would cost somebody their change.";

    private const string Create =
        "Creates one row and returns it, with `Location` naming the new row and `ETag` carrying its version.\n\n"
        + "The row's `id` is assigned by the store and must not be sent. Framework-managed columns are the "
        + "framework's to write and are refused in a payload — with the one exception of `tenant_id` on a "
        + "tenant-scoped entity, which legitimately places the new row in a tenant and is then judged by the "
        + "tenant scope. A key the entity does not declare is refused with 422 rather than ignored.\n\n"
        + "**`Idempotency-Key` makes the create retry-safe.** The first request is performed and its result "
        + "recorded against the key *and the caller's own scope*; a retry carrying the same key and the same "
        + "body is answered with the first result and writes no second row, and the same key with a different "
        + "body is 409. An anonymous caller's key is refused (422): a record's identity is the key plus the "
        + "caller, and every anonymous caller shares one reserved identity, so their keys would share one "
        + "space.\n\n"
        + "**If your role may create this entity but not read it back, the retry is still 201, never 403.** The "
        + "body then carries only `id` — the same id your original `201` and its `Location` already gave you — "
        + "rather than a re-read of a row your role cannot see. A retry must not be worse than the create it "
        + "replays.\n\n"
        + "**A create carrying `If-Match` or `If-None-Match` is refused with 412.** There is no stored record "
        + "for a version to be compared against and this API assigns the new row's id itself, so neither header "
        + "can be evaluated — and the rule this feature is built on is that a precondition which cannot be "
        + "evaluated is refused rather than ignored, because a 201 would tell a caller their condition held. "
        + "Note what this means for a client that blanket-attaches `If-Match` to every mutating request, as "
        + "several SDKs do: it will be refused here. There is no `PUT`, so create-if-absent was never on offer "
        + "in the first place. Send the create unconditionally, and `If-Match` on the update that follows it.";

    /// <summary>
    /// The partial update's prose. The precondition paragraph is conditional for the reason
    /// <see cref="ReadOne"/>'s is: a <em>version-less</em> entity mints no <c>ETag</c>, so telling a caller to
    /// send one back as <c>If-Match</c> would be an instruction into a permanent 412.
    /// </summary>
    /// <param name="entity">The entity, consulted for whether a row of it can be versioned.</param>
    private static string Update(EntitySchema entity) =>
        "Partially updates one row and returns it. A field the body does not mention keeps its stored value — "
        + "which is why this is a `PATCH` and there is no `PUT`: the underlying update is partial by contract, "
        + "so a `PUT` would advertise whole-resource replacement that never happens.\n\n"
        + "A write to a read-only field is refused with 422 rather than silently dropped, and so is a key the "
        + "entity does not declare. `id` and the framework-managed columns can never be rewritten, `tenant_id` "
        + "included: a row does not move between tenants.\n\n"
        + UpdateConditioning(entity)
        + "**`Idempotency-Key` is accepted and ignored here — a known limitation, and this is what it costs.** "
        + "The row's end state is unaffected: an update assigns *absolute* values to named fields, so applying "
        + "it twice leaves exactly the state applying it once leaves, and there is no duplicate row to prevent. "
        + "The *outcome you observe* is another matter. "
        + UpdateRetry(entity)
        + " Refusing the header instead would break the widespread client habit of attaching it to every "
        + "mutating request and would reject requests that are otherwise fine, so it is accepted — and declared "
        + "here rather than left to be discovered.";

    /// <summary>
    /// The delete's prose, conditional on the same trait <see cref="Update"/> is and for the same reason.
    /// </summary>
    /// <param name="entity">The entity, consulted for whether a row of it can be versioned.</param>
    private static string Delete(EntitySchema entity) =>
        "Deletes one row and returns no body. A row the caller's policy excludes is 404, exactly as an absent "
        + "one is.\n\n"
        + DeleteConditioning(entity)
        + "**`Idempotency-Key` is accepted and ignored here — the same known limitation as on the update.** "
        + "Removing one row twice leaves the same state as removing it once, so nothing is duplicated; but a "
        + "retry after a lost `204` is a "
        + (AlvoManagedColumns.VersionColumn(entity) is null
            ? "**404 you cannot tell apart from somebody else's delete**, "
            : "**404 (or a 412) you cannot tell apart from somebody else's delete**, ")
        + "which is precisely the question a key would have answered. Read the row back rather than treating the "
        + "second answer as evidence the first attempt did not land.";

    /// <summary>
    /// How an update of this entity can be conditioned — or that it cannot be.
    /// </summary>
    /// <remarks>
    /// This is the paragraph the <c>ifMatch</c> parameter's absence has to be explained by. A version-less
    /// entity publishes no such parameter (see <c>DataApiParameters.HeaderNames</c>), and a document that
    /// simply omitted it without saying so would leave a caller to conclude the header was overlooked.
    /// </remarks>
    /// <param name="entity">The entity, consulted for whether a row of it can be versioned.</param>
    private static string UpdateConditioning(EntitySchema entity) =>
        AlvoManagedColumns.VersionColumn(entity) is null
            ? Unconditionable
            : "**`If-Match` conditions the write on the row's current version**, compared with RFC 9110 "
            + "§13.1.1's *strong* comparison. Send back one `ETag` exactly as a previous response returned it, "
            + "or `*` to require only that the row still exist. Anything this API cannot turn into exactly one "
            + "version it minted — several tags, a weak `W/` tag, an opaque value it never issued — is 412, "
            + "never ignored. `If-None-Match` is refused with 412 as well: this API compares a row against the "
            + "version a caller *holds*, and has no channel for \"act only if the row is not at this version\". "
            + "That is a labelled deviation from RFC 9110 §13.1.2, which would let a non-matching "
            + "`If-None-Match` simply succeed — Alvo cannot evaluate the header at all, so a conforming success "
            + "would be indistinguishable from a precondition that was never checked.\n\n";

    /// <summary>How a delete of this entity can be conditioned — or that it cannot be.</summary>
    /// <param name="entity">The entity, consulted for whether a row of it can be versioned.</param>
    private static string DeleteConditioning(EntitySchema entity) =>
        AlvoManagedColumns.VersionColumn(entity) is null
            ? Unconditionable
            : "**`If-Match` conditions the delete** on the row's current version, and `If-None-Match` is "
            + "refused with 412 — both exactly as on the update, including the wording of what cannot be "
            + "compared.\n\n";

    /// <summary>
    /// The version-less arm of both writes: this entity cannot be conditioned at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It states exactly which headers are refused and which one is not, because the difference is real.</b>
    /// <c>DataApiEndpoints.IfMatch</c> reads <c>*</c> as "no version named" and hands the port
    /// <see langword="null"/>, which <c>AlvoPrecondition.EnsureSupported</c> lets through — so <c>*</c> is
    /// accepted on a version-less entity, and it buys nothing there or anywhere else, because "the row still
    /// exists" is what a 404 already answers. Claiming every precondition is refused would have been the
    /// mirror image of the defect this text exists to fix: a document stating a refusal that does not happen.
    /// </para>
    /// <para>
    /// The fix is named. An author reading "cannot be conditioned" needs to know the cause is one missing
    /// descriptor flag, or they will look for the header they sent wrong.
    /// </para>
    /// </remarks>
    private const string Unconditionable =
        "**This entity's rows carry no version, so this write cannot be conditioned.** A row version comes from "
        + "`audit: true` in the descriptor; without it there is no column the framework writes on every change, "
        + "and a version a caller can rewrite is not a version. No `ETag` is ever returned for a row of this "
        + "entity, so there is no tag a caller could ever send back — and an `If-Match` naming a version is "
        + "therefore **refused with 412**, never ignored, because a success would tell a caller their condition "
        + "held when nothing was compared. `If-None-Match` is refused with 412 here as it is on every write. "
        + "The one precondition that is accepted is `If-Match: *`, and it changes nothing: it asks only that "
        + "the row still exist, which an absent row already answers with 404. Neither header is offered as a "
        + "parameter on this operation for that reason — a parameter is an invitation to send a value, and "
        + "there is no value to send. Note what this means for a client that blanket-attaches `If-Match` to "
        + "every mutating request, as several SDKs do: unless it sends `*`, it is refused here. Add "
        + "`audit: true` to the entity to make a conditional write possible.\n\n";

    /// <summary>
    /// What a retried update after a lost response actually looks like — which depends on whether the entity
    /// has a version, because the whole scenario is about a stale one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The versioned arm's advice is "`If-Match` plus a re-read", and on a version-less entity that would be
    /// the same instruction into a permanent 412 the parameter guard removed one paragraph above. The
    /// version-less arm keeps the actionable half — read the row back — and names what the entity forfeits.
    /// </para>
    /// <para>
    /// Each arm is one sentence run of the surrounding paragraph rather than a paragraph of its own, so an
    /// audited entity's published prose is byte-for-byte what it was before this became conditional. A
    /// baseline that also moved for <c>products</c> would make a reviewer diff two paragraphs to find out that
    /// only <c>categories</c> changed.
    /// </para>
    /// </remarks>
    /// <param name="entity">The entity, consulted for whether a row of it can be versioned.</param>
    private static string UpdateRetry(EntitySchema entity) =>
        AlvoManagedColumns.VersionColumn(entity) is null
            ? "If the 200 is lost to a dropped connection and you retry the identical request, the retry "
            + "assigns the same absolute values again and is answered 200 — so unlike an audited entity, there "
            + "is no 412 to misread. What you cannot learn is whether anybody changed the row between your two "
            + "attempts: this entity keeps no version, so the retry overwrites a concurrent change exactly as "
            + "the first attempt would have, and silently. **Read the row back and compare it with what you "
            + "sent** before treating the write as settled. That is the whole of retry safety on this verb "
            + "here; `audit: true` on the entity is what buys the rest."
            : "If you send `PATCH … If-Match: \"v1\"`, the 200 is lost to a dropped connection, and you retry "
            + "the identical request, the write has landed and the row is at `v2` — so the retry is **412, and "
            + "you cannot tell it apart from someone else having changed the row**. Resolving that 412 the "
            + "usual way (re-read, re-merge, re-apply) would clobber a genuinely concurrent change if it *was* "
            + "someone else. A key would have told you it was your own write. So retry safety on this verb is "
            + "`If-Match` plus a re-read, not a key: after a lost response, **read the row back and compare it "
            + "with what you sent** before deciding the write did not land.";

    /// <summary>The document-level prose: what this API is, and the invariants that hold on every route.</summary>
    /// <remarks>
    /// Only the facts that are genuinely uniform go here. A behaviour that varies by operation — which
    /// precondition header is honoured, which status is reachable — belongs on the operation, because a caller
    /// reading one endpoint's entry must not have to reconstruct it from a preamble.
    /// </remarks>
    internal const string Overview =
        "Every route here is generated from the applied Alvo descriptor: an entity declared in the descriptor "
        + "is the explicit decision to expose it, and a field marked `hidden` is the per-field opt-out. It "
        + "appears in no response schema, and in a request schema only when the descriptor also marks it "
        + "`required` — a mandatory field a caller cannot see could not be supplied at all, so the name is "
        + "published only where a caller must read it to perform the write.\n\n"
        + "**Default-deny.** Nothing is reachable without a policy that admits the caller for that entity and "
        + "operation. An operation the descriptor configures no rule for is refused for everybody.\n\n"
        + "**Refusals are RFC 9457 problem documents** (`application/problem+json`) whose `type` classifies the "
        + "refusal under `" + AlvoProblemTypes.BaseUri + "`. Branch on `type`; `detail` is prose and, per RFC "
        + "9457 §3.1.1, ought not be parsed. A refusal carries every reason at once in `violations`, each with "
        + "a JSON Pointer, a stable code and a fix suggestion.\n\n"
        + "**Every response is `Cache-Control: no-store`**, refusals included. These representations are "
        + "policy-masked per caller, and the `ETag` is minted over the row's *version* rather than over the "
        + "response bytes — which is the only tag `If-Match`'s strong comparison could ever match, and the "
        + "reason no intermediary may keep the body.\n\n"
        + "**A 500 is not documented on any operation, and its body depends on the host.** An invariant the "
        + "implementation itself relies on propagates past Alvo's endpoints untouched, so what a 500 looks "
        + "like is the host's decision and not a promise this document can make. A host that opted in "
        + "(`AddAlvoProblemDetails()`) answers with the same problem document as every other refusal, under "
        + "`" + AlvoProblemTypes.BaseUri + AlvoProblemTypes.Internal + "` — which is why that value is in "
        + "`type`'s list. A host that did not composes its own.\n\n"
        + "**A request the web server would not read is not documented on any operation either.** A body over "
        + "the server's limit, one that arrived too slowly, or one whose framing broke never reaches the "
        + "operation, so no operation can promise a status for it. A host that opted in answers it under `"
        + AlvoProblemTypes.BaseUri + AlvoProblemTypes.UnreadableRequest + "`, at the status the server chose "
        + "(413, 408 or 400) — the second value in `type`'s list that no operation lists.";
}
