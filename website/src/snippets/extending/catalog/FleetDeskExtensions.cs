using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace FleetDesk;

public static class FleetDeskExtensions
{
    public static IAlvoBuilder AddFleetDeskExtensions(this IAlvoBuilder alvo) => alvo
        .Entity("vehicles", vehicles => vehicles
            .OnUpdated(
                "book-repair",
                when: "changed(status) && new.status == 'in_repair'",
                handler: (AlvoUpdated<AlvoRecord> updated, IWorkshop workshop, CancellationToken ct) =>
                    workshop.BookAsync((Guid)updated.New["id"]!, ct),
                configure: options => options.Description = "Books the vehicle into the workshop when it goes in for repair."));
}

public interface IWorkshop
{
    Task BookAsync(Guid vehicle, CancellationToken cancellationToken);
}
