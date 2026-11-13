using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;

namespace FirstExtension;

[AlvoEntity("orders")]
public sealed class Order
{
    public Guid Id { get; init; }

    public required string Customer { get; init; }

    public required string Postcode { get; init; }
}

public interface IDeliveryAreas
{
    Task<bool> CoversAsync(string postcode, CancellationToken cancellationToken);
}

public sealed class PostcodeIsCovered(IDeliveryAreas areas) : IValidateWriteHook<Order>
{
    public async ValueTask ValidateAsync(ValidateWrite<Order> write, CancellationToken cancellationToken)
    {
        if (!await areas.CoversAsync(write.New.Postcode, cancellationToken))
        {
            write.Invalid("postcode-not-covered", "The courier does not deliver to this postcode.", "postcode");
        }
    }
}

public sealed class CourierAreas(HttpClient courier) : IDeliveryAreas
{
    public async Task<bool> CoversAsync(string postcode, CancellationToken cancellationToken)
    {
        using var answer = await courier.GetAsync(new Uri($"areas/{Uri.EscapeDataString(postcode)}", UriKind.Relative), cancellationToken);

        return answer.IsSuccessStatusCode;
    }
}

public static class DeliveryDesk
{
    public static IAlvoBuilder AddDeliveryDesk(this IAlvoBuilder alvo) => alvo
        .Entity<Order>(orders => orders.Validate<PostcodeIsCovered>("postcode-is-covered"));
}
