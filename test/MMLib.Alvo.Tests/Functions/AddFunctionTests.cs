using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Functions;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Migrations;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Functions;

/// <summary>
/// <c>AddFunction&lt;T&gt;</c>: the type and name checks at registration, the keyed service a delivery resolves, the shared id
/// namespace, and the catalog entry with the input's JSON Schema.
/// </summary>
public sealed class AddFunctionTests
{
    [Fact]
    public void A_function_is_a_keyed_service_under_its_name()
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.AddFunction<IssueInvoice>("issue-invoice"));
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredKeyedService<IssueInvoice>("issue-invoice");
        var second = provider.GetRequiredKeyedService<IssueInvoice>("issue-invoice");

        first.ShouldNotBeSameAs(second, "a function is built per run, like every other extension class");
        provider.GetKeyedService<IssueInvoice>("other").ShouldBeNull();
        var function = provider.GetRequiredService<ExtensionRegistry>().Function("issue-invoice").ShouldNotBeNull();
        (function.Function, function.Input).ShouldBe((typeof(IssueInvoice), typeof(InvoiceInput)));
    }

    [Fact]
    public void A_type_implementing_no_function_interface_is_refused()
    {
        var refusal = Should.Throw<ArgumentException>(() => Registry(alvo => alvo.AddFunction<NotAFunction>("issue-invoice")));

        refusal.Message.ShouldContain("NotAFunction");
        refusal.Message.ShouldContain("IAlvoFunction<TInput>");
        refusal.Message.ShouldContain("0 times");
    }

    [Fact]
    public void A_type_implementing_two_function_interfaces_is_refused()
    {
        var refusal = Should.Throw<ArgumentException>(() => Registry(alvo => alvo.AddFunction<TwoFunctions>("issue-invoice")));

        refusal.Message.ShouldContain("TwoFunctions");
        refusal.Message.ShouldContain("2 times");
    }

    [Theory]
    [InlineData("Issue-Invoice")]
    [InlineData("1invoice")]
    [InlineData("issue.invoice")]
    [InlineData("")]
    public void A_name_outside_the_identifier_grammar_is_refused(string name) =>
        Should.Throw<ArgumentException>(() => Registry(alvo => alvo.AddFunction<IssueInvoice>(name)));

    [Fact]
    public void A_function_named_like_a_handler_refuses_the_boot()
    {
        var refusal = Should.Throw<AlvoStartupRefusedException>(() => EnsureRegistrable(alvo => alvo
            .AddFunction<IssueInvoice>("issue-invoice")
            .AddEventHandler("issue-invoice", "orders.approved", OnRecord)));

        refusal.Message.ShouldContain("'issue-invoice' is registered 2 times (function, eventHandler)");
    }

    [Fact]
    public void The_catalog_lists_the_function_with_its_input_schema()
    {
        var catalog = new ExtensionCatalog(
            Registry(alvo => alvo.AddFunction<IssueInvoice>("issue-invoice")),
            Options.Create(new AlvoExtensionOptions { ExposeTypeNames = true }));

        var item = catalog.Items.ShouldHaveSingleItem();
        (item.Id, item.Kind, item.Stage, item.Event).ShouldBe(("issue-invoice", AlvoExtensionKind.Function, AlvoExtensionStage.PostCommit, (string?)null));
        item.Entities.ShouldBeEmpty();
        item.Operations.ShouldBeEmpty();
        item.TypeName.ShouldBe(typeof(IssueInvoice).FullName);
        var schema = item.InputSchema.ShouldNotBeNull();
        schema.GetProperty("properties").TryGetProperty("orderId", out _).ShouldBeTrue();
        ((int)AlvoExtensionKind.Function).ShouldBe(11, "7-10 are taken, and the enum is append-only");
        ExtensionCatalog.WireName(AlvoExtensionKind.Function).ShouldBe("function");
    }

    [Fact]
    public void The_capability_report_honours_extensions_function()
    {
        var catalog = new ExtensionCatalog(
            Registry(alvo => alvo.AddFunction<IssueInvoice>("issue-invoice")),
            Options.Create(new AlvoExtensionOptions()));

        CapabilityReport.Project(catalog.HonouredKinds).Honoured.ShouldContain("extensions.function");
        catalog.Items.ShouldHaveSingleItem().TypeName.ShouldBeNull();
    }

    private static void EnsureRegistrable(Action<IAlvoBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddAlvo(configure);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ExtensionBinder>().EnsureRegistrable();
    }

    internal sealed record InvoiceInput(string OrderId, decimal Amount, string? Note);

    private sealed class IssueInvoice : IAlvoFunction<InvoiceInput>
    {
        public Task InvokeAsync(InvoiceInput input, AlvoFunctionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NotAFunction
    {
    }

    private sealed class TwoFunctions : IAlvoFunction<InvoiceInput>, IAlvoFunction<string>
    {
        public Task InvokeAsync(InvoiceInput input, AlvoFunctionContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task InvokeAsync(string input, AlvoFunctionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
