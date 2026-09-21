using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// The one call a management route makes to carry the gate.
/// </summary>
/// <remarks>
/// <b>Through a real route rather than by constructing the filter.</b> The filter's own facts prove what
/// the gate decides; this proves the extension actually attaches it and that its collaborators resolve
/// from the application's own container — the half that a filter the container cannot build would fail,
/// silently, on the first management request rather than here. The endpoint's request delegate is invoked
/// directly instead of over a socket, which is what keeps this in ring0.
/// </remarks>
public class ManagementAccessRouteBuilderExtensionsTests
{
    [Fact]
    public async Task A_gated_route_refuses_a_caller_who_matches_no_level()
    {
        var probe = await CallAsync(
            ManagementOperation.GetSchema,
            ManagementCallers.Caller("sales"),
            ManagementCallers.Levels(admin: "'manager' in @user.roles"));

        probe.Status.ShouldBe(StatusCodes.Status403Forbidden);
        probe.HandlerRan.ShouldBeFalse("the gate has to refuse before the route's own delegate runs");
    }

    [Fact]
    public async Task A_gated_route_admits_a_caller_at_the_required_level()
    {
        var probe = await CallAsync(
            ManagementOperation.GetSchema,
            ManagementCallers.Caller("manager"),
            ManagementCallers.Levels(admin: "'manager' in @user.roles"));

        probe.Status.ShouldBe(StatusCodes.Status200OK);
        probe.HandlerRan.ShouldBeTrue();
    }

    /// <summary>What one request through a gated route did.</summary>
    /// <param name="Status">The status the response carries.</param>
    /// <param name="HandlerRan">Whether the route's own delegate was reached.</param>
    private sealed record Probe(int Status, bool HandlerRan);

    /// <summary>Builds a host with one gated route and sends one request through it.</summary>
    /// <param name="operation">The operation the route is gated on.</param>
    /// <param name="caller">The caller the request runs as.</param>
    /// <param name="levels">The <c>access</c> block the host applied.</param>
    private static async Task<Probe> CallAsync(
        ManagementOperation operation, AlvoContext caller, Access levels)
    {
        var handlerRan = false;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IAlvoContextAccessor>(new PublishedCaller(caller));
        builder.Services.AddSingleton(ManagementCallers.Primed(levels));
        builder.Services.AddAlvo();

        await using var app = builder.Build();
        app.MapGet("/probe", () => { handlerRan = true; return Results.Ok("reached"); })
            .RequireAlvoManagementAccess(operation);

        return new Probe(await StatusOfAsync(app), handlerRan);
    }

    /// <summary>Invokes the single mapped endpoint's request delegate and reports the status it wrote.</summary>
    /// <param name="app">The host holding exactly one mapped route.</param>
    private static async Task<int> StatusOfAsync(WebApplication app)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.Single().Endpoints.Single();
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            Response = { Body = new MemoryStream() },
        };

        await endpoint.RequestDelegate!(context);
        return context.Response.StatusCode;
    }

    /// <summary>The caller every request through this host runs as.</summary>
    /// <param name="caller">The caller to publish.</param>
    private sealed class PublishedCaller(AlvoContext caller) : IAlvoContextAccessor
    {
        /// <inheritdoc/>
        public AlvoPrincipal? Principal { get; set; } = new()
        {
            Context = caller,
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "probe",
        };
    }
}
