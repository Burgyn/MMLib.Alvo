using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Events.Internal;

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
    /// (<c>WebhookDelivery.HttpClientName</c>) without the framework owning any of them.
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

        services.AddHttpClient(WebhookDelivery.HttpClientName);
        services.TryAddSingleton<WebhookDelivery>();
        services.TryAddSingleton<IEmailSender, ConsoleEmailSender>();
        services.TryAddSingleton<EventActionExecutor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OutboxDispatcher>());

        return services;

        static AlvoEventOptionsConfiguration Create(IServiceProvider provider)
            => new(provider.GetService<IConfiguration>());
    }
}
