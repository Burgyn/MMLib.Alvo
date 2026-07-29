using System.Text;

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
/// <see cref="EnsureUsableKey"/>.
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
    /// <see cref="EnsureUsableKey"/> is what keeps this method's contract true rather than
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
    /// The longest key any caller may hold, in <b>UTF-8 bytes</b> — 255.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bytes, not characters, because the constraint it serves is a byte one.</b> The key is half of the
    /// record's composite primary key, and PostgreSQL caps a btree index entry at roughly 2700 bytes — so a
    /// bound counted in UTF-16 <c>string.Length</c> would let a key of multi-byte characters past it and hand
    /// storage exactly the over-long index entry the bound exists to prevent. It was written that way once:
    /// 4000 <em>characters</em> passed every check and could be 16 000 bytes.
    /// </para>
    /// <para>
    /// <b>Here rather than in a request layer's options, because this is the layer that can be bypassed.</b> An
    /// embedded host calls <c>CreateAsync</c> directly, with no HTTP in front of it, so a bound that lived only
    /// there would guard the one caller who is least likely to send a hostile key. A request layer may narrow
    /// it — <c>AlvoApiOptions.MaxIdempotencyKeyBytes</c> defaults to this number and is refused above it — but
    /// nothing may widen it.
    /// </para>
    /// <para>
    /// 255 is also what the <c>Idempotency-Key</c> header's field implementations conventionally allow, so a
    /// client that already speaks the header fits inside it.
    /// </para>
    /// </remarks>
    public const int MaxKeyBytes = 255;

    /// <summary>
    /// Throws when <paramref name="idempotency"/> carries a key this port cannot record, or one
    /// <paramref name="context"/> has no identity to scope it by. The guard every implementation of
    /// <c>IAlvoData.CreateAsync</c> calls.
    /// </summary>
    /// <param name="idempotency">The caller's token, or <see langword="null"/> when they sent none.</param>
    /// <param name="context">The caller the create is performed as.</param>
    /// <remarks>
    /// <para>
    /// A token-shaped wrapper over <see cref="EnsureUsableKey"/> so an implementation writes one call rather
    /// than one call plus a null test — <b>no token is always legal</b>, and forgetting that half is how a
    /// plain create starts being refused.
    /// </para>
    /// <para>
    /// On the port, beside the other rules every implementation must obey
    /// (<see cref="AlvoFilter.EnsureWithinLimits"/>, <see cref="AlvoQuery.EnsurePagingWindowIsSane"/>,
    /// <see cref="AlvoPrecondition.EnsureSupported"/>), so a third implementation inherits it instead of
    /// writing a fourth copy — and so the inherited contract suite proves both shipped ones call it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The token cannot be recorded for this caller.</exception>
    public static void EnsureUsableToken(AlvoIdempotency? idempotency, AlvoContext context)
    {
        if (idempotency is { } token)
        {
            EnsureUsableKey(token.Key, context);
        }
    }

    /// <summary>
    /// Throws unless <paramref name="key"/> is a key this port can record for <paramref name="context"/>:
    /// present, non-blank, within <see cref="MaxKeyBytes"/>, and belonging to a caller with an identity.
    /// </summary>
    /// <param name="key">The caller's key.</param>
    /// <param name="context">The caller the create is performed as.</param>
    /// <remarks>
    /// <para>
    /// <b>Three rules in one guard, and all three are the port's rather than a request layer's.</b> This type is
    /// a <c>readonly record struct</c>, so <c>default(AlvoIdempotency)</c> and
    /// <c>new AlvoIdempotency(null!, null!)</c> both exist and no constructor can be made to run — a static
    /// guard is therefore the only shape that can hold an invariant here at all. And every one of the three
    /// rules is broken by an <em>embedded host</em>, not by an HTTP caller: the request layer already refuses
    /// each of them before the port is reached, which is precisely why leaving them there was a mistake.
    /// </para>
    /// <para>
    /// <b>A blank key is the worst of the three, and it was the one nothing checked.</b>
    /// <c>new AlvoIdempotency("", …)</c> lands the empty string in
    /// <c>PRIMARY KEY (idempotency_key, scope)</c>, so every caller in one scope who ever sends a blank key
    /// shares one record — the shared key space <see cref="IdentityOf"/> exists to remove, restored silently
    /// and per-caller. An over-long key at least fails loudly at storage; a blank one succeeds and starts
    /// answering the wrong row.
    /// </para>
    /// <para>
    /// <b>The length rule is in bytes</b> — see <see cref="MaxKeyBytes"/> for why counting characters was
    /// wrong. It refuses rather than truncates: two keys differing only past the cut would become one, so the
    /// second create would be answered with the first create's row.
    /// </para>
    /// <para>
    /// <b>The identity rule fails closed, because the alternatives are worse.</b> A record's identity is
    /// <see cref="IdentityOf"/>'s scope, and every <see cref="AlvoContext.Anonymous"/> caller carries the same
    /// reserved all-zero <see cref="UserId"/> — so on an entity whose policy permits anonymous creates, every
    /// anonymous caller in a tenant would share one key space, and one caller's replay could reach another's
    /// record. The only other options were a silently shared key space or an identity invented per request
    /// (which is not an identity, and would make every replay a miss). Refusing says so out loud.
    /// <see cref="AlvoContext.System"/> is unaffected, and that is a checked property rather than a hope: its
    /// user id is a distinct reserved value, not the all-zero one, so a system-context token scopes like any
    /// other caller's. <c>An_idempotency_token_from_the_system_context_is_accepted</c> pins it.
    /// </para>
    /// <para>
    /// <b>The malformed-request family, with a fix suggestion</b> — a request layer renders all three as 422.
    /// None is a denial: the caller is not being told they may not create, they are being told this
    /// <em>combination</em> cannot be served. Each is decided from the key and the context alone, before any
    /// policy is resolved or any entity is looked up, so none discloses anything about the entity — the answer
    /// is identical for one that does not exist.
    /// </para>
    /// <para>
    /// <b>The caller's key is never echoed into the message.</b> It is caller-supplied text — a log-injection
    /// vector like every other such string on this port — so the refusal names the rule and the bound, never
    /// the value.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The key cannot be recorded for this caller.</exception>
    public static void EnsureUsableKey(string? key, AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException(
                "An idempotency key must not be blank: a record is filed under the key and the caller's scope, "
                + "so every caller who sent a blank one would share a single record and one caller's retry "
                + "could be answered with another's row. Send an opaque, unique key, or send this create "
                + "without one.",
                nameof(key));
        }

        if (Encoding.UTF8.GetByteCount(key) > MaxKeyBytes)
        {
            throw new ArgumentException(
                $"An idempotency key must be at most {MaxKeyBytes} bytes when encoded as UTF-8. It is refused "
                + "rather than shortened, because two keys that differ only past that length would become one "
                + "key and the second create would be answered with the first create's row.",
                nameof(key));
        }

        if (context.User.Value == default)
        {
            throw new ArgumentException(
                "An idempotency key needs a caller it can be scoped to, and an anonymous caller has none: "
                + "every anonymous caller shares one reserved identity, so their keys would share one space "
                + "and one caller's retry could be answered with another's row. Authenticate the caller, or "
                + "send this create without an idempotency key.",
                nameof(key));
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
