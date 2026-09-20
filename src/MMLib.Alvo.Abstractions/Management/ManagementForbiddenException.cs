namespace MMLib.Alvo.Management;

/// <summary>
/// The caller is not admitted to this operation: the level the project's <c>access</c> block resolves them
/// to does not reach the level the operation needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Public, and in Abstractions, because every member of <see cref="IAlvoManagement"/> raises it.</b> The
/// level gate used to be the HTTP adapter's endpoint filter alone, on the reading that "which level an
/// operation needs is decided at composition". A dashboard resolves <em>one</em> registered
/// <see cref="IAlvoManagement"/> and serves <em>many</em> humans through it, so which level <em>this
/// caller</em> holds is a per-request question — exactly like the <c>access</c>-block comparison
/// <see cref="ManagementEscalationException"/> answers. Both transports now meet the same gate, and an
/// in-process caller needs the type to tell "you may not" from "no such project".
/// </para>
/// <para>
/// <b>It names neither the level held nor the level needed</b>, and its message is identical for every
/// caller, every project and every operation. That is <c>ProblemResultFactory.ManagementForbidden</c>'s
/// anti-fingerprinting rule: a refusal that named the requirement would let a caller map the whole level
/// table one call at a time. There is nothing to put in a field that would not end up in a body.
/// </para>
/// <para>
/// <b>A caller who published nobody is refused by this type, not by an authentication failure.</b> An
/// unattended in-process call is the anonymous caller, and the anonymous caller reaches no level — the same
/// fail-closed reading the gate applies everywhere else.
/// </para>
/// </remarks>
public sealed class ManagementForbiddenException : Exception
{
    /// <summary>The refusal every caller gets, whatever they asked for and whatever they hold.</summary>
    private const string Refusal =
        "This caller is not admitted to the project's management surface. The project's access block "
        + "decides who is; ask whoever administers it.";

    /// <summary>Initializes a new instance of the <see cref="ManagementForbiddenException"/> class.</summary>
    public ManagementForbiddenException()
        : base(Refusal)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementForbiddenException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public ManagementForbiddenException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementForbiddenException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ManagementForbiddenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
