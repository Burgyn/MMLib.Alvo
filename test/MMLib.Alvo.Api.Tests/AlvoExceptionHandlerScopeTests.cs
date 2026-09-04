using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Rules;
using System.Net;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// What <c>AddAlvoProblemDetails()</c> costs an embedded host that also registers an
/// <see cref="IExceptionHandler"/> of its own — measured over a running pipeline, because the answer is
/// "which handler ran", and that is invisible from either handler alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this pins used to be silent in both directions.</b> Alvo's handler answered every exception
/// and returned <see langword="true"/>; the framework stops at the first handler that claims a failure, so a
/// host's handler registered after <c>AddAlvoProblemDetails()</c> never ran — not for Alvo's endpoints, and
/// not for the host's own either. Its error contract disappeared from production 500s with no build error and
/// no failing test, because nothing anywhere composed the two registrations.
/// </para>
/// <para>
/// <b>The pipeline is built here rather than taken from <see cref="AlvoApiWorld"/>, and that is the point of
/// the fixture.</b> What is under test is the interaction between two registrations and two endpoints — one
/// carrying <see cref="DataApiOperationMetadata"/> and one not — and the world maps only Alvo's own routes,
/// so it cannot express "the host's endpoint failed". No descriptor and no database are involved for the same
/// reason: nothing here reaches the data layer. The link back to the real thing is
/// <c>DataApiEndpointTests</c>' fact that <em>every</em> generated route carries the marker this fixture
/// attaches by hand, and <c>ProblemDetailsTests</c>' #119 facts over the real Data API.
/// </para>
/// </remarks>
public class AlvoExceptionHandlerScopeTests
{
    /// <summary>The body the host's own handler writes, so a fact can tell whose document answered.</summary>
    private const string HostDocument = "the host's own error contract";

    /// <summary>
    /// A failure on the host's own endpoint reaches the host's own handler, even though Alvo's was registered
    /// first.
    /// </summary>
    [Fact]
    public async Task A_hosts_own_handler_registered_after_alvos_answers_the_hosts_own_endpoint()
    {
        await using var pipeline = await Pipeline.StartAsync();

        using var response = await pipeline.GetAsync("/the-hosts-own/boom");

        pipeline.HostHandlerReached.ShouldBeTrue(
            "an IExceptionHandler registered after AddAlvoProblemDetails() must still run for the host's own "
            + "endpoints, or adding Alvo deleted the host's error contract");
        (await response.ReadTextAsync()).ShouldBe(HostDocument);
    }

    /// <summary>
    /// The control, and #119 at the same seam: a failure on one of Alvo's generated endpoints is still Alvo's
    /// to answer, and the host's handler does not get it.
    /// </summary>
    /// <remarks>
    /// Without this half, a handler that declined <em>everything</em> would satisfy the fact above while
    /// giving back the defect #119 fixed — the framework's RFC 9110 status-code URI in the one member an agent
    /// branches on. The two facts differ in exactly one thing: whether the failing endpoint carries
    /// <see cref="DataApiOperationMetadata"/>.
    /// </remarks>
    [Fact]
    public async Task Alvos_handler_still_answers_a_failure_from_one_of_alvos_endpoints()
    {
        await using var pipeline = await Pipeline.StartAsync();

        using var response = await pipeline.GetAsync("/alvos-own/boom");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Internal);
        pipeline.HostHandlerReached.ShouldBeFalse(
            "Alvo answered its own endpoint's failure, so the chain must stop at Alvo's handler");
    }

    /// <summary>
    /// One pipeline: Alvo's handler, then the host's, over one endpoint of each kind.
    /// </summary>
    private sealed class Pipeline(WebApplication app, HostHandlerProbe probe) : IAsyncDisposable
    {
        internal static async Task<Pipeline> StartAsync()
        {
            var probe = new HostHandlerProbe();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();

            builder.Services.AddAlvoProblemDetails();
            builder.Services.AddSingleton(probe);

            // Registered *after* Alvo's, which is the order that used to lose it entirely.
            builder.Services.AddExceptionHandler<HostExceptionHandler>();

            var app = builder.Build();
            app.UseExceptionHandler();
            app.MapGet("/the-hosts-own/boom", void () => throw new InvalidOperationException("the host's own bug"));
            app.MapGet("/alvos-own/boom", void () => throw new InvalidOperationException("a broken invariant"))
                .WithMetadata(new DataApiOperationMetadata("owners", DataApiEndpointKind.List));

            await app.StartAsync(TestContext.Current.CancellationToken);

            return new Pipeline(app, probe);
        }

        internal bool HostHandlerReached => probe.Reached;

        internal Task<HttpResponseMessage> GetAsync(string path) =>
            app.GetTestClient().GetAsync(path, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    /// <summary>Whether the host's handler ran — the whole observable this suite is about.</summary>
    private sealed class HostHandlerProbe
    {
        internal bool Reached { get; set; }
    }

    /// <summary>An embedded host's own error rendering, in the shape a host really registers it.</summary>
    private sealed class HostExceptionHandler(HostHandlerProbe probe) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            probe.Reached = true;
            await httpContext.Response.WriteAsync(HostDocument, cancellationToken);

            return true;
        }
    }
}
