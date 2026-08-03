using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MMLib.Alvo.Host.Tests.Integration;

/// <summary>
/// A real webhook endpoint on a loopback port: it records each delivery's body and answers <c>200 OK</c>, and it
/// can end the delivering process from inside the request instead of answering.
/// </summary>
/// <remarks>
/// <para>
/// <b>A real socket, not a substituted handler.</b> The host under test is a separate process, so there is no
/// container to install a primary <see cref="HttpMessageHandler"/> into — the delivery has to leave the process
/// and arrive somewhere. That is also what makes <see cref="OnFirstDelivery"/> possible at all: the kill happens
/// on the receiving side, after the request body is in hand and before any response is written, which is a
/// deterministic mid-action kill rather than a <c>Task.Delay</c>-timed guess at one.
/// </para>
/// <para>
/// <b>Every wait here is bounded.</b> <see cref="WaitForDeliveriesAsync"/> throws with what did arrive rather
/// than blocking forever, because a crash harness that hangs costs a whole CI job while a crash harness that
/// fails costs one red test.
/// </para>
/// </remarks>
internal sealed class LoopbackWebhookReceiver : IDisposable
{
    /// <summary>Starts a receiver on a free loopback port.</summary>
    /// <exception cref="HttpListenerException">Every attempted port was taken.</exception>
    /// <remarks>
    /// <see cref="HttpListener"/> has no "bind port zero" form, so a port is probed with a
    /// <see cref="TcpListener"/> and then claimed — a window in which something else can take it. The retry
    /// closes it in practice; the last attempt's own exception is what a reader sees if it does not, naming the
    /// port that was refused.
    /// </remarks>
    internal static LoopbackWebhookReceiver Start()
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = FreeLoopbackPort();
            try
            {
                return new LoopbackWebhookReceiver(port);
            }
            catch (HttpListenerException) when (attempt < BindAttempts)
            {
            }
        }
    }

    /// <summary>The URL a descriptor's webhook endpoint must point at to reach this receiver.</summary>
    internal string Url { get; }

    /// <summary>Every delivery's body, in arrival order.</summary>
    internal IReadOnlyList<string> Deliveries
    {
        get
        {
            lock (_gate)
            {
                return [.. _deliveries];
            }
        }
    }

    /// <summary>
    /// Runs once, on the next delivery, after its body has been recorded and before a response is written; then
    /// disarms itself.
    /// </summary>
    /// <remarks>
    /// Set to the harness's kill for a mid-action crash and cleared on every restart, so the child that has to
    /// survive the restart is not killed by the delivery it exists to make.
    /// </remarks>
    internal Action? OnFirstDelivery
    {
        get => Volatile.Read(ref _onFirstDelivery);
        set => Volatile.Write(ref _onFirstDelivery, value);
    }

    /// <summary>Waits until at least <paramref name="count"/> deliveries have arrived.</summary>
    /// <param name="count">How many deliveries to wait for.</param>
    /// <returns>Every delivery recorded so far.</returns>
    /// <exception cref="TimeoutException">Fewer than <paramref name="count"/> arrived inside the budget.</exception>
    internal async Task<IReadOnlyList<string>> WaitForDeliveriesAsync(int count)
    {
        var deadline = DateTimeOffset.UtcNow + _deliveryBudget;
        while (Deliveries.Count < count)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Waited {_deliveryBudget} for {count} webhook delivery/deliveries on {Url}; "
                    + $"{Deliveries.Count} arrived.");
            }

            await Task.Delay(_pollDelay).ConfigureAwait(false);
        }

        return Deliveries;
    }

    /// <inheritdoc/>
    public void Dispose() => _listener.Close();

    private LoopbackWebhookReceiver(int port)
    {
        Url = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/hooks/orders");
        _listener = new HttpListener();
        _listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
        _listener.Start();
        _ = Task.Run(AcceptForeverAsync);
    }

    /// <summary>Accepts requests until the listener is closed, which is the only way this loop ends.</summary>
    private async Task AcceptForeverAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            await HandleAsync(context).ConfigureAwait(false);
        }
    }

    /// <summary>Records one delivery, then either ends the deliverer or answers it.</summary>
    /// <param name="context">The accepted request.</param>
    private async Task HandleAsync(HttpListenerContext context)
    {
        var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
        lock (_gate)
        {
            _deliveries.Add(body);
        }

        if (Interlocked.Exchange(ref _onFirstDelivery, null) is { } end)
        {
            end();
        }

        Answer(context.Response);
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);

        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Answers <c>200 OK</c>, or gives up on answering at all when the deliverer is already gone.
    /// </summary>
    /// <remarks>
    /// After a mid-action kill there is nobody left to read the response, and the write fails — which is the
    /// expected outcome of this receiver's own kill rather than a failure of the fact. Swallowed only for the two
    /// shapes a dead peer produces.
    /// </remarks>
    /// <param name="response">The response for the delivery just recorded.</param>
    private static void Answer(HttpListenerResponse response)
    {
        try
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            response.Close();
        }
        catch (Exception failure) when (failure is HttpListenerException or ObjectDisposedException or IOException)
        {
            response.Abort();
        }
    }

    /// <summary>A loopback port nothing is listening on, as of this instant.</summary>
    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            probe.Start();

            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>How long <see cref="WaitForDeliveriesAsync"/> waits before failing loudly.</summary>
    /// <remarks>
    /// Generous against the measured cost of the whole harness — a publish plus two boots came to 6.0 s (spike
    /// Q9) — and finite, because the alternative to a bounded wait here is a CI job that burns its whole
    /// 20-minute budget on one stuck delivery.
    /// </remarks>
    private static readonly TimeSpan _deliveryBudget = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan _pollDelay = TimeSpan.FromMilliseconds(25);

    private const int BindAttempts = 4;

    private readonly HttpListener _listener;
    private readonly List<string> _deliveries = [];
    private readonly object _gate = new();
    private Action? _onFirstDelivery;
}
