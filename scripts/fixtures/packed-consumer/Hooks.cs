using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Hooks;

namespace PackedConsumer;

public sealed class RateGuard(HttpClient http) : IBeforeWriteHook
{
    public async ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken) =>
        write.Set("rate", await http.GetStringAsync("https://rates.example/eur", cancellationToken)); // ALVO1001
}

public static class Rates
{
    public static IAlvoBuilder AddRates(this IAlvoBuilder alvo, HttpClient http) =>
        alvo.AddCelFunction("rateOf", (string currency) => http.GetStringAsync("https://rates.example/" + currency).Result); // ALVO1001
}

public sealed class Notify(HttpClient http) : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
{
    public Task HandleAsync(AlvoCreated<AlvoRecord> created, AlvoEventContext context, CancellationToken cancellationToken) =>
        http.GetAsync("https://hooks.example", cancellationToken);
}
