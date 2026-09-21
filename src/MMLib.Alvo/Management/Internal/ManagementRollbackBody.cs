namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The wire shape of a rollback's request body.
/// </summary>
/// <remarks>
/// <b>Everything in it is optional, and the body itself is too.</b> What to restore is in the route, the
/// base is <c>If-Match</c>, the plan-only flag is <c>?dryRun=</c> and the key is <c>Idempotency-Key</c> — so
/// what is left here is the destructive allowance and the provenance, none of which a caller has to send.
/// A rollback with no body at all is a well-formed request, which is why <see cref="ToRequest"/> can be
/// reached through a default instance.
/// </remarks>
/// <param name="AllowDestructive">
/// Whether the reverse migration may discard data. Never implied — a reverse migration routinely drops what
/// the forward one added.
/// </param>
/// <param name="Author">Who is rolling back, carried into the appended revision.</param>
/// <param name="Reason">Why; absent, the framework's own <c>Rollback to revision N</c> stands in.</param>
internal sealed record ManagementRollbackBody(
    bool AllowDestructive = false,
    string? Author = null,
    string? Reason = null)
{
    /// <summary>The contract request this body, that precondition and that query string make up.</summary>
    /// <param name="expectedRevision">The revision <c>If-Match</c> named.</param>
    /// <param name="dryRun">Whether <c>?dryRun=true</c> asked for a plan-only pass.</param>
    /// <param name="idempotencyKey">The key <c>Idempotency-Key</c> carried, or <see langword="null"/>.</param>
    /// <returns>The contract request.</returns>
    internal ManagementRollbackRequest ToRequest(int expectedRevision, bool dryRun, string? idempotencyKey) =>
        new(expectedRevision, AllowDestructive, dryRun, Author, Reason, idempotencyKey);
}
