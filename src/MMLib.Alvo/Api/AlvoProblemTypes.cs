namespace MMLib.Alvo.Api;

/// <summary>
/// Every problem type the Data API answers with, as the slug that identifies it — the machine-readable
/// half of an RFC 9457 problem document, enumerated in one place so nothing can be spelled twice.
/// </summary>
/// <remarks>
/// <para>
/// <b>A slug keys on the refusal's <em>kind</em>, never on its <em>reason</em>.</b> RFC 9457 §3.1.1 makes
/// <c>type</c> the classification a client is allowed to branch on and <c>detail</c> prose that "ought not
/// be parsed" — so the kinds here are exactly the distinctions Alvo is willing to commit to. A slug
/// encoding <em>why</em> policy refused would become the schema-and-data oracle every deny reason in the
/// framework is worded to avoid: the wording of <c>IPolicyEngine</c>'s reasons is deliberately free of the
/// entity, the row, and whether it exists, and a parseable classification beside it would hand back what
/// the prose withholds. <see cref="Forbidden"/> is therefore one slug for every policy refusal, and
/// <see cref="NotFound"/> is one slug whether the row is absent or merely invisible.
/// </para>
/// <para>
/// <see cref="OutOfScope"/> is a legitimate <em>second</em> 403 by the same rule, not an exception to it: a
/// key's own scope is a fact about the caller's credential — knowable to whoever issued it — rather than
/// about whether data exists, and the two have different fixes (grant the key a scope; change a rule). A
/// caller who cannot tell them apart re-issues the wrong one.
/// </para>
/// <para>
/// <b>The 500's slug exists, and only a host that asked for it emits one.</b> <c>IAlvoData</c>'s fifth
/// failure family (<see cref="InvalidOperationException"/> — "an invariant the implementation itself relies on
/// is broken") is still never caught by the endpoint layer: swallowing it there would lose the stack trace the
/// host's own logging exists to record, so it propagates. What changed in PR4 is that a host may now ask Alvo
/// to answer for it — <c>AddAlvoProblemDetails()</c> plus <c>UseExceptionHandler()</c> — and when it does, the
/// answer carries <see cref="Internal"/> rather than the framework's RFC 9110 status-code URI, which would
/// classify a refusal by the status the response line already carried. An embedded host that registers
/// neither keeps rendering its own 500, which is the point (#119).
/// </para>
/// <para>
/// Public because it <em>is</em> the contract: an agent or an embedded host branching on a refusal needs the
/// same constants the framework emits, and a copied string literal is how the two come to disagree.
/// </para>
/// </remarks>
public static class AlvoProblemTypes
{
    /// <summary>
    /// The namespace every problem <c>type</c> is minted under. A resolvable URI rather than a bare token,
    /// as RFC 9457 §3.1.1 asks for, so the classification doubles as the place its documentation lives.
    /// </summary>
    public const string BaseUri = "https://alvo.dev/errors/";

    /// <summary>Schema-derived validation refused the request body (422).</summary>
    public const string Validation = "validation";

    /// <summary>The query string or the request body is malformed (422) — the shape is wrong, nothing is hidden.</summary>
    /// <remarks>
    /// One slug for both, because it is the channel <c>IAlvoData</c>'s <see cref="ArgumentException"/>
    /// family lands on too: "the query or payload is malformed" is one diagnosis whether this layer or the
    /// port noticed it, and a second slug would let a caller tell which layer looked at their request.
    /// </remarks>
    public const string MalformedQuery = "malformed-query";

    /// <summary>A policy refused the operation (403).</summary>
    public const string Forbidden = "forbidden";

    /// <summary>The presented API key's scopes do not cover this entity and operation (403).</summary>
    public const string OutOfScope = "out-of-scope";

    /// <summary>The row does not exist, or the caller's policy excludes it — indistinguishably (404).</summary>
    public const string NotFound = "not-found";

    /// <summary>The write carried a version the stored row does not have (412).</summary>
    public const string PreconditionFailed = "precondition-failed";

    /// <summary>The write requires a precondition and carried none (428).</summary>
    /// <remarks>
    /// RFC 6585 §3. Distinct from <see cref="PreconditionFailed"/>, which is a precondition that <em>was</em>
    /// sent and did not hold: this one is the header's absence, and the fix is to read the current revision
    /// and send it. Applying without one is a lost update with nothing to detect it, on the one document
    /// that defines the whole backend — so it is refused rather than defaulted.
    /// </remarks>
    public const string PreconditionRequired = "precondition-required";

    /// <summary>An idempotency key was reused for a different request (409).</summary>
    public const string IdempotencyConflict = "idempotency-conflict";

    /// <summary>The request collides with stored state a database constraint guards (409).</summary>
    /// <remarks>
    /// <para>
    /// A value another record already holds on a <c>unique</c> field, or a delete a <c>ref</c> declaring
    /// <c>onDelete: "restrict"</c> refuses. Both were <see cref="Internal"/> until #138 — "an invariant Alvo
    /// itself relies on is broken", which neither of them is: the caller's request conflicts with data that was
    /// already there, and that is what 409 means.
    /// </para>
    /// <para>
    /// <b>A second 409 beside <see cref="IdempotencyConflict"/>, by the same rule <see cref="OutOfScope"/> is a
    /// second 403 by.</b> The two have different fixes and a caller can act on the difference: an idempotency
    /// conflict is repaired with a fresh key and the same body, and this one with a different body (or by
    /// removing what stands in the way). A caller who cannot tell them apart retries the wrong one forever.
    /// </para>
    /// <para>
    /// <b>One slug for both kinds, and the <c>violations</c> array carries the difference.</b> A slug keys on
    /// the refusal's kind, and "your request conflicts with stored state" is one kind; which constraint, and
    /// which field, is per-violation detail with its own stable <c>code</c> (<c>unique</c>, <c>referenced</c>)
    /// and pointer. Splitting the slug would put the schema's shape into the classification an agent branches
    /// on.
    /// </para>
    /// </remarks>
    public const string Conflict = "conflict";

    /// <summary>The change would discard data and the caller did not ask for that (409).</summary>
    /// <remarks>
    /// <para>
    /// <b>A third 409, and it is a different fix from both of the others.</b> An
    /// <see cref="IdempotencyConflict"/> is repaired with a fresh key, a <see cref="Conflict"/> with a
    /// different value — and this one with the same request plus an explicit destructive allowance, or with
    /// a descriptor that keeps what the plan would drop. A caller who cannot tell the three apart retries
    /// the wrong one.
    /// </para>
    /// <para>
    /// Not <see cref="Validation"/>: nothing about the descriptor is malformed. It is a well-formed request
    /// that collides with data already stored, which is what 409 means.
    /// </para>
    /// </remarks>
    public const string DestructiveChange = "destructive-change";

    /// <summary>A credential was presented and cannot be used (401).</summary>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>The server refused the request before Alvo could read it (400, 408 or 413).</summary>
    /// <remarks>
    /// <para>
    /// The one slug whose status is not fixed, and deliberately so: it keys on the <em>kind</em> of refusal —
    /// the request never became something Alvo could look at — while the status says which limit the server
    /// applied (a body over <c>MaxRequestBodySize</c>, framing that broke mid-upload, a body arriving too
    /// slowly). Splitting it per status would encode the server's configuration in the classification an
    /// agent branches on, and the fix is the same for all three: send a different request.
    /// </para>
    /// <para>
    /// Distinct from <see cref="MalformedQuery"/>, which is Alvo reading a request and refusing its
    /// <em>content</em>. This one is emitted only by <c>AlvoExceptionHandler</c>, from a
    /// <c>BadHttpRequestException</c> the web server raised, and therefore only in a host that registered it.
    /// </para>
    /// </remarks>
    public const string UnreadableRequest = "unreadable-request";

    /// <summary>
    /// The request carried a body that is not declared as JSON, or carried no <c>Content-Type</c> at all
    /// (415).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Distinct from <see cref="MalformedQuery"/>, and it is the <em>kind</em> that separates them.</b>
    /// This one means Alvo never looked at the content, because the caller did not declare it as JSON, and
    /// the fix is a header. <see cref="MalformedQuery"/> means Alvo read the content and refused it, and the
    /// fix is the body. It is distinct from <see cref="UnreadableRequest"/> too — that one is the web server
    /// refusing before Alvo was reached at all.
    /// </para>
    /// <para>
    /// Answered on every route that reads a request body, and on no other — a read that parses no body
    /// cannot reach it, which is why the generated document lists it on exactly seven operations per entity.
    /// </para>
    /// </remarks>
    public const string UnsupportedMediaType = "unsupported-media-type";

    /// <summary>An invariant Alvo itself relies on is broken (500).</summary>
    /// <remarks>
    /// Emitted only by <c>AlvoExceptionHandler</c>, and therefore only in a host that registered it. The slug
    /// carries no reason at all — not the exception's type, not its message — because a 500 is the one refusal
    /// whose cause is by definition not the caller's business, and its text is the log's.
    /// </remarks>
    public const string Internal = "internal";

    /// <summary>
    /// A host function (<c>AddCelFunction</c>) failed — it threw, ran past its time budget, or was handed an argument its
    /// parameter cannot hold — or Alvo's own evaluation of a hook failed, so nothing was written (500).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Internal"/> because the failing party is a function, not an Alvo invariant. A built-in
    /// function refusing the caller's data is <see cref="ExpressionFailed"/> instead; a built-in refusing a host
    /// function's output, the stored row, a default or a value a hook wrote stays here, as does any refusal on a command's
    /// or a webhook receiver's endpoint. The detail names the function (descriptor-authored) and Alvo's own reason; never the
    /// host's exception text, which is the log's. Emitted only by <c>AlvoExceptionHandler</c>.
    /// </remarks>
    public const string FunctionFailed = "function-failed";

    /// <summary>The operation sits deeper in one causation chain than the configured limit allows — a loop (508).</summary>
    /// <remarks>
    /// RFC 5842's registered meaning for 508. The document carries <c>"retryable": false</c>: the same request arrives
    /// at the same depth, so an agent must not retry it. Raised from <c>AlvoChainDepthExceededException</c>; the fix is
    /// to break the loop, or raise <c>Alvo:Extensions:MaxDepth</c> when a chain that long is intended.
    /// </remarks>
    public const string ChainDepthExceeded = "chain-depth-exceeded";

    /// <summary>A host write hook failed, so nothing was written (500).</summary>
    /// <remarks>
    /// The document carries <c>extensions</c> — the hook's id — and a <c>reason</c>: <c>exception</c> (the hook threw, or a
    /// host function in its condition did), <c>timeout</c>, <c>budget</c> (too many calls through the write's transaction), <c>invalid-set</c>
    /// (a value its field cannot hold, or a field it may not set), <c>self-write</c> (it wrote the row it runs on),
    /// <c>unbound</c> (the hook does not fit the current schema) or <c>row-vanished</c> (after its writes, the write could
    /// no longer read back the row it ran on). Never the hook's own message, which is the log's. Emitted only by
    /// <c>AlvoExceptionHandler</c>. A host hook's refusal is not this: it is <see cref="Validation"/>,
    /// <see cref="Conflict"/> or <see cref="Forbidden"/>.
    /// </remarks>
    public const string HookFailed = "hook-failed";

    /// <summary>A built-in CEL function or hook arithmetic refused the request's own data, so nothing was written (422).</summary>
    /// <remarks>
    /// <c>int('x')</c>, a <c>substring</c> past the end of the text, a <c>timestamp()</c> of a malformed value, an overflow, a
    /// division by zero, a condition that is no Bool — over the request's own values: the caller fixes it by sending other
    /// data. Only fields the request's body carried count; a refusal of a value the request did not send (the stored row an
    /// update left alone, a default, a literal, <c>@user</c>) and any refusal on a delete stay <see cref="FunctionFailed"/>.
    /// The detail names the function and Alvo's reason, never the value. Answered on every host, without opting in. A host
    /// function's failure is <see cref="FunctionFailed"/> (500), because only the host can fix it.
    /// </remarks>
    public const string ExpressionFailed = "expression-failed";

    /// <summary>The token endpoint refused the request, the subject token or the refresh token (400).</summary>
    /// <remarks>
    /// <para>
    /// <b>One slug for every OAuth 2.0 token-endpoint error</b>, and the document's <c>error</c> member carries which:
    /// <c>invalid_request</c> (a malformed form, or a subject token that is not a valid, fresh ID token from a provider
    /// this backend accepts — RFC 8693 §2.2.2), <c>invalid_grant</c> (a refresh token that is unknown, expired, revoked
    /// or reused), <c>unsupported_grant_type</c> and <c>invalid_target</c> (RFC 6749 §5.2). An OAuth client reads
    /// <c>error</c> and <c>error_description</c> from any JSON body, so the problem document carries both beside
    /// <c>detail</c>.
    /// </para>
    /// <para>
    /// Answered only on the sign-in routes, so only by a host that called <c>AddAlvoAccounts</c> and
    /// <c>MapAlvoAccounts</c>. Every refused credential gets one description, so the answer is no oracle for why.
    /// </para>
    /// </remarks>
    public const string TokenRefused = "token-refused";

    /// <summary>The sign-up policy refused to create an account for a person seen for the first time (403).</summary>
    /// <remarks>
    /// The document carries <c>error: access_denied</c> and a <c>policy</c> member naming the descriptor rule that refused:
    /// <c>invitationOnly</c> (<c>auth.signUp.mode</c>) or <c>allowedEmailDomains</c> (the person's email is not a trusted
    /// address in a listed domain). Nothing was written. Answered only on the sign-in routes. <c>access_denied</c> is an
    /// authorization-endpoint code (RFC 6749 §4.1.2.1) rather than a §5.2 one — a deliberate choice, since a client treats an
    /// unknown code generically and none of §5.2's codes says "the person may not sign up".
    /// </remarks>
    public const string SignUpRefused = "sign-up-refused";

    /// <summary>An existing account may not sign in (403).</summary>
    /// <remarks>
    /// The document carries <c>error: access_denied</c> and one fixed description naming no reason — an operator disabled
    /// the account, and only a person holding a valid ID token for that very identity ever sees this answer. Answered only
    /// on the sign-in routes.
    /// </remarks>
    public const string SignInRefused = "sign-in-refused";

    /// <summary>The caller sent more requests than the route admits in its window (429).</summary>
    /// <remarks>
    /// The response carries <c>Retry-After</c>, in seconds: the same request succeeds once the window has passed. Charged
    /// per client address, on the routes Alvo limits — the token endpoint first.
    /// </remarks>
    public const string RateLimited = "rate-limited";

    /// <summary>The route answers only when the applied descriptor enables the feature it belongs to (404).</summary>
    /// <remarks>
    /// The document carries <c>descriptorKey</c>, the descriptor key that enables the route — <c>tenancy.selfService</c>
    /// or <c>tenancy.selfService.createTenants</c> — so an agent learns what to declare rather than guessing why a route
    /// it found in the OpenAPI document is missing. The self-service routes are always mapped (accounts spec §6.3); this
    /// is what they answer while their feature is off. Answered only on a host that maps the sign-in routes.
    /// </remarks>
    public const string FeatureDisabled = "feature-disabled";

    /// <summary>The change would leave a shared tenant without an owner (409).</summary>
    /// <remarks>
    /// The last owner cannot leave, be removed or lose ownership (accounts spec §6.4): another member has to be made an
    /// owner first. Distinct from <see cref="Conflict"/> because the fix differs — promote someone, rather than send
    /// another value. Answered only on the self-service routes.
    /// </remarks>
    public const string LastOwner = "last-owner";

    /// <summary>The person has created as many shared tenants as the descriptor allows one account (409).</summary>
    /// <remarks>
    /// The document carries <c>maxPerAccount</c>, the cap (<c>tenancy.selfService.createTenants.maxPerAccount</c>). The
    /// count is every shared tenant the account ever created, so handing one to someone else frees nothing. Answered only
    /// on the self-service routes.
    /// </remarks>
    public const string TenantLimit = "tenant-limit";

    /// <summary>
    /// Every slug this catalogue declares. Enumerated rather than discovered by reflection so a fact can
    /// assert the catalogue and the code agree without the assertion being satisfied by its own subject.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Validation,
        MalformedQuery,
        Forbidden,
        OutOfScope,
        NotFound,
        PreconditionFailed,
        PreconditionRequired,
        IdempotencyConflict,
        Conflict,
        DestructiveChange,
        Unauthenticated,
        UnreadableRequest,
        UnsupportedMediaType,
        Internal,
        FunctionFailed,
        ChainDepthExceeded,
        HookFailed,
        ExpressionFailed,
        TokenRefused,
        SignUpRefused,
        SignInRefused,
        RateLimited,
        FeatureDisabled,
        LastOwner,
        TenantLimit,
    ];

    /// <summary>The full problem <c>type</c> URI for one slug.</summary>
    /// <remarks>
    /// <b>An unknown slug is family 5, not a malformed request.</b> Every call site passes a constant from this
    /// catalogue, so reaching the throw means a framework author minted a <c>type</c> the catalogue does not
    /// declare — an invariant this implementation relies on, which <c>ProblemResultFactory.GuardAsync</c> lets
    /// propagate to the host as a 500 with its stack trace. An <see cref="ArgumentException"/> here would land
    /// on that guard's widest arm instead and be rendered to the caller as <em>"the request is malformed"</em>,
    /// which is the exact defect <c>JsonPayloadReader.TryBind</c> narrowed its own catch to avoid: telling a
    /// caller to fix a request that was fine.
    /// </remarks>
    /// <param name="slug">One of this type's slugs.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="slug"/> is not a slug this catalogue declares.
    /// </exception>
    public static string UriOf(string slug)
    {
        if (!All.Contains(slug, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{slug}' is not an Alvo problem type. Use one of: {string.Join(", ", All)}.");
        }

        return BaseUri + slug;
    }
}
