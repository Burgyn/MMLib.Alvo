using MMLib.Alvo.Events;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// One timer to arm, as <see cref="ITimerStore.UpsertAsync"/> takes it: where and when it fires, the custom message it
/// fires, and the provenance the fired event carries.
/// </summary>
/// <remarks>
/// <b>Built only by Alvo's scheduler, and read by a provider.</b> Its provenance — the chain depth a fired event resumes
/// from — is what stops a timer that re-arms itself from looping forever, so a caller who could build one could reset it.
/// The message is an <see cref="AlvoCustomEvent"/>, so a timer never fires an <c>alvo.</c> event.
/// </remarks>
public sealed class TimerEntry
{
    private const int MaxKeyLength = 200;

    private TimerEntry(string scope, string key, DateTimeOffset dueAt, string messageType, string payload, string provenance)
    {
        Scope = scope;
        Key = key;
        DueAt = dueAt;
        MessageType = messageType;
        Payload = payload;
        Provenance = provenance;
    }

    /// <summary>Gets the tenant id text, or <c>""</c> for none; a key is unique within its scope.</summary>
    public string Scope { get; }

    /// <summary>Gets the timer's key: 1–200 characters, none of them a control character.</summary>
    public string Key { get; }

    /// <summary>Gets when the timer is due, in UTC.</summary>
    public DateTimeOffset DueAt { get; }

    /// <summary>Gets the custom event type the timer fires.</summary>
    public string MessageType { get; }

    /// <summary>Gets the message, as <see cref="AlvoEventJson"/> writes it; opaque to the store.</summary>
    /// <remarks>
    /// It is the arming caller's whole envelope, but only its data is meant to be read back: the fired event is rebuilt
    /// around it, so the stored actor, claims, id and time mean nothing and are not to be shown as if they did.
    /// </remarks>
    public string Payload { get; }

    /// <summary>Gets the provenance the fired event resumes from; opaque to the store.</summary>
    public string Provenance { get; }

    /// <summary>Builds a timer, refusing a malformed key.</summary>
    /// <param name="scope">The tenant id text, or <c>""</c> for none.</param>
    /// <param name="key">The timer's key.</param>
    /// <param name="dueAt">When it is due; stored as UTC.</param>
    /// <param name="message">The custom event it fires.</param>
    /// <param name="provenance">The provenance the fired event resumes from.</param>
    /// <exception cref="ArgumentException">The key is empty, longer than 200 characters, or holds a control character.</exception>
    internal static TimerEntry Of(string scope, string key, DateTimeOffset dueAt, AlvoCustomEvent message, AlvoProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(provenance);
        EnsureKey(key);

        return new TimerEntry(
            scope, key, dueAt.ToUniversalTime(), message.Envelope.Type, AlvoEventJson.Write(message.Envelope), TimerProvenance.Write(provenance));
    }

    private static void EnsureKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is 0 or > MaxKeyLength || key.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A timer key is 1-{MaxKeyLength} characters with no control characters; '{key}' is not.", nameof(key));
        }
    }
}
