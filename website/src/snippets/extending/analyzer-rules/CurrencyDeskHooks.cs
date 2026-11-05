using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using System.Net;

namespace CurrencyDesk;

[AlvoEntity("rates")]
public sealed class Rate
{
    public required string Currency { get; init; }

    public decimal PerEuro { get; init; }
}

[AlvoEntity("invoices")]
public sealed class Invoice
{
    public Guid Id { get; init; }

    public required string Currency { get; init; }

    public decimal Amount { get; init; }

    public decimal? PerEuro { get; init; }
}

public static class CurrencyDeskHooks
{
    public static IAlvoBuilder AddCurrencyDesk(this IAlvoBuilder alvo) => alvo
        .Entity<Invoice>(invoices => invoices
            .Validate<CurrencyIsTraded>("currency-is-traded")
            .BeforeWrite<StampTheRate>("stamp-the-rate"));
}

public sealed class CurrencyIsTraded(HttpClient exchange) : IValidateWriteHook<Invoice>
{
    public async ValueTask ValidateAsync(ValidateWrite<Invoice> write, CancellationToken cancellationToken)
    {
        using var answer = await exchange.GetAsync($"/traded/{write.New.Currency}", cancellationToken);
        if (answer.StatusCode == HttpStatusCode.NotFound)
        {
            write.Invalid("currency-not-traded", "The exchange does not trade this currency.", "currency");
        }
    }
}

public sealed class StampTheRate : IBeforeWriteHook<Invoice>
{
    public async ValueTask BeforeWriteAsync(BeforeWrite<Invoice> write, CancellationToken cancellationToken)
    {
        var currency = write.New.Currency;
        var rates = await write.Transaction.QueryAsync<Rate>(x => x.Currency == currency, limit: 1, cancellationToken);
        if (rates.Count == 0)
        {
            write.Conflict("no-rate", "No rate is stored for this currency yet.", "currency");
            return;
        }

        write.Set(x => x.PerEuro, rates[0].PerEuro);
    }
}
