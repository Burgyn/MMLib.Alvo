using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.TestHost;
using Xunit;

namespace FirstExtension.Tests;

public sealed class DeliveryDeskTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "delivery-desk.alvo.json");

    [Fact]
    public async Task An_order_to_a_postcode_the_courier_does_not_cover_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddDeliveryDesk();
            builder.Services.AddSingleton<IDeliveryAreas>(new CoveredPostcodes("811 01"));
        }, cancellationToken: ct);
        var clerk = AlvoTestHost.Caller("clerk");

        var stored = await alvo.Data.CreateAsync("orders", Order("811 01"), clerk, cancellationToken: ct);
        var refusal = await Assert.ThrowsAsync<AlvoWriteRejectedException>(
            () => alvo.Data.CreateAsync("orders", Order("040 01"), clerk, cancellationToken: ct));

        Assert.Equal("811 01", stored["postcode"]);
        Assert.Equal((AlvoWriteRejectionKind.Invalid, "postcode-not-covered", "/postcode"), (refusal.Kind, refusal.Code, refusal.Pointer));
        alvo.Hooks.AssertRan("postcode-is-covered", times: 2);
    }

    private static Dictionary<string, object?> Order(string postcode) => new()
    {
        ["customer"] = "Ada",
        ["postcode"] = postcode,
    };

    private sealed class CoveredPostcodes(params string[] covered) : IDeliveryAreas
    {
        public Task<bool> CoversAsync(string postcode, CancellationToken cancellationToken) => Task.FromResult(covered.Contains(postcode));
    }
}
