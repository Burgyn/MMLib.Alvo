namespace MMLib.Alvo.Events;

/// <summary>
/// An event of Alvo's reserved <c>alvo</c> namespace — a command's completion, a schedule's or a data migration's run —
/// in the only shape a port that appends one accepts.
/// </summary>
/// <remarks>
/// <para>
/// <b>The framework-namespace twin of <see cref="AlvoCustomEvent"/>, and the reason a host cannot forge one.</b>
/// <see cref="AlvoEvent"/> is a public record with public initializers, so a port taking a bare envelope would let anyone
/// who resolves it append <c>alvo.schedule.nightly_roll</c> as the system and run every handler subscribed to it. This type
/// has no public constructor and no public factory: Alvo mints it, through the one door that stamps the caller's identity
/// and provenance, and a provider only reads <see cref="Envelope"/> to store it.
/// </para>
/// <para>
/// The cost is stated: a provider author outside this repository can read one but cannot build one, so a contract suite
/// that needs one comes from Alvo's own testing library.
/// </para>
/// </remarks>
public sealed class AlvoFrameworkEnvelope
{
    internal AlvoFrameworkEnvelope(AlvoEvent envelope) => Envelope = envelope;

    /// <summary>Gets the envelope to append, exactly as the queue must store it.</summary>
    public AlvoEvent Envelope { get; }
}
