namespace MMLib.Alvo.Data;

/// <summary>
/// A caller-supplied idempotency token: replaying the same key with the same request must return the
/// first request's row and create nothing new.
/// </summary>
/// <param name="Key">The caller's key, verbatim.</param>
/// <param name="Fingerprint">
/// A hash of the request this key was first used for. A replay carrying the same key and a different
/// fingerprint is a conflict, not a replay — the caller reused a key for a different request, and
/// answering with the first result would silently discard the second one.
/// <para>
/// <b>It must cover the entity being written.</b> The layer that computes it hashes the whole request — for
/// HTTP, the method, the path and the body, and the path names the entity — so a matched fingerprint proves a
/// replay is for the same entity the original wrote, which is why an implementation stores no entity beside
/// the record. The same key against a different entity is therefore a conflict, like any other different
/// request. A caller whose fingerprint does <em>not</em> distinguish the entity is still never answered with a
/// wrong row: the replay re-reads the recorded row id under the entity of the request being served, finds
/// nothing there, and raises <see cref="AlvoRecordNotFoundException"/> — fail-closed, never cross-entity.
/// </para>
/// </param>
/// <remarks>
/// <para>
/// <b>The key is scoped to the tenant <em>and</em> to the acting user, and that scoping is part of the
/// record's identity rather than a column beside it</b> — see <see cref="IdentityOf"/>. It follows that an
/// <b>anonymous caller cannot hold a key at all</b>: every anonymous caller carries the same reserved all-zero
/// <see cref="UserId"/>, so there is no identity to scope by and a token from one is refused outright — see
/// <see cref="EnsureIdentifiableCaller"/>.
/// </para>
/// <para>
/// <b>An implementation stores the row's id, never a rendered response.</b> On replay the row is re-read
/// through the caller's <em>current</em> <c>get</c> policy, so a replay can never hand back a representation
/// the caller's policy would not produce today — a field that has since become <c>hidden</c> for them stays
/// hidden, and a row they can no longer see is not resurrected from a cache.
/// </para>
/// <para>
/// The <paramref name="Fingerprint"/> is computed by whoever owns the request, because only that layer knows
/// what "the same request" means on its own wire format. This port compares the string it was given and does
/// not interpret it.
/// </para>
/// </remarks>
public readonly record struct AlvoIdempotency(string Key, string Fingerprint)
{
    /// <summary>
    /// The stored record's identity for <paramref name="context"/> — the scope this caller's key is qualified
    /// by: the tenant, and the acting user.
    /// </summary>
    /// <param name="context">The caller the create is performed as.</param>
    /// <remarks>
    /// <para>
    /// <b>Scoped to the user as well as the tenant</b>, because a key is the caller's own opaque string, so
    /// two clients in one tenant will collide on <c>"1"</c>. Sharing the key space between them lets one
    /// client's replay return the other client's row, and makes the 409-versus-201 outcome a probe of the
    /// other's key space. Two callers who happen to share a key string are two different clients making two
    /// different requests, and the right answer is two rows — not a refusal, and not a shared one.
    /// </para>
    /// <para>
    /// <b>One member on the port, called by every implementation.</b> The identity was written twice — once
    /// per shipped backend — with no test that could catch the two drifting apart, which is exactly the
    /// situation <see cref="AlvoPrecondition.EnsureSupported"/> was hoisted here to avoid. Scoped by
    /// construction, so no call site can forget a part of it.
    /// </para>
    /// <para>
    /// <b>The tenantless sentinel is the literal <see cref="TenantlessScope"/>, not the empty GUID.</b> It
    /// cannot be the text of any GUID, so no real tenant can collide with it and no non-empty guard on
    /// <see cref="TenantId"/> is needed to keep that true — where the empty GUID relied on an invariant
    /// nothing enforces. The separator is <c>/</c>, which occurs in neither a GUID nor the sentinel, so the two
    /// parts can never be read as one another.
    /// </para>
    /// <para>
    /// <b>The scoping claim above holds only for a caller who has an identity, which is why an anonymous one is
    /// refused before it gets here.</b> The all-zero <see cref="UserId"/> is reserved framework-wide to mean
    /// "no identity" (see <see cref="AlvoContext.Anonymous"/>), so <em>every</em> anonymous caller in a tenant
    /// would produce the same scope and share one key space — the exact collision this member exists to remove,
    /// reintroduced for the one caller who cannot be told apart from the next.
    /// <see cref="EnsureIdentifiableCaller"/> is what keeps this method's contract true rather than
    /// conditionally true, and it is stated here because a reader who takes this scope as unconditionally
    /// per-caller would build on a claim that is false in that one case.
    /// </para>
    /// </remarks>
    public static string IdentityOf(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return $"{context.Tenant?.Value.ToString() ?? TenantlessScope}/{context.User.Value}";
    }

    /// <summary>
    /// The tenant part of <see cref="IdentityOf"/> for a caller with no tenant — a global entity's writes.
    /// </summary>
    public static string TenantlessScope => "global";

    /// <summary>
    /// Throws when <paramref name="idempotency"/> is supplied for a caller who has no identity to scope it by.
    /// </summary>
    /// <param name="idempotency">The caller's token, or <see langword="null"/> when they sent none.</param>
    /// <param name="context">The caller the create is performed as.</param>
    /// <remarks>
    /// <para>
    /// <b>Fail closed, because the alternatives are worse.</b> A record's identity is
    /// <see cref="IdentityOf"/>'s scope, and every <see cref="AlvoContext.Anonymous"/> caller carries the same
    /// reserved all-zero <see cref="UserId"/> — so on an entity whose policy permits anonymous creates, every
    /// anonymous caller in a tenant would share one key space, and one caller's replay could reach another's
    /// record. The only other options were a silently shared key space or an identity invented per request
    /// (which is not an identity, and would make every replay a miss). Refusing says so out loud.
    /// </para>
    /// <para>
    /// <b><see cref="AlvoContext.System"/> is unaffected</b>, and that is a checked property rather than a
    /// hope: its user id is a distinct reserved value, not the all-zero one, so a system-context token scopes
    /// like any other caller's. <c>An_idempotency_token_from_the_system_context_is_accepted</c> pins it.
    /// </para>
    /// <para>
    /// <b>The malformed-request family, with a fix suggestion</b> — a request layer renders it 422. It is not a
    /// denial: the caller is not being told they may not create, they are being told this <em>combination</em>
    /// of an anonymous identity and an idempotency key cannot be served. Decided from the token and the context
    /// alone, before any policy is resolved or any entity is looked up, so it discloses nothing about the
    /// entity — the answer is identical for one that does not exist.
    /// </para>
    /// <para>
    /// On the port, beside the other rules every implementation must obey
    /// (<see cref="AlvoFilter.EnsureWithinLimits"/>, <see cref="AlvoQuery.EnsurePagingWindowIsSane"/>,
    /// <see cref="AlvoPrecondition.EnsureSupported"/>), so a third implementation inherits it instead of
    /// writing a fourth copy — and so the inherited contract suite proves both shipped ones call it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="context"/> is anonymous and a token was supplied.</exception>
    public static void EnsureIdentifiableCaller(AlvoIdempotency? idempotency, AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (idempotency is not null && context.User.Value == default)
        {
            throw new ArgumentException(
                "An idempotency key needs a caller it can be scoped to, and an anonymous caller has none: "
                + "every anonymous caller shares one reserved identity, so their keys would share one space "
                + "and one caller's retry could be answered with another's row. Authenticate the caller, or "
                + "send this create without an idempotency key.",
                nameof(idempotency));
        }
    }

    /// <summary>
    /// Whether <paramref name="storedFingerprint"/> is the fingerprint of the request this token carries.
    /// </summary>
    /// <param name="storedFingerprint">The fingerprint recorded when this key was first used.</param>
    /// <remarks>
    /// An ordinal comparison, in one place, because a fingerprint is a digest: a culture-sensitive or
    /// case-insensitive comparison of one is a silent collision — <c>ff</c> and <c>FF</c> are different
    /// digests and must stay different requests. This too was written twice, once per shipped backend.
    /// </remarks>
    public bool Matches(string storedFingerprint) =>
        string.Equals(storedFingerprint, Fingerprint, StringComparison.Ordinal);
}
