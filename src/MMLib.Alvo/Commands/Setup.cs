using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Commands.Internal;

namespace MMLib.Alvo.Commands;

/// <summary>Registers the command feature; called from <c>AddAlvo</c> and nowhere else.</summary>
internal static class CommandsSetup
{
    internal static IServiceCollection AddAlvoCommands(this IServiceCollection services)
    {
        services.TryAddSingleton<CommandRegistry>();
        services.TryAddSingleton<CommandAccessJudge>();
        return services;
    }
}
