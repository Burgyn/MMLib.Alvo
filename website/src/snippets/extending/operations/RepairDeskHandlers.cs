using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;

namespace RepairDesk;

public static class RepairDeskHandlers
{
    public static IAlvoBuilder AddRepairDesk(this IAlvoBuilder alvo) =>
        alvo.Entity("repairs", repairs => repairs.OnCreated<OrderParts>(
            "order-parts",
            configure: options =>
            {
                options.Description = "Orders the part a new repair needs.";
                options.Retry = AlvoRetryPolicy.From(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
            }));
}

public interface IPartsSupplier
{
    Task OrderAsync(string part, CancellationToken cancellationToken);
}

public sealed class OrderParts(IPartsSupplier supplier) : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
{
    public Task HandleAsync(AlvoCreated<AlvoRecord> created, AlvoEventContext context, CancellationToken cancellationToken) =>
        supplier.OrderAsync((string)created.New["part"]!, cancellationToken);
}
