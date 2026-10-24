using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>Adds registrations to the container, numbering them in call order.</summary>
internal static class ExtensionRegistrations
{
    internal static void AddHandler(
        IServiceCollection services,
        string id,
        HandlerSource source,
        string? when,
        Action<AlvoHandlerOptions>? configure,
        HandlerInvoker invoker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (when is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(when);
        }

        var options = new AlvoHandlerOptions();
        configure?.Invoke(options);
        Add(services, sequence => new HandlerRegistration(id, sequence, source, when, HandlerSettings.From(options), invoker));
    }

    internal static void Add(IServiceCollection services, Func<int, ExtensionRegistration> create)
    {
        var sequence = services.Count(descriptor => descriptor.ServiceType == typeof(ExtensionRegistration));
        services.AddSingleton(create(sequence));
    }
}
