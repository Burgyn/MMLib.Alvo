using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The fix each site offers for each refusal — the five per-screen tables F-9 found, in one place.
/// </summary>
internal static class AdminProblemFixes
{
    /// <summary>What to do about a refusal at one site, or <see langword="null"/> when nothing better than the detail can be said.</summary>
    /// <param name="exception">The refusal.</param>
    /// <param name="site">Where it happened.</param>
    public static string? For(Exception exception, ProblemSite site) => site switch
    {
        ProblemSite.SchemaApply => SchemaApply(exception),
        ProblemSite.Rollback => Rollback(exception),
        ProblemSite.People => People(exception),
        ProblemSite.Records or ProblemSite.ScopedRecordsWithoutTenant => Records(exception, site),
        ProblemSite.RecordWrite => RecordWrite(exception),
        _ => null,
    } ?? General(exception);

    private static string? General(Exception exception) => exception switch
    {
        CelSyntaxException { FixSuggestion: { Length: > 0 } suggestion } => suggestion,
        _ => null,
    };

    /// <summary>
    /// The two concurrency answers are worth separating: an <em>absent</em> precondition is a 428 and a
    /// <em>stale</em> one is a 412, and they send an operator to two different places — one is a bug in the
    /// client, the other is somebody else having applied in between.
    /// </summary>
    private static string? SchemaApply(Exception exception) => exception switch
    {
        DescriptorConcurrencyException
            => "Somebody applied a revision while this copy was open. Reload the descriptor, re-check the diff, and apply again — your edits are still here.",
        DestructiveChangeNotAllowedException
            => "The plan discards data and that was not confirmed. Type the project's name above to allow it.",
        DescriptorValidationException
            => "The descriptor breaks a rule every Alvo descriptor must follow. The message above says where in it.",
        ManagementEscalationException
            => "This change touches the access block, which re-qualifies the whole apply to admin. Your management level is lower.",
        _ => null,
    };

    private static string? Rollback(Exception exception) => exception switch
    {
        DescriptorConcurrencyException => "Somebody applied a revision while this screen was open. Reload and try again.",
        _ => null,
    };

    /// <summary>
    /// The escalation refusals are the interesting ones and are worth telling apart from a plain "forbidden":
    /// one of them means <em>you are not an administrator</em> and the other means <em>you are, and this is the
    /// one thing an administrator may not do to themselves</em>.
    /// </summary>
    private static string? People(Exception exception) => exception switch
    {
        ManagementEscalationException => "Another administrator can make this change, or the access block can, through an apply — which records who made it.",
        ManagementForbiddenException => "Administering people is an admin operation. Your management level is lower.",
        NotSupportedException => "This deployment's membership store does not support that operation.",
        _ => null,
    };

    /// <summary>
    /// <see cref="AlvoAuthorizationException"/> is the tenant guard and the unconfigured-operation case — the two
    /// that really are a 403 rather than an empty page — and the distinction is worth carrying into the wording,
    /// because "you are refused" and "you match nothing" send an operator to two different screens.
    /// </summary>
    private static string? Records(Exception exception, ProblemSite site) => exception switch
    {
        AlvoAuthorizationException when site == ProblemSite.ScopedRecordsWithoutTenant
            => "This entity is tenant-scoped and your account holds no tenant. An administrator grants one in Access.",
        AlvoAuthorizationException
            => "The descriptor configures no rule for this operation, or the tenant guard refused before any rule ran. The entity's Rules tab shows which operations are configured.",
        AlvoConstraintViolationException violation
            => $"A constraint refused the value: {violation.Kind}. The field's facets are on the entity's Fields tab.",
        _ => null,
    };

    /// <summary>
    /// A unique collision is a <c>409</c> with violation code <c>unique</c> — not a validation error and not an
    /// "unknown field", neither of which the problem-type catalogue contains. The wording follows the catalogue
    /// rather than inventing a friendlier one, because the operator may well be reading the same slug in a log.
    /// </summary>
    private static string? RecordWrite(Exception exception) => exception switch
    {
        AlvoConstraintViolationException { Kind: AlvoConstraintKind.Unique } violation
            => $"Another record already has this value for {string.Join(", ", violation.Fields)}. The field is declared unique on the entity's Fields tab.",
        AlvoConstraintViolationException violation
            => $"The value does not satisfy {violation.Kind} on {string.Join(", ", violation.Fields)}.",
        AlvoAuthorizationException
            => "The write rule on this entity does not admit you, or the entity is tenant-scoped and you hold no tenant.",
        AlvoRecordNotFoundException
            => "The record is gone, or the read rule no longer admits it. Both answer 404, deliberately.",
        _ => null,
    };
}
