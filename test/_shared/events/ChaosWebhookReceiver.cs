using MMLib.Alvo.Events;

using System.Net;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The receiver the chaos criterion posts to: it records the <em>event id</em> of every delivery it accepts and
/// refuses every <c>failEvery</c>-th attempt with a <c>503</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It records ids, not a count.</b> A count cannot tell ten thousand deliveries of one event from one
/// delivery each, so "no event was lost" would be satisfied by a pump that redelivered a single entry forever.
/// The id comes out of the delivered body — the envelope the receiver actually got — rather than from anything
/// the test knew beforehand, so a body carrying the wrong event fails the set comparison rather than passing it.
/// </para>
/// <para>
/// <b>The refusal is a <c>503</c> and not a thrown exception.</b> That is the transient failure a real endpoint
/// produces, it travels the shipped path (<c>EnsureSuccessStatusCode</c> → <c>HttpRequestException</c> → the
/// dispatcher releases the entry → the next claim delivers it again), and it keeps the criterion about the
/// outbox's own release-and-retry rather than about a handler that threw before the delivery existed.
/// </para>
/// <para>
/// <b>Refused ids are kept apart from accepted ones</b>, because the two answer different questions: the
/// accepted set is "every event arrived", and the refused set is "the chaos really happened, and every event it
/// hit arrived anyway".
/// </para>
/// </remarks>
/// <param name="failEvery">Every n-th attempt is refused; the criterion asserts the refusals really happened.</param>
internal sealed class ChaosWebhookReceiver(int failEvery) : HttpMessageHandler
{
    /// <summary>Every delivery attempt this receiver saw, accepted or refused.</summary>
    internal int Attempts
    {
        get { lock (_gate) { return _attempts; } }
    }

    /// <summary>How many deliveries it accepted, counting a redelivered event once per acceptance.</summary>
    internal int Accepted
    {
        get { lock (_gate) { return _accepted; } }
    }

    /// <summary>The distinct events it accepted at least one delivery of.</summary>
    internal IReadOnlySet<Guid> AcceptedIds
    {
        get { lock (_gate) { return _acceptedIds.ToHashSet(); } }
    }

    /// <summary>How many attempts it refused.</summary>
    internal int Refused
    {
        get { lock (_gate) { return _refused; } }
    }

    /// <summary>The distinct events at least one delivery of which was refused.</summary>
    internal IReadOnlySet<Guid> RefusedIds
    {
        get { lock (_gate) { return _refusedIds.ToHashSet(); } }
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = EventIdOf(await BodyOfAsync(request, cancellationToken).ConfigureAwait(false));

        return new HttpResponseMessage(Record(id));
    }

    /// <summary>
    /// Records one attempt and answers the status it gets, under the one lock, so the every-n-th decision and
    /// the two sets cannot disagree about which attempt this was.
    /// </summary>
    private HttpStatusCode Record(Guid id)
    {
        lock (_gate)
        {
            _attempts++;
            if (_attempts % failEvery == 0)
            {
                _refused++;
                _refusedIds.Add(id);

                return HttpStatusCode.ServiceUnavailable;
            }

            _accepted++;
            _acceptedIds.Add(id);

            return HttpStatusCode.OK;
        }
    }

    private static async Task<string> BodyOfAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The delivered envelope's own <c>id</c>, which is the only thing that makes this a set comparison.
    /// </summary>
    /// <remarks>
    /// Read with <see cref="JsonDocument"/> rather than through <c>AlvoEventJson.Read</c> on purpose: the
    /// criterion is about which events arrived, and a receiver that refused a body the envelope reader cannot
    /// parse would report a lost event where the real defect is a malformed body. A body with no id at all is a
    /// defect either way, and <see cref="Guid.Empty"/> is what surfaces it — it belongs to no seeded event, so
    /// the set comparison fails and names the count.
    /// </remarks>
    private static Guid EventIdOf(string body)
    {
        if (body.Length == 0)
        {
            return Guid.Empty;
        }

        using var document = JsonDocument.Parse(body);

        return document.RootElement.TryGetProperty(AlvoEventAttributes.Id, out var id)
            && id.TryGetGuid(out var value)
                ? value
                : Guid.Empty;
    }

    private readonly HashSet<Guid> _acceptedIds = [];

    private readonly HashSet<Guid> _refusedIds = [];

    private readonly object _gate = new();

    private int _attempts;

    private int _accepted;

    private int _refused;
}
