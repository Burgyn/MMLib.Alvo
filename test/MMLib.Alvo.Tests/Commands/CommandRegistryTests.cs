using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>Mapped commands live in one registry the boot seals (Ruling 13).</summary>
public sealed class CommandRegistryTests
{
    [Fact]
    public void A_mapped_command_joins_the_extension_registry_after_every_registered_extension()
    {
        using var provider = Provider(alvo => alvo.AddRetiredExtension("old-handler", AlvoRetirement.Discard));
        provider.GetRequiredService<CommandRegistry>().Map("quote_stay", "/commands/quote", null, CommandAccess.Of("true"));

        var all = provider.GetRequiredService<ExtensionRegistry>().All;

        all.Select(registration => registration.Id).ShouldBe(["old-handler", "quote_stay"]);
        all[1].ShouldBeOfType<CommandRegistration>().IsMapped.ShouldBeTrue();
    }

    [Fact]
    public void A_command_mapped_after_the_boot_is_refused()
    {
        using var provider = Provider(_ => { });
        provider.GetRequiredService<ExtensionRegistry>().Seal();

        var refusal = Should.Throw<InvalidOperationException>(() =>
            provider.GetRequiredService<CommandRegistry>().Map("late", "/commands/late", null, CommandAccess.Of("true")));

        refusal.Message.ShouldContain("before app.Run()");
    }

    [Fact]
    public void A_command_mapped_after_a_first_binding_is_refused()
    {
        using var provider = Provider(_ => { });
        provider.GetRequiredService<ExtensionBinder>().Result(MMLib.Alvo.Tests.Extensions.HandlerCatalog.Catalog());

        Should.Throw<InvalidOperationException>(() =>
            provider.GetRequiredService<CommandRegistry>().Map("late", "/commands/late", null, CommandAccess.Of("true")))
            .Message.ShouldContain("before applying a descriptor");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_access_rule_refuses_registration(string? access) =>
        Should.Throw<ArgumentException>(() => CommandAccess.Of(access)).Message.ShouldContain("'true' to admit every caller");

    [Theory]
    [InlineData("confirm_booking", "commands.confirmBooking")]
    [InlineData("billing.issue_invoice", "commands.billingIssueInvoice")]
    [InlineData("a", "commands.a")]
    [InlineData("v2.re_quote", "commands.v2ReQuote")]
    public void An_operation_id_is_the_name_in_lower_camel_case(string name, string expected) =>
        CommandOperationIds.Of(name).ShouldBe(expected);

    private static ServiceProvider Provider(Action<IAlvoBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddAlvo(configure);
        return services.BuildServiceProvider();
    }
}
