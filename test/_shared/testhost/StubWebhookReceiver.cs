using System.Collections.Concurrent;
using System.Net;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// A webhook receiver that keeps every body it was sent and answers 200 — and, when told to, writes back first
/// (spec 2026-10-14 Appendix A R-12).
/// </summary>
/// <remarks>
/// <para>
/// <b>The write-back runs as an out-of-process receiver's would: with the execution context not flowing.</b> In process,
/// the stub runs inside the dispatcher's async flow, and from HX-03 on a dispatcher sets the ambient chain depth
/// (<c>AlvoFlow</c>, spec 2026-10-14 §4.6: envelope depth + 1) for each run. A write-back inheriting it would climb one
/// level per round and be refused at depth 9, the drain would go quiet, and the loop fact would fail although a real
/// receiver — writing back over HTTP, at depth 0 — loops forever. Suppressing the flow keeps the fact about the real
/// loop; do not "fix" a failing loop fact in HX-03 by deleting this.
/// </para>
/// <para>
/// <b>It refuses to work once disposed</b>, so a host that disposed a caller's handler is caught by the fact that reuses
/// one across hosts rather than passing because a disposed handler kept answering.
/// </para>
/// </remarks>
internal sealed class StubWebhookReceiver : HttpMessageHandler
{
    private readonly ConcurrentQueue<string> _deliveries = new();

    /// <summary>Gets or sets what the receiver does on every delivery before it answers.</summary>
    internal Func<Task>? OnDelivery { get; set; }

    /// <summary>Gets every body received, in arrival order.</summary>
    internal IReadOnlyList<string> Deliveries => [.. _deliveries];

    /// <summary>Gets whether anything disposed this receiver.</summary>
    internal bool IsDisposed { get; private set; }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        _deliveries.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (OnDelivery is { } onDelivery)
        {
            await OutOfProcess(onDelivery);
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }

    private static Task OutOfProcess(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work);
        }
    }
}
