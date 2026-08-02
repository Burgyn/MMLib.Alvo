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
