using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// How often an open circuit is re-checked: thirty seconds, shortened only by the internal, test-only key
/// <c>Alvo:Admin:SessionRevalidationSeconds</c>, and never lengthened.
/// </summary>
public sealed class AlvoSessionRevalidationIntervalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_nothing_configured_an_open_circuit_is_re_checked_every_thirty_seconds()
    {
        await using var app = await StartAsync(configured: null);

        Provider(app).Every.ShouldBe(TimeSpan.FromSeconds(30));
        AlvoSessionRevalidationOptions.DefaultSeconds.ShouldBe(30);
    }

    [Fact]
    public async Task The_internal_key_shortens_the_interval()
    {
        await using var app = await StartAsync(configured: "2");

        Provider(app).Every.ShouldBe(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Anything but a whole number from 1 to 30 is refused at start: a longer interval would let an open tab outlive a
    /// disable or a password change for longer than the design says, so the key can only shorten it.
    /// </summary>
    /// <param name="configured">The configured value.</param>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("31")]
    [InlineData("3600")]
    [InlineData("two")]
    public async Task A_value_outside_one_to_thirty_is_refused_at_start(string configured)
    {
        var failure = await Should.ThrowAsync<OptionsValidationException>(async () =>
        {
            await using var app = await StartAsync(configured);
        });

        failure.Message.ShouldContain("Alvo__Admin__SessionRevalidationSeconds");
    }

    private static AlvoIdentityRevalidatingAuthenticationStateProvider Provider(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>()
            .ShouldBeOfType<AlvoIdentityRevalidatingAuthenticationStateProvider>();
    }

    private static async Task<WebApplication> StartAsync(string? configured)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (configured is not null)
        {
            builder.Configuration.AddInMemoryCollection(
                [new KeyValuePair<string, string?>(AlvoSessionRevalidationOptions.Key, configured)]);
        }

        builder.Services.AddAlvoIdentityCookieSignIn("/sign-in");
        var app = builder.Build();
        try
        {
            await app.StartAsync(Ct);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }
}
