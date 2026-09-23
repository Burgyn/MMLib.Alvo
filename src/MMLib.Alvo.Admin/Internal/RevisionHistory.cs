using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The configuration history in the order an operator reads it, and the words a revision is shown with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordered once, here, rather than by each screen that lists revisions.</b> The management contract
/// answers oldest first — the order a log is appended in — and Overview took <c>[0]</c> of that as "the
/// latest change", so after an apply of r2 it still showed r1. Every reader of the history wants the
/// newest at the top, so the gateway sorts before any screen sees the list and no screen has an index to
/// get wrong.
/// </para>
/// <para>
/// By revision number rather than by timestamp: the number is what the append-only log is ordered by and
/// what an <c>If-Match</c> carries, and two applies inside one clock tick share a timestamp.
/// </para>
/// </remarks>
internal static class RevisionHistory
{
    /// <summary>The title of the first revision when nobody recorded why it was applied.</summary>
    public const string InitialDescriptor = "Initial descriptor";

    /// <summary>The title of any later revision nobody recorded a reason for.</summary>
    public const string NoReason = "No reason recorded";

    /// <summary>The revisions, highest first.</summary>
    /// <param name="revisions">The revisions as the contract answered them, in any order.</param>
    public static IReadOnlyList<ManagementRevision> NewestFirst(IEnumerable<ManagementRevision> revisions)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        return [.. revisions.OrderByDescending(revision => revision.Revision)];
    }

    /// <summary>What a revision is called in a list: its reason, or what the absence of one means.</summary>
    /// <remarks>
    /// Revision 1 with no reason is the descriptor the project started from — mounted or seeded rather than
    /// changed by somebody — so calling it "no reason recorded" read as a gap in the audit trail where there
    /// is none.
    /// </remarks>
    /// <param name="revision">The revision to name.</param>
    public static string Title(ManagementRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        return revision.Reason is { Length: > 0 } reason
            ? reason
            : revision.Revision == 1 ? InitialDescriptor : NoReason;
    }

    /// <summary>Who applied a revision, or <see langword="null"/> when the line should not name anybody.</summary>
    /// <remarks>
    /// The initial descriptor with no author is left unattributed rather than labelled "code-first or
    /// system": the title already says what it is, and the label only repeated that in jargon.
    /// </remarks>
    /// <param name="revision">The revision to attribute.</param>
    public static string? Author(ManagementRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        if (revision.Author is { Length: > 0 } author)
        {
            return author;
        }

        return IsInitial(revision) ? null : "code-first or system";
    }

    private static bool IsInitial(ManagementRevision revision)
        => revision.Revision == 1 && revision.Reason is not { Length: > 0 };
}
