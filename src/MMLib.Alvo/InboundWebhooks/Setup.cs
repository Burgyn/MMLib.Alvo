using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.InboundWebhooks.Internal;

namespace MMLib.Alvo.InboundWebhooks;

/// <summary>Registers inbound webhooks; called from <c>AddAlvo</c> and nowhere else.</summary>
internal static class InboundWebhooksSetup
{
    internal static IServiceCollection AddAlvoInboundWebhooks(this IServiceCollection services)
    {
        services.TryAddSingleton<WebhookReceivers>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LedgerSweeper>(provider => new LedgerSweeper(
            provider,
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<ILogger<LedgerSweeper>>())));
        return services;
    }
}
