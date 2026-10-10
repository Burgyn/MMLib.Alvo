using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Events.Internal;

using System.Net;

namespace MMLib.Alvo.Events;

/// <summary>
/// Registers the event subsystem: the validated <see cref="AlvoEventOptions"/>, the delivery collaborators, and
/// the one background service that drains the outbox.
/// </summary>
internal static class EventsSetup
{
    /// <summary>
    /// Adds <see cref="AlvoEventOptions"/> (bound from its section and refused at startup), the webhook and mail
    /// delivery, and the outbox dispatcher as an <see cref="IHostedService"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="IEmailSender"/> is registered with <c>TryAddSingleton</c>, so the console provider is a
    /// default rather than a decision.</b> A host with a real SMTP or transactional-mail provider registers its
    /// own and takes mail over; nothing in this build ships one, which is why the console provider's own log line
    /// has to name itself a development provider.
    /// </para>
    /// <para>
    /// <b>The named <see cref="HttpClient"/> is registered here rather than created by the delivery</b>, so a host
    /// owns the handler, the timeout and any resilience policy by name
    /// (<c>WebhookDelivery.HttpClientName</c>). What it gets unless it says otherwise is secure by default: a
    /// primary handler that refuses non-public destinations at connect time and follows no redirect
    /// (<c>WebhookEgressGuard</c>), and a finite per-attempt timeout (<c>WebhookDelivery.AttemptTimeout</c>).
    /// </para>
    /// <para>
    /// <b>The named client carries none of <c>IHttpClientFactory</c>'s default logging handlers</b>
    /// (<c>RemoveAllLoggers</c>). They write the full request URI, path included, at Information under
    /// <c>System.Net.Http.HttpClient.{name}.*</c>, in the message, the structured state and a scope, and a
    /// webhook URL's path is routinely its only credential (#347). Removing them here, where the library
    /// registers the client, is what makes the guarantee hold in an embedded host whose logging configuration
    /// Alvo does not own; the delivery is still logged, by Alvo's own lines, under the endpoint's name. The
    /// removal is a configuration by name like any other, so it is order-sensitive: a host that wants transport
    /// logging back adds its own logger to the client <em>after</em> <c>AddAlvo</c>, and owns its redaction.
    /// </para>
    /// <para>
    /// <b><see cref="IAlvoEvents"/> is a singleton over the same <see cref="IOutboxStore"/> the dispatcher
    /// drains</b>, so a host publishing a custom application event and the framework emitting a data event
    /// reach one queue — the ordering, the attempt ceiling and the lease are then properties of the queue
    /// rather than of who wrote to it. It is registered here rather than beside <c>IAlvoData</c> because
    /// publishing is an event-subsystem concern: it never touches a driver, a schema or a policy.
    /// </para>
    /// <para>
    /// <b>The dispatcher is an <see cref="IHostedService"/> through <c>TryAddEnumerable</c></b>, so a host that
    /// called <c>AddAlvo</c> twice still drains the queue once — two dispatchers in one process would break
    /// per-entity-key ordering exactly as two replicas do. Registration order says nothing about when it runs:
    /// on .NET 10 <c>ExecuteAsync</c> runs entirely off the startup thread, so the readiness gate is an await on
    /// <c>AlvoBootState</c> inside the service itself.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add the event services to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    internal static IServiceCollection AddAlvoEvents(this IServiceCollection services)
    {
        services.AddOptions<AlvoEventOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<AlvoEventOptions>, AlvoEventOptionsConfiguration>(Create));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoEventOptions>, AlvoEventOptionsConfiguration>(Create));

        services.AddHttpClient(WebhookDelivery.HttpClientName).RemoveAllLoggers();
        services.Configure<HttpClientFactoryOptions>(WebhookDelivery.HttpClientName, GuardedByDefault);
        services.TryAddSingleton(new WebhookHostResolver(Dns.GetHostAddressesAsync));
        services.TryAddSingleton<WebhookEgressGuard>();
        services.TryAddSingleton<WebhookDelivery>();
        services.TryAddSingleton<IEmailSender, ConsoleEmailSender>();
        services.TryAddSingleton<EventActionExecutor>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAlvoEvents, AlvoEvents>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OutboxDispatcher>());

        return services;

        static AlvoEventOptionsConfiguration Create(IServiceProvider provider)
            => new(provider.GetService<IConfiguration>());
    }

    /// <summary>
    /// Puts the egress-guarded primary handler and the per-attempt timeout <em>first</em> in the named client's
    /// configuration, so they are the default every later configuration by name builds on or replaces.
    /// </summary>
    /// <remarks>
    /// <b>Inserted at the front rather than appended</b>, because <c>ConfigurePrimaryHttpMessageHandler</c> and
    /// <c>ConfigureHttpClient</c> are last-wins and a host may configure the client before or after it calls
    /// <c>AddAlvo</c>: appended, a host's own handler registered earlier would be silently overwritten by the
    /// guard. A host that replaces the primary handler therefore owns the egress policy — the documented way to
    /// route webhooks through an SSRF-aware egress proxy — and a test that substitutes the socket still does.
    /// </remarks>
    private static void GuardedByDefault(HttpClientFactoryOptions options)
    {
        options.HttpClientActions.Insert(0, client => client.Timeout = WebhookDelivery.AttemptTimeout);
        options.HttpMessageHandlerBuilderActions.Insert(0, builder =>
            builder.PrimaryHandler = builder.Services.GetRequiredService<WebhookEgressGuard>().CreateHandler());
    }
}
