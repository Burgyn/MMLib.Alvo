using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;
using NSubstitute;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>
/// Boot validation of commands (spec 2026-10-14 §4.2, §4.9, E3): every problem at once, with id, kind, locator and fix;
/// a command with a problem never binds; the access rule compiles in CelProfile.Condition (Ruling 1).
/// </summary>
public sealed class CommandBindingTests
{
    [Fact]
    public void An_unbound_command_reading_the_caller_and_the_tenant_binds()
    {
        var result = Compile(commands => commands.Map("quote_stay", "/commands/quote", null, CommandAccess.Of("'staff' in @user.roles && string(@tenant.id) != ''")));

        result.Problems.ShouldBeEmpty();
        var bound = result.Bindings.Command("quote_stay").ShouldNotBeNull();
        bound.ReadsRow.ShouldBeFalse();
    }

    [Fact]
    public void An_entity_command_reading_its_row_binds_and_reads_the_row()
    {
        var result = Compile(commands => commands.Map("resolve_ticket", "/commands/tickets/{id}/resolve", "tickets", CommandAccess.Of("status == 'open' && 'staff' in @user.roles")));

        result.Problems.ShouldBeEmpty();
        result.Bindings.Command("resolve_ticket").ShouldNotBeNull().ReadsRow.ShouldBeTrue();
    }

    [Fact]
    public void An_unbound_command_naming_a_field_is_refused_at_its_access_slot() =>
        Compile(commands => commands.Map("quote_stay", "/q", null, CommandAccess.Of("status == 'open'")))
            .Problems.ShouldContain(problem => problem.Id == "quote_stay" && problem.Kind == "command" && problem.Locator == "extensions/quote_stay/access");

    [Fact]
    public void A_command_bound_to_an_undeclared_entity_is_refused_at_its_entity_slot() =>
        Compile(commands => commands.Map("resolve", "/r/{id}", "tikets", CommandAccess.Of("true")))
            .Problems.ShouldContain(problem => problem.Locator == "extensions/resolve/entity");

    [Theory]
    [InlineData("old.status == 'open'")]
    [InlineData("changed(status)")]
    public void An_access_rule_reading_a_before_image_is_refused(string access) =>
        Compile(commands => commands.Map("resolve", "/r/{id}", "tickets", CommandAccess.Of(access)))
            .Problems.ShouldContain(problem => problem.Locator == "extensions/resolve/access" && problem.Message.Contains("no before-image", StringComparison.Ordinal));

    [Fact]
    public void A_command_name_off_the_grammar_is_refused_with_the_command_fix() =>
        Compile(commands => commands.Map("Confirm-Booking", "/c", null, CommandAccess.Of("true")))
            .Problems.ShouldContain(problem => problem.Locator == "extensions/Confirm-Booking/id" && problem.Fix.Contains("separated by dots", StringComparison.Ordinal));

    [Fact]
    public void Two_commands_publishing_one_operation_id_are_refused() =>
        Compile(commands =>
        {
            commands.Map("a_b", "/one", null, CommandAccess.Of("true"));
            commands.Map("a.b", "/two", null, CommandAccess.Of("true"));
        }).Problems.Where(problem => problem.Message.Contains("operationId", StringComparison.Ordinal)).Select(problem => problem.Id)
            .ShouldBe(["a_b", "a.b"], ignoreOrder: true);

    [Fact]
    public void A_command_and_a_handler_deriving_one_event_segment_are_refused() =>
        Compile(
            commands => commands.Map("send_note", "/n", null, CommandAccess.Of("true")),
            alvo => alvo.Entity("notes", n => n.OnCreated("send-note", OnCreated)))
            .Problems.ShouldContain(problem => problem.Message.Contains("one event-name segment", StringComparison.Ordinal));

    [Fact]
    public void A_handler_of_a_completion_no_command_produces_is_refused() =>
        Compile(
            _ => { },
            alvo => alvo.AddEventHandler("on-confirmed", "alvo.command.confirm_booking.completed", (AlvoRecord _) => Task.CompletedTask))
            .Problems.ShouldContain(problem => problem.Id == "on-confirmed" && problem.Locator == "extensions/on-confirmed/event");

    [Fact]
    public void A_handler_of_a_mapped_command_s_completion_binds()
    {
        var result = Compile(
            commands => commands.Map("confirm_booking", "/c/{id}", "tickets", CommandAccess.Of("true")),
            alvo => alvo.AddEventHandler("on-confirmed", "alvo.command.confirm_booking.completed", (AlvoRecord _) => Task.CompletedTask));

        result.Problems.ShouldBeEmpty();
        result.Bindings.Handler("on-confirmed").ShouldNotBeNull();
    }

    [Fact]
    public void A_composition_without_a_relational_provider_refuses_every_command()
    {
        var services = Substitute.For<IServiceProviderIsService>();
        services.IsService(Arg.Any<Type>()).Returns(call => call.Arg<Type>() != typeof(ICommandLedger));

        Compile(commands => commands.Map("quote_stay", "/q", null, CommandAccess.Of("true")), services: services)
            .Problems.ShouldContain(problem => problem.Locator == "extensions/quote_stay/provider");
    }

    [Fact]
    public void An_access_rule_reading_a_hidden_field_is_refused()
    {
        var collection = new ServiceCollection();
        collection.AddAlvo();
        using var provider = collection.BuildServiceProvider();
        provider.GetRequiredService<CommandRegistry>().Map("resolve", "/r/{id}", "tickets", CommandAccess.Of("!(status == 'resolved')"));

        var problems = ExtensionBindingCompiler.Compile(provider.GetRequiredService<ExtensionRegistry>(), HidingStatus(), CelFixtures.Compiler, EveryService()).Problems;

        problems.ShouldContain(problem => problem.Locator == "extensions/resolve/access" && problem.Message.Contains("hides from some callers", StringComparison.Ordinal));
    }

    [Fact]
    public void Removing_an_entity_a_command_binds_is_refused_at_its_pointer()
    {
        var collection = new ServiceCollection();
        collection.AddAlvo();
        using var provider = collection.BuildServiceProvider();
        provider.GetRequiredService<CommandRegistry>().Map("resolve_ticket", "/r/{id}", "tickets", CommandAccess.Of("status == 'open'"));
        var registry = provider.GetRequiredService<ExtensionRegistry>();
        var regressions = ExtensionBindingCompiler.Compile(registry, CatalogOf(new([.. HandlerCatalog.Schema.Entities.Where(entity => entity.Name != "tickets")])), CelFixtures.Compiler, EveryService()).Problems;

        var refusals = ExtensionApplyRefusals.Refusals(registry, regressions, HandlerCatalog.Schema, new([.. HandlerCatalog.Schema.Entities.Where(entity => entity.Name != "tickets")]), CelFixtures.Compiler);

        refusals.ShouldContain(refusal => refusal.Path == "/entities/tickets" && refusal.Message.Contains("extension 'resolve_ticket' (command, in this host)", StringComparison.Ordinal));
    }

    private static ExtensionBindingResult Compile(
        Action<CommandRegistry> map, Action<IAlvoBuilder>? configure = null, IServiceProviderIsService? services = null)
    {
        var collection = new ServiceCollection();
        collection.AddAlvo(configure ?? (_ => { }));
        using var provider = collection.BuildServiceProvider();
        map(provider.GetRequiredService<CommandRegistry>());

        return ExtensionBindingCompiler.Compile(provider.GetRequiredService<ExtensionRegistry>(), Catalog(), CelFixtures.Compiler, services ?? EveryService());
    }

    private static PolicyCatalog HidingStatus() =>
        PolicyCatalog.Build(
            new AlvoDescriptor
            {
                ApiVersion = "alvo.dev/v1",
                Name = "handlers",
                Entities = HandlerCatalog.Schema.Entities.ToDictionary(
                    entity => entity.Name,
                    entity => new EntityDescriptor
                    {
                        Fields = entity.Name == "tickets"
                            ? new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal) { ["status"] = new() { Type = FieldType.Enum, Values = ["open", "resolved"], Hidden = BoolOrCel.FromExpression("!('admin' in @user.roles)") } }
                            : new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal),
                    },
                    StringComparer.Ordinal),
            },
            HandlerCatalog.Schema,
            CelFixtures.Compiler);
}
