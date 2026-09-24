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
/// the fix this site offers. A cancellation or a closed browser is dropped: nobody is left
/// to read a panel about it. Anything else is a fault: it is logged with its stack, and the operator reads a
/// generic sentence, because <see cref="Exception.Message"/> of an unexpected exception is written for a log
/// and may carry what a log holds.
/// </para>
/// <para>
/// <b>An <see cref="ObjectDisposedException"/> is a fault, not a drop.</b> It is often the circuit's service
/// scope being disposed under an await, but it is also what a genuine use-after-dispose throws, and the
/// classifier cannot tell the two apart without every screen tracking its own disposal. Logging the first at
/// error costs a line in a log; dropping the second would hide a bug.
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
            : Fault(exception);
    }

    /// <summary>The generic answer for an exception nothing documents.</summary>
    /// <param name="exception">What was thrown.</param>
    public static AdminProblem Fault(Exception exception)
        => new(FaultTitle, FaultDetail, null, exception, IsFault: true);

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
        => exception is OperationCanceledException or JSDisconnectedException;

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
        ArgumentException and not ArgumentNullException when IsDataSite(site) => "The values were refused",
        NotSupportedException when site == ProblemSite.People => "This deployment does not support that",
        _ => null,
    };

    /// <summary>
    /// Whether the data port is what was called, where an <see cref="ArgumentException"/> is its documented
    /// validation refusal rather than a bug in this assembly — except an <see cref="ArgumentNullException"/>,
    /// which the port documents as a defect in the caller.
    /// </summary>
    private static bool IsDataSite(ProblemSite site)
        => site is ProblemSite.Records or ProblemSite.ScopedRecordsWithoutTenant or ProblemSite.RecordWrite;
}

/// <summary>The dashboard's log lines — the first it writes.</summary>
internal static partial class AdminProblemLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "The admin dashboard caught an exception nothing documents; the operator was shown a generic error.")]
    public static partial void Fault(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "The admin dashboard dropped a {ExceptionType}: the operation was cancelled or the browser had gone.")]
    public static partial void Dropped(ILogger logger, string exceptionType, Exception exception);
}
