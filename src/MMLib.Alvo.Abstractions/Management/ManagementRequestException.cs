namespace MMLib.Alvo.Management;

/// <summary>
/// A management request is well-formed and still cannot be served — the caller asked for a combination this
/// surface does not offer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinct from a validation failure and from a refusal.</b> Nothing about the descriptor is wrong, no
/// policy denied anybody, and no precondition failed: the request itself names something that cannot be
/// done, such as an <c>Idempotency-Key</c> on a dry run, which appends nothing to replay. Over HTTP it is
/// the 422 that carries this message as its <c>detail</c>, so every construction names what was sent
/// <em>and</em> what to do instead.
/// </para>
/// <para>
/// <b>It is public because an in-process caller meets it.</b> The admin dashboard and the CLI call
/// <see cref="IAlvoManagement"/> directly, with no HTTP layer to turn the refusal into a status, so a type
/// they cannot name is a refusal they can only catch as <see cref="Exception"/>.
/// </para>
/// </remarks>
public sealed class ManagementRequestException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ManagementRequestException"/> class.</summary>
    public ManagementRequestException()
        : base("The request is well-formed and cannot be served as sent.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementRequestException"/> class.</summary>
    /// <param name="message">The message, naming what was sent and what to do instead.</param>
    public ManagementRequestException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementRequestException"/> class.</summary>
    /// <param name="message">The message, naming what was sent and what to do instead.</param>
    /// <param name="innerException">The refusal this one restates.</param>
    public ManagementRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
