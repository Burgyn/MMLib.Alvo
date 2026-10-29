using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;

namespace InvoiceDesk;

public static class InvoiceDeskSetup
{
    public static IAlvoBuilder AddInvoiceDesk(this IAlvoBuilder alvo)
    {
        alvo.Services.AddSingleton<InvoiceIssuer>();
        return alvo;
    }
}

[AlvoEntity("invoices")]
public sealed class Invoice
{
    public Guid Id { get; init; }

    public required string Number { get; init; }

    public decimal? Total { get; init; }

    public bool? Paid { get; init; }
}

public sealed record InvoiceLine(string Label, decimal Amount);

public sealed class InvoiceIssuer(IAlvoTransactions transactions)
{
    public Task<Guid> IssueAsync(AlvoContext clerk, string number, IReadOnlyList<InvoiceLine> lines, CancellationToken cancellationToken) =>
        transactions.TransactAsync(clerk, async (unit, ct) =>
        {
            var invoice = await CreateInvoiceAsync(unit, number, lines.Sum(line => line.Amount), ct);
            var id = invoice.Id;

            var written = await unit.CreateManyAsync("invoice_lines", [.. lines.Select(line => Line(id, line))], ct);
            if (!written.Succeeded)
            {
                throw new InvalidOperationException($"Invoice {number} has a line the rules refuse.");
            }

            await unit.PublishAsync("billing.invoiced", $"invoices/{id}", new Dictionary<string, object?> { ["number"] = invoice.Number }, ct);
            return id;
        }, cancellationToken: cancellationToken);

    public Task MarkPaidAsync(AlvoContext clerk, Guid invoice, CancellationToken cancellationToken) =>
        transactions.TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.UpdateAsync(invoice, AlvoPatch.For<Invoice>().Set(x => x.Paid, true), cancellationToken: ct);
            await unit.PublishAsync("billing.paid", $"invoices/{invoice}", null, ct);
        }, cancellationToken: cancellationToken);

    private static async Task<Invoice> CreateInvoiceAsync(IAlvoTransaction unit, string number, decimal total, CancellationToken ct)
    {
        try
        {
            return (await unit.CreateAsync(new Invoice { Number = number, Total = total }, ct))!;
        }
        catch (AlvoConstraintViolationException taken) when (taken.Kind == AlvoConstraintKind.Unique)
        {
            return (await unit.CreateAsync(new Invoice { Number = number + "-B", Total = total }, ct))!;
        }
    }

    private static Dictionary<string, object?> Line(Guid invoice, InvoiceLine line) =>
        new Dictionary<string, object?> { ["invoice_id"] = invoice, ["label"] = line.Label, ["amount"] = line.Amount };
}
