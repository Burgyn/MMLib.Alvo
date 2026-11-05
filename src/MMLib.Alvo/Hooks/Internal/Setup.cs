using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>Registers the core's write-hook runner — the one <see cref="IWriteExtensionRunner"/> a relational driver resolves.</summary>
internal static class HooksSetup
{
    internal static IServiceCollection AddAlvoWriteHooks(this IServiceCollection services)
    {
        services.TryAddSingleton<IWriteExtensionRunner, WriteExtensionRunner>();
        return services;
    }
}
