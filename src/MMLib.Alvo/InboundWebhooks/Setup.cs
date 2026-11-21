using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.InboundWebhooks.Internal;
using MMLib.Alvo.Sweeping;

namespace MMLib.Alvo.InboundWebhooks;

/// <summary>Registers inbound webhooks; called from <c>AddAlvo</c> and nowhere else.</summary>
internal static class InboundWebhooksSetup
{
    internal static IServiceCollection AddAlvoInboundWebhooks(this IServiceCollection services)
    {
        services.TryAddSingleton<WebhookReceivers>();
        return services.AddAlvoSweepJob<InboundWebhookLedgerSweepJob>().AddAlvoSweepJob<CommandLedgerSweepJob>();
    }
}
