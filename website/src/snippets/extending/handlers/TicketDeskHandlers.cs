using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace TicketDesk;

public static class TicketDeskHandlers
{
    public static IAlvoBuilder AddTicketDeskHandlers(this IAlvoBuilder alvo) => alvo
        .Entity("tickets", tickets => tickets
            .OnUpdated(
                "ticket-resolved-to-chat",
                when: "changed(status) && new.status == 'resolved'",
                handler: (AlvoUpdated<AlvoRecord> resolved, IChatNotifier chat, CancellationToken ct) =>
                    chat.PostAsync($"Ticket '{resolved.New["title"]}' resolved", ct))
            .OnCreated<OpenFollowUp>(
                "open-follow-up",
                when: "new.priority == 'high'",
                configure: options => options.Timeout = TimeSpan.FromSeconds(10)));
}

public interface IChatNotifier
{
    Task PostAsync(string message, CancellationToken cancellationToken);
}

public sealed class OpenFollowUp : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
{
    public Task HandleAsync(AlvoCreated<AlvoRecord> created, AlvoEventContext context, CancellationToken cancellationToken) =>
        context.Data.CreateAsync(
            "follow_ups",
            new Dictionary<string, object?> { ["ticket_id"] = created.New["id"]?.ToString(), ["note"] = "Call the customer back" },
            cancellationToken);
}
