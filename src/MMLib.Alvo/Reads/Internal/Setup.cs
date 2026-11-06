using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Reads.Internal;

/// <summary>Registers the core's read-extension runner — the one <see cref="IReadExtensionRunner"/> a relational driver resolves.</summary>
internal static class ReadsSetup
{
    internal static IServiceCollection AddAlvoReads(this IServiceCollection services)
    {
        services.TryAddSingleton<IReadExtensionRunner, ReadExtensionRunner>();
        return services;
    }
}
