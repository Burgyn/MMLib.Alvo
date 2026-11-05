using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.TestHost;
using Shouldly;
using System.Net;
using Xunit;

namespace CurrencyDesk.Tests;

public sealed class CurrencyDeskHooksTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "currency-desk.alvo.json");

    [Fact]
    public async Task An_invoice_is_stamped_with_the_stored_rate_inside_its_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await StartAsync(ct);
        var clerk = AlvoTestHost.Caller("clerk");
        await alvo.Data.CreateAsync("rates", new Dictionary<string, object?> { ["currency"] = "CZK", ["per_euro"] = 25.1m }, clerk, cancellationToken: ct);

        var invoice = await alvo.Data.CreateAsync("invoices", new Dictionary<string, object?> { ["currency"] = "CZK", ["amount"] = 100m }, clerk, cancellationToken: ct);

        invoice["per_euro"].ShouldBe(25.1m);
        alvo.Hooks.AssertRan("currency-is-traded");
        alvo.Hooks.AssertRan("stamp-the-rate");
    }

    [Fact]
    public async Task A_currency_the_exchange_does_not_trade_is_refused_before_the_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await StartAsync(ct);

        var refused = await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(
            "invoices", new Dictionary<string, object?> { ["currency"] = "XTS", ["amount"] = 1m }, AlvoTestHost.Caller("clerk"), cancellationToken: ct));

        refused.Code.ShouldBe("currency-not-traded");
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "invoices" }, AlvoTestHost.Caller("clerk"), ct)).Items.ShouldBeEmpty();
    }

    private static Task<AlvoTestHost> StartAsync(CancellationToken ct) =>
        AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddCurrencyDesk();
            builder.Services.AddSingleton(new HttpClient(new Exchange("CZK", "EUR")) { BaseAddress = new Uri("https://exchange.example") });
        }, time: new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero)), cancellationToken: ct);

    private sealed class Exchange(params string[] traded) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(
                traded.Contains(request.RequestUri!.Segments[^1], StringComparer.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.NotFound));
    }
}
