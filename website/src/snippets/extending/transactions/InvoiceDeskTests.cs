using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace InvoiceDesk.Tests;

public sealed class InvoiceDeskTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "invoice-desk.alvo.json");

    [Fact]
    public async Task An_invoice_its_lines_and_its_event_commit_together()
    {
        var ct = TestContext.Current.CancellationToken;
        var billing = new Billing();
        await using var alvo = await StartAsync(billing, ct);
        var clerk = AlvoTestHost.Caller("clerk");

        var id = await alvo.Services.GetRequiredService<InvoiceIssuer>().IssueAsync(
            clerk, "2026-0001", [new("Brake pads", 24.90m), new("Labour", 30m)], ct);
        await alvo.DrainEventsAsync(ct);

        (await alvo.Data.GetAsync<Invoice>(id, clerk, ct))!.Total.ShouldBe(54.90m);
        var lines = await alvo.Data.QueryAsync(
            new AlvoQuery { Entity = "invoice_lines", Filter = new AlvoComparison("invoice_id", AlvoFilterOperator.Eq, id) }, clerk, ct);
        lines.Items.Count.ShouldBe(2);
        billing.Invoiced.ShouldBe(["2026-0001"]);
    }

    [Fact]
    public async Task A_taken_number_is_caught_inside_the_unit_and_the_invoice_still_commits()
    {
        var ct = TestContext.Current.CancellationToken;
        var billing = new Billing();
        await using var alvo = await StartAsync(billing, ct);
        var clerk = AlvoTestHost.Caller("clerk");
        var issuer = alvo.Services.GetRequiredService<InvoiceIssuer>();

        await issuer.IssueAsync(clerk, "2026-0002", [new("Chain", 19m)], ct);
        var second = await issuer.IssueAsync(clerk, "2026-0002", [new("Tyre", 35m)], ct);
        await alvo.DrainEventsAsync(ct);

        (await alvo.Data.GetAsync<Invoice>(second, clerk, ct))!.Number.ShouldBe("2026-0002-B");
        billing.Invoiced.ShouldBe(["2026-0002", "2026-0002-B"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_refused_line_leaves_no_invoice_no_line_and_no_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var billing = new Billing();
        await using var alvo = await StartAsync(billing, ct);
        var clerk = AlvoTestHost.Caller("clerk");

        await Should.ThrowAsync<InvalidOperationException>(() => alvo.Services.GetRequiredService<InvoiceIssuer>().IssueAsync(
            clerk, "2026-0003", [new("Brake pads", 24.90m), new("Goodwill", 0m)], ct));
        await alvo.DrainEventsAsync(ct);

        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "invoices" }, clerk, ct)).Items.ShouldBeEmpty();
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "invoice_lines" }, clerk, ct)).Items.ShouldBeEmpty();
        billing.Invoiced.ShouldBeEmpty();
    }

    [Fact]
    public async Task Paying_is_a_typed_update_and_its_event_in_one_unit()
    {
        var ct = TestContext.Current.CancellationToken;
        var billing = new Billing();
        await using var alvo = await StartAsync(billing, ct);
        var clerk = AlvoTestHost.Caller("clerk");
        var issuer = alvo.Services.GetRequiredService<InvoiceIssuer>();
        var id = await issuer.IssueAsync(clerk, "2026-0004", [new("Labour", 30m)], ct);

        await issuer.MarkPaidAsync(clerk, id, ct);
        await alvo.DrainEventsAsync(ct);

        (await alvo.Data.GetAsync<Invoice>(id, clerk, ct))!.Paid.ShouldBe(true);
        billing.Paid.ShouldBe(1);
    }

    [Fact]
    public async Task The_data_port_is_refused_inside_a_unit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await StartAsync(new Billing(), ct);
        var clerk = AlvoTestHost.Caller("clerk");
        var transactions = alvo.Services.GetRequiredService<IAlvoTransactions>();

        var refusal = await Should.ThrowAsync<AlvoUnitInProgressException>(() => transactions.TransactAsync(clerk, async (unit, token) =>
        {
            await unit.CreateAsync(new Invoice { Number = "2026-0009" }, token);
            await alvo.Data.CreateAsync("invoices", new Dictionary<string, object?> { ["number"] = "2026-0010" }, clerk, cancellationToken: token);
        }, cancellationToken: ct));

        refusal.Message.ShouldStartWith("IAlvoData.CreateAsync was called while a transaction unit is open");
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "invoices" }, clerk, ct)).Items.ShouldBeEmpty();
    }

    private static Task<AlvoTestHost> StartAsync(Billing billing, CancellationToken ct) =>
        AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddInvoiceDesk();
            builder.AddEventHandler("record-invoice", "billing.invoiced", (AlvoRecord invoice) => billing.Invoice((string)invoice["number"]!));
            builder.AddEventHandler("record-payment", "billing.paid", (AlvoRecord _) => billing.Pay());
        }, cancellationToken: ct);

    private sealed class Billing
    {
        private readonly Lock _gate = new();
        private readonly List<string> _invoiced = [];
        private int _paid;

        public IReadOnlyList<string> Invoiced
        {
            get
            {
                lock (_gate)
                {
                    return [.. _invoiced];
                }
            }
        }

        public int Paid => _paid;

        public void Invoice(string number)
        {
            lock (_gate)
            {
                _invoiced.Add(number);
            }
        }

        public void Pay() => Interlocked.Increment(ref _paid);
    }
}
