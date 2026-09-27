using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Whether the host maps the set-password form's post (final branch review, item 15): only <c>MMLib.Alvo.Host</c> does
/// it for you, so an embedded host that registers people but maps no redemption must not be handed a link to a form
/// that posts nowhere.
/// </summary>
/// <remarks>
/// Each fact starts a real, bare host on a free loopback port: the routing table the dashboard reads is the one the
/// host's pipeline publishes as it starts, so a host that was only built would answer "nothing mapped" for every
/// host alike.
/// </remarks>
public sealed class SetPasswordRouteTests
{
    [Fact]
    public async Task A_host_that_maps_no_set_password_post_has_no_page()
    {
        await using var app = Host();
        app.MapGet(AlvoAdmin.SetPasswordEndpoint, () => Results.Ok());
        app.MapPost("/admin/something-else", () => Results.Ok());

        (await IsMappedAsync(app)).ShouldBeFalse("a GET at the path, or a post elsewhere, redeems nothing");
    }

    [Fact]
    public async Task A_host_that_maps_the_post_has_the_page()
    {
        await using var app = Host();
        app.MapPost(AlvoAdmin.SetPasswordEndpoint, () => Results.Ok());

        (await IsMappedAsync(app)).ShouldBeTrue();
    }

    /// <summary>A bare host on a free loopback port: routing, and whatever each fact maps.</summary>
    private static WebApplication Host()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        return builder.Build();
    }

    /// <summary>Starts <paramref name="app"/>, asks, and stops it.</summary>
    private static async Task<bool> IsMappedAsync(WebApplication app)
    {
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            return new SetPasswordRoute(app.Services).IsMapped;
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
