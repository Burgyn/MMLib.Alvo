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

    /// <summary>
    /// The refusal for a filter, sort key or projection naming a field the caller cannot use — one the entity
    /// does not declare, <b>or</b> one <see cref="Rules.PolicyDecision.HiddenFields"/> hides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its sameness is the security property, so it cannot live in a literal per assembly.</b> §2.1's warning
    /// is that a filter over a hidden field leaks that field's value one comparison at a time
    /// (<c>salary.gt.&lt;x&gt;</c>, repeated, is a binary search), which is why "does not exist" and "exists and
    /// is hidden from you" must be one indistinguishable answer. A one-bit difference between the two —
    /// including a differently worded message from a different layer — <em>is</em> the oracle.
    /// </para>
    /// <para>
    /// It was written out three times before it lived here: both shipped <see cref="IAlvoData"/> implementations
    /// and PR3's query-string parser, hand-synced, pinned by no test. That is the same defect
    /// <see cref="WriteRejectedByPolicy"/> exists to prevent, in the one message where divergence is not a
    /// cosmetic inconsistency but a disclosure channel. The parser refuses before the port is reached, so the two
    /// are never even observed side by side — which is exactly why nothing would have caught them drifting.
    /// </para>
    /// <para>
    /// It names neither the field nor which of the two conditions applied. A field name is also the one
    /// caller-supplied string that reaches SQL as an identifier, so it is attacker-controlled text this framework
    /// will not echo into a response or a log.
    /// </para>
    /// </remarks>
    public const string QueryFieldUnavailable = "The query references a field that is not available to this caller.";

    /// <summary>
    /// The refusal for a row a batch names that this caller cannot act on — one that does not exist,
    /// <b>or</b> one the caller's <c>USING</c> predicate excludes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its sameness is the security property, exactly as <see cref="QueryFieldUnavailable"/>'s is</b>, and
    /// a batch is where it matters most. A single write already conflates the two into one
    /// <see cref="AlvoRecordNotFoundException"/>; a batch answers one refusal per row, so distinguishing them
    /// would make one request answer as many existence questions as it carries rows. That turns the oracle a
    /// single call closes into a bulk one — the same channel, multiplied by the batch size.
    /// </para>
    /// <para>
    /// It names neither the row nor which of the two conditions applied, and it lives here rather than in
    /// each driver because two drivers wording it differently is how the guarantee is lost with nothing to
    /// catch it. <see cref="Rules.PolicyDecision"/>'s own predicate is what decides visibility; this is only
    /// the sentence both answers share.
    /// </para>
    /// </remarks>
    public const string RowUnavailable = "The row is not available to this caller.";

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
