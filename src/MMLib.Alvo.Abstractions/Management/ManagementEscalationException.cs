namespace MMLib.Alvo.Management;

/// <summary>
/// A write that would change <b>who may reach the project</b>, from a caller the project does not admit
/// as an administrator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Public, and in Abstractions, because <see cref="IAlvoManagement"/> raises it.</b> It is thrown out of
/// <see cref="IAlvoManagement.ApplyDescriptorAsync"/> and <see cref="IAlvoManagement.RollbackAsync"/>, which
/// an embedded host calls directly — a dashboard that wants to render "you may not change the access block"
/// differently from "no such project" has to be able to write the catch clause. Every other refusal those
/// two members raise is public and lives here for the same reason.
/// </para>
/// <para>
/// <b>An exception rather than an early return, so one place decides what a refusal looks like.</b>
/// Mapping it onto <c>ManagementEndpoints.Answer</c>'s one catalogue of
/// <c>ProblemResultFactory</c> refusals is now only half its job: the HTTP adapter renders it as a
/// <c>403</c>, and an in-process caller catches the type itself.
/// </para>
/// <para>
/// <b>It carries nothing.</b> Not the level the caller holds, not the block they sent, not the block they
/// would have replaced — the rendered 403 is the same for every caller and every project, which is
/// <c>ProblemResultFactory.ManagementAccessChangeForbidden</c>'s own anti-fingerprinting rule. There is
/// nothing to put in a field that would not end up in a body.
/// </para>
/// </remarks>
public sealed class ManagementEscalationException : Exception
{
    /// <summary>The refusal every caller gets, whatever they sent and whatever they hold.</summary>
    private const string Refusal =
        "This apply would change the project's access block, which is reserved to an administrator.";

    /// <summary>Initializes a new instance of the <see cref="ManagementEscalationException"/> class.</summary>
    public ManagementEscalationException()
        : base(Refusal)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementEscalationException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public ManagementEscalationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementEscalationException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ManagementEscalationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
