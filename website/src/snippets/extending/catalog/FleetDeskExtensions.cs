using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace FleetDesk;

[AlvoEntity("vehicles")]
public sealed class Vehicle
{
    public Guid Id { get; init; }

    public required string Plate { get; init; }

    public VehicleStatus? Status { get; init; }
}

public enum VehicleStatus
{
    InService,
    InRepair,
}

public static class FleetDeskExtensions
{
    public static IAlvoBuilder AddFleetDeskExtensions(this IAlvoBuilder alvo) => alvo
        .Entity<Vehicle>(vehicles => vehicles
            .OnUpdated(
                "book-repair",
                when: e => e.Changed(x => x.Status) && e.New.Status == VehicleStatus.InRepair,
                handler: (AlvoUpdated<Vehicle> updated, IWorkshop workshop, CancellationToken ct) =>
                    workshop.BookAsync(updated.New.Id, ct),
                configure: options => options.Description = "Books the vehicle into the workshop when it goes in for repair."));
}

public interface IWorkshop
{
    Task BookAsync(Guid vehicle, CancellationToken cancellationToken);
}
