using System.Net;

namespace HelpDesk.Tests;

public sealed class OpsBoard : HttpMessageHandler
{
    private int _received;

    public int Received => Volatile.Read(ref _received);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _received);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
