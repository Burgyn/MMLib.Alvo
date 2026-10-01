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

    /// <summary>
    /// <b>A redirect is a failed delivery, not a second destination.</b> Following it would hand the request to
    /// wherever a public endpoint's <c>Location</c> points — an internal host, or the same host over cleartext —
    /// without that destination ever passing the check the first one did.
    /// </summary>
    [Fact]
    public async Task A_redirect_is_not_followed_and_fails_the_delivery()
    {
        await using var elsewhere = await LoopbackReceiver.StartAsync(HttpStatusCode.NoContent);
        await using var redirecting = await LoopbackReceiver.StartAsync(
            HttpStatusCode.Found, location: elsewhere.Endpoint.Url);

        var failure = await Should.ThrowAsync<HttpRequestException>(
            () => Delivery().PostAsync(redirecting.Endpoint, AlvoEventJson.Write(SampleEvent()), Cancellation));

        failure.StatusCode.ShouldBe(HttpStatusCode.Found);
        redirecting.Method.ShouldBe(HttpMethods.Post);
        elsewhere.Method.ShouldBeNull("the redirect target must never have been contacted");
    }

    /// <summary>
    /// <b>A name that resolves to loopback is refused before a socket opens</b>, even though a real receiver is
    /// listening there — the rebinding case, which the apply-time URL check cannot see.
    /// </summary>
    [Fact]
    public async Task A_name_resolving_to_loopback_is_refused_at_connect_time()
    {
        await using var receiver = await LoopbackReceiver.StartAsync(HttpStatusCode.NoContent);

        var failure = await Should.ThrowAsync<HttpRequestException>(
            () => Delivery(RebindingToLoopback).PostAsync(
                Rebound(receiver.Endpoint), AlvoEventJson.Write(SampleEvent()), Cancellation));

        failure.ToString().ShouldContain(AlvoEventOptionsConfiguration.WebhookAllowedNetworksKey);
        failure.ToString().ShouldNotContain("/hook", Case.Sensitive, "the refusal must not name the URL's path");
        receiver.Method.ShouldBeNull("the refused destination must never have been contacted");
    }

    /// <summary>
    /// The host's opt-in is what lets a name reach a non-public network — here loopback, standing in for the
    /// private network an embedded host's internal service lives on.
    /// </summary>
    [Fact]
    public async Task An_allowed_network_lets_a_name_reach_a_non_public_receiver()
    {
        await using var receiver = await LoopbackReceiver.StartAsync(HttpStatusCode.NoContent);

        await Delivery(RebindingToLoopback, allowedNetwork: "127.0.0.0/8")
            .PostAsync(Rebound(receiver.Endpoint), AlvoEventJson.Write(SampleEvent()), Cancellation);

        receiver.Method.ShouldBe(HttpMethods.Post);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// The delivery exactly as <c>AddAlvo</c> wires it — the guarded handler and the timeout — so every fact
    /// here, the loopback carve-out included, is about the production client rather than a bare one.
    /// </summary>
    private static WebhookDelivery Delivery(WebhookHostResolver? resolver = null, string? allowedNetwork = null)
    {
        var services = new ServiceCollection();
        services.AddAlvoEvents();
        if (resolver is not null)
        {
            services.AddSingleton(resolver);
        }

        if (allowedNetwork is not null)
        {
            services.Configure<AlvoEventOptions>(options => options.WebhookAllowedNetworks.Add(allowedNetwork));
        }

        return services.BuildServiceProvider().GetRequiredService<WebhookDelivery>();
    }

    private const string ReboundHost = "hooks.rebind.example";

    private static Task<IPAddress[]> RebindingToLoopback(string host, CancellationToken cancellationToken) =>
        Task.FromResult(host == ReboundHost ? [IPAddress.Loopback] : Array.Empty<IPAddress>());

    private static WebhookTarget Rebound(WebhookTarget target) =>
        target with { Url = new UriBuilder(target.Url) { Host = ReboundHost }.Uri };

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

        internal static async Task<LoopbackReceiver> StartAsync(HttpStatusCode answer, Uri? location = null)
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
                if (location is not null)
                {
                    context.Response.Headers.Location = location.ToString();
                }
            });

            await app.StartAsync();
            receiver = new LoopbackReceiver(app, new WebhookTarget("loopback", new Uri($"{app.Urls.First()}/hook")));

            return receiver;
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
