using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data;

/// <summary>
/// The row version a caller believes it is changing — the port's optimistic-concurrency channel.
/// </summary>
/// <param name="Version">
/// The row's <c>updated_at</c> instant as the caller last read it. Compared for equality against
/// the row-locked pre-image inside the write transaction, so the comparison cannot race the write
/// it guards.
/// </param>
/// <remarks>
/// <para>
/// <b>A <see cref="DateTimeOffset"/>, not an opaque string.</b> This port does not know what an HTTP
/// <c>ETag</c> is, and it must not: the encoding (quoting, weak/strong, base64) belongs to the request
/// layer that speaks HTTP, and a port taking a pre-encoded string would force every other caller — an
/// automation action, a future gRPC surface, a test — to learn that encoding first. What the port owns is
/// the comparison; what the API layer owns is the spelling.
/// </para>
/// <para>
/// <b>The version must survive its own round trip, so it is only ever minted from a stored value.</b>
/// PostgreSQL's <c>timestamptz</c> keeps microseconds and SQLite keeps rendered text, while a .NET clock
/// keeps 100-nanosecond ticks — so a version taken from <see cref="TimeProvider"/> at the moment of the
/// write would not equal the value the same write stored, and every following precondition would be
/// refused with nothing for the caller to diagnose. Both members below therefore compare a
/// caller-supplied version against a value that came <em>out of</em> the database, never against one this
/// process computed.
/// </para>
/// </remarks>
public readonly record struct AlvoPrecondition(DateTimeOffset Version)
{
    /// <summary>
    /// Throws when <paramref name="precondition"/> is supplied for an entity that has no version column
    /// at all — refused, never ignored.
    /// </summary>
    /// <param name="precondition">The caller's precondition, or <see langword="null"/> when they sent none.</param>
    /// <param name="entity">The entity being written, as the implementation's applied schema declares it.</param>
    /// <remarks>
    /// <para>
    /// A silently ignored precondition is a lost update the caller believes it prevented: they sent
    /// <c>If-Match</c>, got a <c>200</c>, and overwrote a concurrent writer's change anyway. There is no
    /// third option here — an entity with no <see cref="AlvoManagedColumns.VersionColumn"/> cannot answer
    /// "has this row changed since you read it" at all, so the only honest answers are "refuse" and "lie".
    /// </para>
    /// <para>
    /// Decided from the <em>schema</em> alone, before any row is looked up, which is what keeps it off the
    /// existence-oracle channel: the answer is identical for a row that is present, absent, or invisible to
    /// this caller, so it discloses only whether the entity declares <c>audit</c> — something the
    /// descriptor's author already knows.
    /// </para>
    /// <para>
    /// The message lives here, on the port, for the reason
    /// <see cref="AlvoManagedColumns.RefusalReason"/> gives: the inherited contract suite asserts on it, and
    /// two implementations wording one refusal two ways would give this port two contracts.
    /// </para>
    /// </remarks>
    /// <exception cref="AlvoPreconditionFailedException">
    /// <paramref name="precondition"/> is not <see langword="null"/> and <paramref name="entity"/> declares
    /// no version column.
    /// </exception>
    public static void EnsureSupported(AlvoPrecondition? precondition, EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (precondition is not null && AlvoManagedColumns.VersionColumn(entity) is null)
        {
            throw new AlvoPreconditionFailedException(
                "This record cannot answer a version precondition, because the entity keeps no version of a "
                + "row: 'updated_at' exists only where 'audit: true' asked for it. Add 'audit: true' to the "
                + "entity, or send the write without a precondition.");
        }
    }

    /// <summary>
    /// Throws unless <paramref name="precondition"/> matches <paramref name="storedVersion"/> — the version
    /// column's value on the row-locked pre-image, read inside the write transaction.
    /// </summary>
    /// <param name="precondition">The caller's precondition, or <see langword="null"/> when they sent none.</param>
    /// <param name="storedVersion">
    /// The pre-image's version column value, exactly as storage returned it. A value that is not a
    /// <see cref="DateTimeOffset"/> — <see langword="null"/>, or some driver's own spelling — is refused
    /// rather than coerced: this is the one comparison that decides whether a concurrent write is about to
    /// be silently overwritten, and it fails closed.
    /// </param>
    /// <remarks>
    /// Called only <b>after</b> the operation's <c>USING</c> predicate has decided that the row is visible.
    /// A row the caller's policy excludes raises <see cref="AlvoRecordNotFoundException"/> whatever
    /// precondition was supplied — otherwise "was my <c>If-Match</c> stale, or is the row not mine" becomes
    /// an oracle for a row's existence, one request at a time.
    /// </remarks>
    /// <exception cref="AlvoPreconditionFailedException"><paramref name="precondition"/> does not match the stored version.</exception>
    public static void EnsureMatches(AlvoPrecondition? precondition, object? storedVersion)
    {
        if (precondition is not { } expected)
        {
            return;
        }

        if (storedVersion is not DateTimeOffset stored || stored != expected.Version)
        {
            throw new AlvoPreconditionFailedException();
        }
    }
}
