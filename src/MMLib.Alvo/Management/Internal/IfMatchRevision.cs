namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// What an <c>If-Match</c> header named: nothing at all, something this API cannot compare, or a revision.
/// </summary>
/// <remarks>
/// <b>Three states rather than an <c>int?</c>, because two of them are different refusals.</b> An absent
/// header is <c>428</c> — the caller has not sent a precondition and the fix is to read one — and an
/// uncomparable one is <c>412</c>. Collapsing them into a single null would answer "you sent the wrong
/// revision" to a caller who sent none, which is the one wording they cannot act on.
/// </remarks>
/// <param name="Value">The revision the header named, or <see langword="null"/> when it named none.</param>
/// <param name="Present">Whether the header was sent at all.</param>
internal readonly record struct IfMatchRevision(int? Value, bool Present)
{
    /// <summary>No <c>If-Match</c> was sent.</summary>
    internal static IfMatchRevision Absent => new(null, Present: false);

    /// <summary>An <c>If-Match</c> was sent and names no revision this API can compare.</summary>
    internal static IfMatchRevision Uncomparable => new(null, Present: true);

    /// <summary>An <c>If-Match</c> naming one revision.</summary>
    /// <param name="revision">The revision it named.</param>
    internal static IfMatchRevision Of(int revision) => new(revision, Present: true);
}
