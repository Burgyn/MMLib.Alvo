namespace MMLib.Alvo.Data;

/// <summary>
/// Thrown when <see cref="IAlvoData"/> refuses an operation outright: no policy allows it (a
/// missing rule, an unknown entity, or a tenant-scoped entity with no tenant in
/// <see cref="AlvoContext"/>), a candidate write fails its <c>WITH CHECK</c> predicate, or a
/// payload writes a field the policy marks read-only.
/// </summary>
/// <remarks>
/// The message never names the entity or a row id, and never discloses whether a specific row
/// exists — the same rule <c>IPolicyEngine</c>'s own deny reasons follow, so this exception
/// cannot become an oracle an attacker probes to learn what a caller cannot already see.
/// A read-only-field message may name the <em>field</em>, because a field name is authored into
/// the descriptor by whoever controls the backend, never supplied by the caller whose write was
/// rejected — naming it leaks nothing the descriptor didn't already declare.
/// </remarks>
public sealed class AlvoAuthorizationException : Exception
{
    private const string DefaultMessage = "The operation was not authorized.";

    /// <summary>
    /// The refusal every implementation raises when a candidate write fails its <c>WITH CHECK</c>
    /// predicate or the synthesized tenant scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the port because <b>three</b> places have to say it identically: both shipped
    /// <see cref="IAlvoData"/> implementations (the reference one and the EF one, which the adversarial
    /// suite holds to the same outcomes), and the HTTP layer's own fact that a refusal came from
    /// <em>policy</em> rather than from the API-key scope gate — a distinction the status code alone cannot
    /// carry, since both render 403. Three literals is how a reference implementation and a driver come to
    /// answer the same refusal with two different messages, and how a test asserting one of them quietly
    /// stops asserting anything.
    /// </para>
    /// <para>
    /// The wording names neither the entity, the row, nor which predicate refused — the same rule every
    /// message on this exception follows. It does say "policy", deliberately: that is the layer an operator
    /// has to go and look at, and it is already knowable from the descriptor they wrote.
    /// </para>
    /// </remarks>
    public const string WriteRejectedByPolicy = "The write was rejected by policy.";

    /// <summary>Initializes a new instance of the <see cref="AlvoAuthorizationException"/> class.</summary>
    public AlvoAuthorizationException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoAuthorizationException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public AlvoAuthorizationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoAuthorizationException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public AlvoAuthorizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
