using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace TicketDesk.Typed;

[AlvoEntity("tickets")]
public sealed class Ticket
{
    public Guid Id { get; init; }

    public required string Title { get; init; }

    public TicketPriority? Priority { get; init; }

    public TicketStatus? Status { get; init; }
}

public enum TicketPriority
{
    Low,
    High,
}

public enum TicketStatus
{
    Open,
    Resolved,
}

public static class TicketDeskTypedHandlers
{
    public static IAlvoBuilder AddTicketDeskTypedHandlers(this IAlvoBuilder alvo) => alvo
        .Entity<Ticket>(tickets => tickets
            .OnUpdated(
                "ticket-resolved-to-chat",
                when: e => e.Changed(x => x.Status) && e.New.Status == TicketStatus.Resolved,
                handler: (AlvoUpdated<Ticket> resolved, IChatNotifier chat, CancellationToken ct) =>
                    chat.PostAsync($"Ticket '{resolved.New.Title}' resolved", ct))
            .OnCreated<OpenFollowUp>(
                "open-follow-up",
                when: e => e.New.Priority == TicketPriority.High,
                configure: options => options.Timeout = TimeSpan.FromSeconds(10)));
}

public sealed class OpenFollowUp : IAlvoEventHandler<AlvoCreated<Ticket>>
{
    public Task HandleAsync(AlvoCreated<Ticket> created, AlvoEventContext context, CancellationToken cancellationToken) =>
        context.Data.CreateAsync(
            "follow_ups",
            new Dictionary<string, object?> { ["ticket_id"] = created.New.Id.ToString(), ["note"] = "Call the customer back" },
            cancellationToken);
}
