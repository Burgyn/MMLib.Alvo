using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.TestHost;
using Shouldly;
using System.Collections.Concurrent;
using Xunit;

namespace Billing.Tests;

public sealed class IssueInvoiceTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "billing-desk.alvo.json");

    [Fact]
    public async Task The_descriptor_hook_invokes_the_function_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var invoicing = new RecordingInvoicing();
        await using var alvo = await AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddFunction<IssueInvoice>("issue-invoice");
            builder.Services.AddSingleton<IInvoicing>(invoicing);
        }, cancellationToken: ct);

        await alvo.Data.CreateAsync("orders", new Dictionary<string, object?> { ["number"] = "SO-1", ["total"] = 120.50m }, Staff(), cancellationToken: ct);
        await alvo.DrainEventsAsync(ct);
        await alvo.DrainEventsAsync(ct);

        invoicing.Issued.ShouldHaveSingleItem().ShouldBe(("SO-1", 120.50m, "EUR"));
    }

    [Fact]
    public async Task An_input_typo_is_refused_at_apply_with_the_closest_property()
    {
        var ct = TestContext.Current.CancellationToken;
        var typo = (await File.ReadAllTextAsync(Descriptor, ct)).Replace("\"orderNumber\"", "\"orderNumbr\"", StringComparison.Ordinal);
        var path = Path.Combine(Path.GetTempPath(), $"billing-typo-{Guid.NewGuid():N}.alvo.json");
        await File.WriteAllTextAsync(path, typo, ct);

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(() => AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(path).AddFunction<IssueInvoice>("issue-invoice");
            builder.Services.AddSingleton<IInvoicing>(new RecordingInvoicing());
        }, cancellationToken: ct));

        refusal.Message.ShouldContain("/entities/orders/hooks/afterCreate/0/action/input/orderNumbr");
        refusal.Message.ShouldContain("Did you mean 'orderNumber'?");
    }

    private static AlvoContext Staff() => AlvoTestHost.Caller("staff");

    private sealed class RecordingInvoicing : IInvoicing
    {
        private readonly ConcurrentDictionary<Guid, (string, decimal, string)> _issued = new();

        public IReadOnlyCollection<(string OrderNumber, decimal Amount, string Currency)> Issued => [.. _issued.Values];

        public Task IssueAsync(Guid idempotencyKey, string orderNumber, decimal amount, string currency, CancellationToken cancellationToken)
        {
            _issued.TryAdd(idempotencyKey, (orderNumber, amount, currency));
            return Task.CompletedTask;
        }
    }
}
