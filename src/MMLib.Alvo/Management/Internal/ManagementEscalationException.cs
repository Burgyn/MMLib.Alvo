namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// An apply that would change <b>who may reach the project</b>, from a caller the project does not admit
/// as an administrator.
/// </summary>
/// <remarks>
/// <para>
/// <b>An exception rather than an early return, so one place decides what a refusal looks like.</b>
/// <c>ManagementEndpoints.Answer</c> already maps every management refusal onto
/// <c>ProblemResultFactory</c>'s one catalogue; a handler that returned its own result beside it would be
/// the second authority that arrangement exists to prevent.
/// </para>
/// <para>
/// <b>It carries nothing.</b> Not the level the caller holds, not the block they sent, not the block they
/// would have replaced — the rendered 403 is the same for every caller and every project, which is
/// <c>ProblemResultFactory.ManagementForbidden</c>'s own anti-fingerprinting rule. There is nothing to put
/// in a field that would not end up in a body.
/// </para>
/// </remarks>
internal sealed class ManagementEscalationException : Exception
{
    /// <summary>The refusal every caller gets, whatever they sent and whatever they hold.</summary>
    private const string Refusal =
        "This apply would change the project's access block, which is reserved to an administrator.";

    /// <summary>Initializes a new instance of the <see cref="ManagementEscalationException"/> class.</summary>
    internal ManagementEscalationException()
        : base(Refusal)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementEscalationException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    internal ManagementEscalationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementEscalationException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    internal ManagementEscalationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
