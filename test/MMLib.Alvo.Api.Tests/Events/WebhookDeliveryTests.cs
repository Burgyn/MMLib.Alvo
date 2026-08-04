using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

using System.Net;

namespace MMLib.Alvo.Api.Tests.Events;

/// <summary>
/// What a webhook delivery looks like <b>on the wire</b>: one real socket, one real HTTP request, one real
/// response status.
/// </summary>
/// <remarks>
/// <para>
/// The unit suite in <c>MMLib.Alvo.Tests</c> stubs the transport, which is right for the rendering and
/// retry-contract facts and structurally unable to answer these two: a stubbed
/// <c>HttpMessageHandler</c> is handed an <c>HttpRequestMessage</c> that never went through content
/// negotiation, header serialization or a status-code round trip, so "the method was POST and the content type
/// was <c>application/json</c>" is a claim about the framework's own plumbing that only a real request can
/// settle.
/// </para>
/// <para>
/// A loopback <c>WebApplication</c> on an ephemeral port rather than <c>HttpListener</c>: this project already
/// carries the ASP.NET Core framework reference, and a receiver written as middleware records whatever
/// arrives — including a method or content type nobody expected — where a mapped endpoint would answer 404
/// and hide it.
/// </para>
/// </remarks>
public class WebhookDeliveryTests
{
    /// <summary>
    /// A delivery arrives as a <c>POST</c> of <c>application/json</c> carrying the whole envelope.
    /// </summary>
    [Fact]
    public async Task A_delivery_arrives_as_a_post_of_json_carrying_the_envelope()
    {
        await using var receiver = await LoopbackReceiver.StartAsync(HttpStatusCode.NoContent);
        var @event = SampleEvent();

        await Delivery().PostAsync(receiver.Endpoint, AlvoEventJson.Write(@event), Cancellation);

        receiver.Method.ShouldBe(HttpMethods.Post);
        MediaTypeHeaderValue.Parse(receiver.ContentType).MediaType.ShouldBe(AlvoEvent.DataContentType);
        AlvoEventJson.Read(receiver.Body!).ShouldBe(@event);
    }

    /// <summary>
    /// A real refusal over a real socket throws, which is what makes the dispatcher's release-and-retry the
    /// thing that delivers at-least-once.
    /// </summary>
    [Fact]
    public async Task A_delivery_the_endpoint_refuses_throws_over_a_real_socket()
    {
        await using var receiver = await LoopbackReceiver.StartAsync(HttpStatusCode.InternalServerError);

        var failure = await Should.ThrowAsync<HttpRequestException>(
            () => Delivery().PostAsync(receiver.Endpoint, AlvoEventJson.Write(SampleEvent()), Cancellation));

        failure.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static WebhookDelivery Delivery()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(WebhookDelivery.HttpClientName);

        return new WebhookDelivery(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());
    }

    private static AlvoEvent SampleEvent() => new()
    {
        Id = Guid.Parse("019000bb-0000-7000-8000-0000000000d1"),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.vehicles.updated",
        Time = new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero),
        Subject = "vehicles/019000bb-0000-7000-8000-0000000000ff",
        PartitionKey = "vehicles:019000bb-0000-7000-8000-0000000000ff",
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = "019000bb-0000-7000-8000-0000000000c0",
        Data = new AlvoEventData
        {
            Record = new AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["plate"] = "BA-123XY" }),
            Changed = ["plate"],
        },
    };

    /// <summary>
    /// A real HTTP server on an ephemeral loopback port that records the one request it receives and answers a
    /// fixed status.
    /// </summary>
    private sealed class LoopbackReceiver : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private LoopbackReceiver(WebApplication app, WebhookTarget endpoint)
        {
            _app = app;
            Endpoint = endpoint;
        }

        /// <summary>
        /// The endpoint a delivery posts to, in the resolved shape the hook compiler hands the delivery.
        /// </summary>
        /// <remarks>
        /// Cleartext over a loopback address, which is the one non-HTTPS shape
        /// <c>AfterHookCompiler</c> accepts and the reason that carve-out exists: there is no network to observe.
        /// </remarks>
        internal WebhookTarget Endpoint { get; }

        /// <summary>The method of the request that arrived.</summary>
        internal string? Method { get; private set; }

        /// <summary>The content type of the request that arrived.</summary>
        internal string? ContentType { get; private set; }

        /// <summary>The body of the request that arrived.</summary>
        internal string? Body { get; private set; }

        internal static async Task<LoopbackReceiver> StartAsync(HttpStatusCode answer)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            var app = builder.Build();
            LoopbackReceiver? receiver = null;
            app.Run(async context =>
            {
                receiver!.Method = context.Request.Method;
                receiver.ContentType = context.Request.ContentType;
                receiver.Body = await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
                context.Response.StatusCode = (int)answer;
            });

            await app.StartAsync();
            receiver = new LoopbackReceiver(app, new WebhookTarget("loopback", new Uri($"{app.Urls.First()}/hook")));

            return receiver;
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
