namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The wire shape of an apply's request body.
/// </summary>
/// <remarks>
/// <b>It carries neither the expected revision nor the dry-run flag</b>, and that split is the whole point:
/// those two arrive as <c>If-Match</c> and <c>?dryRun=</c>, and a field with two sources is a field two
/// callers can disagree about. <see cref="ToRequest"/> is where the three are joined into the contract's
/// own <see cref="ManagementApplyRequest"/>.
/// </remarks>
/// <param name="DescriptorJson">The descriptor to apply, exactly as it should be stored.</param>
/// <param name="AllowDestructive">Whether a plan that discards data may proceed. Never implied.</param>
/// <param name="Author">Who is applying, carried into the appended revision.</param>
/// <param name="Reason">Why, carried into the appended revision.</param>
internal sealed record ManagementApplyBody(
    string? DescriptorJson,
    bool AllowDestructive = false,
    string? Author = null,
    string? Reason = null)
{
    /// <summary>The contract request this body, that precondition and that query string make up.</summary>
    /// <param name="expectedRevision">The revision <c>If-Match</c> named.</param>
    /// <param name="dryRun">Whether <c>?dryRun=true</c> asked for a plan-only pass.</param>
    internal ManagementApplyRequest ToRequest(int expectedRevision, bool dryRun) => new(
        DescriptorJson ?? string.Empty, expectedRevision, AllowDestructive, dryRun, Author, Reason);
}
