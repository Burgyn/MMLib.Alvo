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
/// </param>
/// <remarks>
/// <para>
/// <b>The key is scoped to the caller's tenant, and that scoping is part of the record's identity rather
/// than a column beside it.</b> A key is the caller's own string, so two tenants will collide on
/// <c>"1"</c> sooner rather than later; in a shared key space one tenant's replay would be answered with
/// another tenant's row. An implementation therefore keys a stored record on (key, tenant), with a fixed
/// sentinel standing in for a global entity's absent tenant.
/// </para>
/// <para>
/// <b>An implementation stores the row's id, never a rendered response.</b> On replay the row is re-read
/// through the caller's <em>current</em> policy, so a replay can never hand back a representation the
/// caller's policy would not produce today — a field that has since become <c>hidden</c> for them stays
/// hidden, and a row they can no longer see is not resurrected from a cache.
/// </para>
/// <para>
/// The <paramref name="Fingerprint"/> is computed by whoever owns the request — the HTTP layer hashes
/// method, path, and body — because only that layer knows what "the same request" means on its own wire
/// format. This port compares the string it was given and does not interpret it.
/// </para>
/// </remarks>
public readonly record struct AlvoIdempotency(string Key, string Fingerprint);
