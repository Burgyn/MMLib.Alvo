using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Secrets;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Where a refusal happened, which decides the fix a panel offers for it.
/// </summary>
/// <remarks>
/// The same exception needs a different next step on different screens: an escalation on Preview means the
/// apply touched the access block, and on Access it means an administrator may not do this to themselves.
/// The site is named by the screen rather than worked out from the exception, because only the screen knows.
/// </remarks>
internal enum ProblemSite
{
    /// <summary>Any screen with no fix of its own; the refusal's type alone decides.</summary>
    General,

    /// <summary>Preview: planning or applying the working copy.</summary>
    SchemaApply,

    /// <summary>History: restoring an earlier revision.</summary>
    Rollback,

    /// <summary>Access: administering people.</summary>
    People,

    /// <summary>Data: reading an entity's records.</summary>
    Records,

    /// <summary>Data, on a tenant-scoped entity, for a caller who holds no tenant.</summary>
    ScopedRecordsWithoutTenant,

    /// <summary>The record form: creating, saving or deleting one record.</summary>
    RecordWrite,
}

/// <summary>
/// One error policy for the whole dashboard: what an exception becomes on screen, and whether it is logged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three answers, and only three</b> (docs/architecture/admin-dashboard-review.md, F-9). A refusal the
/// framework documents — the management exceptions, the data port's authorization, constraint, not-found and
/// validation refusals — is expected: it gets a mapped title, the framework's own sentence as its detail, and
/// the fix this site offers. A cancellation, a closed browser or a torn-down circuit is dropped: nobody is left
/// to read a panel about it. Anything else is a fault: it is logged with its stack, and the operator reads a
/// generic sentence, because <see cref="Exception.Message"/> of an unexpected exception is written for a log
/// and may carry what a log holds.
/// </para>
/// <para>
/// <b>An <see cref="ObjectDisposedException"/> is dropped whoever raised it.</b> Reaching a screen's catch, it
/// is the circuit's service scope being disposed under an await — the screen is going too — and telling that
/// apart from a genuine use-after-dispose would need every screen to track its own disposal. It is still
/// logged, at debug, so the second case stays findable.
/// </para>
/// </remarks>
/// <param name="Title">The headline.</param>
/// <param name="Detail">The framework's sentence for a refusal; the generic sentence for a fault.</param>
/// <param name="Fix">What to do about it, when this site knows.</param>
/// <param name="Exception">What was thrown.</param>
/// <param name="IsFault">Whether nothing documents this exception, so it was logged and its message withheld.</param>
internal sealed record AdminProblem(string Title, string Detail, string? Fix, Exception Exception, bool IsFault)
{
    /// <summary>The headline of a fault, and of a panel given nothing better.</summary>
    public const string FaultTitle = "Something went wrong";

    /// <summary>What an operator reads instead of an unexpected exception's message.</summary>
    public const string FaultDetail =
        "The dashboard hit an error it did not expect. The details are in the server log; reload the screen to try again.";

    /// <summary>
    /// Whether the panel offers to sign out and in again — the way out of a session that outlived its account.
    /// </summary>
    public bool OffersSignOut => Exception is ManagementForbiddenException;

    /// <summary>Classifies an exception; <see langword="null"/> when it is dropped.</summary>
    /// <param name="exception">What a screen caught.</param>
    /// <param name="site">Where it was caught.</param>
    public static AdminProblem? From(Exception exception, ProblemSite site = ProblemSite.General)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (IsDropped(exception))
        {
            return null;
        }

        return RefusalTitle(exception, site) is { } title
            ? new AdminProblem(title, exception.Message, AdminProblemFixes.For(exception, site), exception, IsFault: false)
            : new AdminProblem(FaultTitle, FaultDetail, null, exception, IsFault: true);
    }

    /// <summary>Classifies an exception and logs it when it is a fault; <see langword="null"/> when it is dropped.</summary>
    /// <param name="exception">What a screen caught.</param>
    /// <param name="logger">The catching screen's logger.</param>
    /// <param name="site">Where it was caught.</param>
    public static AdminProblem? From(Exception exception, ILogger logger, ProblemSite site = ProblemSite.General)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var problem = From(exception, site);
        if (problem is null)
        {
            AdminProblemLog.Dropped(logger, exception.GetType().Name, exception);
        }
        else if (problem.IsFault)
        {
            AdminProblemLog.Fault(logger, exception);
        }

        return problem;
    }

    /// <summary>
    /// Classifies an exception a screen degrades over rather than shows — an empty search, a short id for a
    /// label — so a fault among them is still logged.
    /// </summary>
    /// <param name="exception">What a screen caught.</param>
    /// <param name="logger">The catching screen's logger.</param>
    /// <param name="site">Where it was caught.</param>
    public static void Absorb(Exception exception, ILogger logger, ProblemSite site = ProblemSite.General)
        => _ = From(exception, logger, site);

    private static bool IsDropped(Exception exception)
        => exception is OperationCanceledException or JSDisconnectedException or ObjectDisposedException;

    /// <summary>The headline of a documented refusal, or <see langword="null"/> for anything else.</summary>
    private static string? RefusalTitle(Exception exception, ProblemSite site) => exception switch
    {
        ManagementForbiddenException => "You are not allowed to do this",
        ManagementProjectNotFoundException => "That project does not exist",
        ManagementRevisionNotFoundException => "That revision does not exist",
        ManagementEscalationException => "This change needs a higher management level",
        ManagementSimulationException => "That policy question cannot be answered",
        ManagementRequestException => "The request was refused",
        DescriptorConcurrencyException => "Somebody applied a revision in between",
        DestructiveChangeNotAllowedException => "The plan discards data",
        DescriptorValidationException => "The descriptor is not valid",
        CelSyntaxException => "That expression does not parse",
        SecretShadowedException or SecretStoreReadOnlyException => "That secret cannot be saved here",
        AlvoAuthorizationException => "The rules did not admit this",
        AlvoConstraintViolationException => "A constraint refused the value",
        AlvoRecordNotFoundException => "That record is not there",
        AlvoPreconditionFailedException => "The record changed since it was read",
        AlvoIdempotencyConflictException => "That request was already made with different content",
        ArgumentException when IsDataSite(site) => "The values were refused",
        NotSupportedException when site == ProblemSite.People => "This deployment does not support that",
        _ => null,
    };

    /// <summary>
    /// Whether the data port is what was called, where an <see cref="ArgumentException"/> is its documented
    /// validation refusal rather than a bug in this assembly.
    /// </summary>
    private static bool IsDataSite(ProblemSite site)
        => site is ProblemSite.Records or ProblemSite.ScopedRecordsWithoutTenant or ProblemSite.RecordWrite;
}

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
            => "The descriptor does not satisfy schema/project.schema.json. The message names the pointer that failed.",
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

/// <summary>The dashboard's log lines — the first it writes.</summary>
internal static partial class AdminProblemLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "The admin dashboard caught an exception nothing documents; the operator was shown a generic error.")]
    public static partial void Fault(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "The admin dashboard dropped a {ExceptionType}: the screen or its circuit was already going.")]
    public static partial void Dropped(ILogger logger, string exceptionType, Exception exception);
}
