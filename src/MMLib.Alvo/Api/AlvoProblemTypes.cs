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
/// <b>There is no slug for a 500, and that is deliberate.</b> <c>IAlvoData</c>'s fifth failure family
/// (<see cref="InvalidOperationException"/> — "an invariant the implementation itself relies on is broken")
/// is never caught by this layer: swallowing it into a hand-made problem document would lose the stack
/// trace the host's own logging exists to record, so it propagates and the host answers. Cataloguing a slug
/// Alvo never emits would document a behaviour that does not exist — which is the same defect as an
/// unreachable entry anywhere else — so the catalogue stops at what Alvo actually produces, and
/// <c>ProblemDetailsTests</c> holds it to that.
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

    /// <summary>An idempotency key was reused for a different request (409).</summary>
    public const string IdempotencyConflict = "idempotency-conflict";

    /// <summary>A credential was presented and cannot be used (401).</summary>
    public const string Unauthenticated = "unauthenticated";

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
        IdempotencyConflict,
        Unauthenticated,
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
