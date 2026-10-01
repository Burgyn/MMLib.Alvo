using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// What the named webhook client is when the host says nothing — egress-guarded, with a finite timeout — and
/// that a host which does say something wins, whichever side of <c>AddAlvo</c> it says it on.
/// </summary>
public sealed class WebhookClientRegistrationTests
{
    /// <summary>With no host configuration, the primary handler is the guard's.</summary>
    [Fact]
    public void By_default_the_primary_handler_is_the_egress_guarded_one()
    {
        var primary = PrimaryHandler(new ServiceCollection().AddAlvoEvents());

        var sockets = primary.ShouldBeOfType<SocketsHttpHandler>();
        sockets.AllowAutoRedirect.ShouldBeFalse();
        sockets.ConnectCallback.ShouldNotBeNull();
    }

    /// <summary>With no host configuration, an attempt times out after <see cref="WebhookDelivery.AttemptTimeout"/>.</summary>
    [Fact]
    public void By_default_an_attempt_has_a_finite_timeout()
    {
        using var client = Clients(new ServiceCollection().AddAlvoEvents())
            .CreateClient(WebhookDelivery.HttpClientName);

        client.Timeout.ShouldBe(WebhookDelivery.AttemptTimeout);
        client.Timeout.ShouldBeLessThan(new AlvoEventOptions().ClaimLease);
    }

    /// <summary>
    /// A host's own primary handler and timeout win even when registered <em>before</em> <c>AddAlvo</c> —
    /// a last-wins default appended after them would silently overwrite a host's egress proxy.
    /// </summary>
    [Fact]
    public void A_host_configuration_registered_before_the_framework_still_wins()
    {
        using var own = new HttpClientHandler();
        var services = new ServiceCollection();
        services.AddHttpClient(WebhookDelivery.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => own)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(3));
        services.AddAlvoEvents();

        PrimaryHandler(services).ShouldBeSameAs(own);
        Clients(services).CreateClient(WebhookDelivery.HttpClientName).Timeout.ShouldBe(TimeSpan.FromSeconds(3));
    }

    private static IHttpClientFactory Clients(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

    private static HttpMessageHandler PrimaryHandler(IServiceCollection services)
    {
        var handler = services.BuildServiceProvider()
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(WebhookDelivery.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        return handler;
    }
}
