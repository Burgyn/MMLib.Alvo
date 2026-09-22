using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Ai;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Management;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Alvo schema assistant.</summary>
public static class AlvoAiServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IAlvoAssistant"/> to <paramref name="services"/>.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// <b>A call of its own rather than part of <c>AddAlvo</c>.</b> The agent is the one package an embedded
    /// host can decline to install; folding it into the core's registration would make every host acquire an
    /// agent runtime and an OpenAI client to serve a Data API.
    /// </para>
    /// <para>
    /// Registering it does not configure a connection. A host that adds this and pins nothing has an
    /// assistant that answers "no AI connection is configured" — which is what the dashboard's settings
    /// screen exists to fix.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAlvoAi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAlvoAssistant>(provider => new AlvoAssistant(
            provider.GetRequiredService<IAlvoManagement>(),
            provider.GetRequiredService<IAiConnectionResolver>(),
            ChatClientFactory.For,
            provider.GetRequiredService<ILogger<AlvoAssistant>>()));

        return services;
    }
}
