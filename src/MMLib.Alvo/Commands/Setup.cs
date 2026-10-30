using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Commands;

/// <summary>Registers the command feature; called from <c>AddAlvo</c> and nowhere else.</summary>
internal static class CommandsSetup
{
    internal static IServiceCollection AddAlvoCommands(this IServiceCollection services)
    {
        services.TryAddSingleton<CommandRegistry>();
        services.TryAddSingleton<CommandAccessJudge>();
        services.TryAddSingleton<CommandEndpointFilter>();
        services.TryAddSingleton<IAlvoCommands, AlvoCommands>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, CommandGateCheck>());
        services.TryAddSingleton(provider => new CommandCompletion(provider.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton(provider => new CommandRunner(
            provider.GetService<ICommandLedger>(),
            provider.GetService<IAlvoTransactions>(),
            provider.GetRequiredService<IAlvoSessions>(),
            provider.GetRequiredService<CommandCompletion>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions,
            provider.GetRequiredService<ILogger<CommandRunner>>()));
        return services;
    }
}
