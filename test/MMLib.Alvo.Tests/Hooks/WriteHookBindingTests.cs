using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Management;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;
using NSubstitute;
using System.Net.Http;

namespace MMLib.Alvo.Tests.Hooks;

public sealed class NetworkGuard(HttpClient http) : IBeforeWriteHook
{
    public ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
    {
        _ = http.Timeout;
        return ValueTask.CompletedTask;
    }
}

public sealed class NetworkValidator(HttpClient http) : IValidateWriteHook
{
    public ValueTask ValidateAsync(ValidateWrite<AlvoRecord> write, CancellationToken cancellationToken)
    {
        _ = http.Timeout;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Boot and apply checks of write hooks (spec 2026-10-14 §4.2, §4.4.5): all problems at once, each with id, kind, locator and fix.</summary>
public sealed class WriteHookBindingTests
{
    private static readonly Func<BeforeWrite<AlvoRecord>, ValueTask> _before = _ => ValueTask.CompletedTask;
    private static readonly Func<BeforeDelete<AlvoRecord>, ValueTask> _beforeDelete = _ => ValueTask.CompletedTask;

    [Fact]
    public void A_hook_on_a_known_entity_binds_with_its_compiled_condition()
    {
        var result = Compile(alvo => alvo.Entity("tickets", t => t.BeforeWrite("guard", _before, when: "new.status == 'open'")));

        result.Problems.ShouldBeEmpty();
        result.Bindings.WriteHook("guard").ShouldNotBeNull().ConditionFor("tickets").ShouldNotBeNull();
    }

    [Fact]
    public void An_unknown_entity_and_a_when_typo_are_refused_at_once_with_their_locators()
    {
        var problems = Compile(alvo => alvo
            .Entity("tikets", t => t.BeforeWrite("misspelled", _before))
            .Entity("tickets", t => t.BeforeWrite("typo", _before, when: "new.stauts == 'open'"))).Problems;

        problems.Select(problem => (problem.Id, problem.Kind, problem.Locator)).Distinct().ShouldBe(
            [("misspelled", "beforeWrite", "extensions/misspelled/entity"), ("typo", "beforeWrite", "extensions/typo/when")], ignoreOrder: true);
        problems.Single(problem => problem.Id == "misspelled").Fix.ShouldContain("Did you mean 'tickets'?");
    }

    [Fact]
    public void A_delete_hook_reading_the_new_image_is_refused() =>
        Compile(alvo => alvo.Entity("tickets", t => t.BeforeDelete("guard", _beforeDelete, when: "new.status == 'open'")))
            .Problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/guard/when");

    [Fact]
    public void An_every_entity_hook_whose_condition_names_a_field_is_refused() =>
        Compile(alvo => alvo.Entity("*", every => every.BeforeWrite("guard", _before, when: "new.status == 'open'")))
            .Problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/guard/when");

    [Fact]
    public void An_in_transaction_hook_taking_a_network_dependency_refuses_the_start_and_a_validate_hook_may()
    {
        var problems = Compile(alvo => alvo
            .Entity("tickets", t => t.BeforeWrite<NetworkGuard>("network-guard"))
            .Entity("tickets", t => t.Validate<NetworkValidator>("network-check"))
            .Entity("notes", n => n.AfterWrite("data-port", (AfterWrite<AlvoRecord> _, IAlvoData data) => ValueTask.CompletedTask))
            .Entity("notes", n => n.AfterWrite("lazy-port", (AfterWrite<AlvoRecord> _, Lazy<IAlvoSessions> sessions) => ValueTask.CompletedTask))).Problems;

        problems.Where(problem => problem.Locator.EndsWith("/dependencies", StringComparison.Ordinal)).Select(problem => problem.Id)
            .ShouldBe(["network-guard", "data-port", "lazy-port"], ignoreOrder: true);
        problems.Single(problem => problem.Id == "network-guard").Message.ShouldContain("System.Net.Http.HttpClient");
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(31_000, 50)]
    [InlineData(250, 0)]
    [InlineData(250, 1001)]
    public void An_in_transaction_hook_s_bounds_are_checked_at_boot(int milliseconds, int operations) =>
        Compile(alvo => alvo.Entity("tickets", t => t.BeforeWrite("guard", _before, configure: o =>
        {
            o.Timeout = TimeSpan.FromMilliseconds(milliseconds);
            o.MaxOperations = operations;
        }))).Problems.ShouldHaveSingleItem().Id.ShouldBe("guard");

    [Fact]
    public void A_validate_hook_may_take_a_minute_and_no_more() =>
        Compile(alvo => alvo.Entity("tickets", t => t.Validate("slow", (ValidateWrite<AlvoRecord> _) => ValueTask.CompletedTask, configure: o => o.Timeout = TimeSpan.FromSeconds(61))))
            .Problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/slow/timeout");

    [Theory]
    [InlineData(typeof(IAlvoTransactions))]
    [InlineData(typeof(IHookRejectionLog))]
    public void A_composition_with_no_relational_provider_refuses_a_write_hook(Type missing)
    {
        var services = Substitute.For<IServiceProviderIsService>();
        services.IsService(Arg.Any<Type>()).Returns(call => (Type)call[0] != missing);

        HandlerCatalog.Compile(alvo => alvo.Entity("tickets", t => t.BeforeWrite("guard", _before)), services)
            .Problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/guard/provider");
    }

    [Fact]
    public void Removing_a_field_a_bound_write_hook_reads_is_refused_at_apply()
    {
        var refusal = Should.Throw<DescriptorValidationException>(() => Guard(HandlerCatalog.Catalog()).Check(WithoutStatus()));

        refusal.Message.ShouldContain("'guard'");
    }

    [Fact]
    public void Removing_the_entity_a_bound_write_hook_guards_is_refused_at_apply_pointing_at_the_entity()
    {
        var others = HandlerCatalog.Schema.Entities.Where(entity => entity.Name != "tickets");

        var refusal = Should.Throw<DescriptorValidationException>(() => Guard(HandlerCatalog.Catalog()).Check(HandlerCatalog.CatalogOf(new SchemaModel([.. others]))));

        refusal.Result.Errors.Select(error => error.Path).ShouldContain("/entities/tickets");
    }

    [Fact]
    public void A_write_hook_that_was_already_unbound_is_a_warning_and_blocks_nothing()
    {
        var check = Guard(WithoutStatus()).Check(WithoutStatus());

        check.Warnings.ShouldNotBeEmpty();
        check.Warnings.ShouldAllBe(warning => warning.Severity == DescriptorValidationSeverity.Warning && warning.Message.Contains("'guard'"));
    }

    [Fact]
    public void The_catalog_describes_each_stage_with_its_kind_stage_operations_and_top_level_flag()
    {
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("tickets", t => t
            .Validate("check", (ValidateWrite<AlvoRecord> _) => ValueTask.CompletedTask)
            .BeforeWrite("guard", _before, when: "new.status == 'open'", configure: o => o.Description = "Guards.")
            .AfterDelete("tidy", (AfterDelete<AlvoRecord> _) => ValueTask.CompletedTask)));

        var items = new ExtensionCatalog(registry, Options.Create(new AlvoExtensionOptions())).Items;

        items.Select(item => (item.Id, item.Kind, item.Stage, string.Join(",", item.Operations), item.TopLevelOnly)).ShouldBe(
        [
            ("check", AlvoExtensionKind.Validate, AlvoExtensionStage.PreTransaction, "create,update", true),
            ("guard", AlvoExtensionKind.BeforeWrite, AlvoExtensionStage.InTransaction, "create,update", false),
            ("tidy", AlvoExtensionKind.AfterDelete, AlvoExtensionStage.InTransaction, "delete", false),
        ]);
        items.Single(item => item.Id == "guard").ShouldSatisfyAllConditions(
            item => item.When.ShouldBe("new.status == 'open'"),
            item => item.Timeout.ShouldBe(TimeSpan.FromMilliseconds(250)),
            item => item.Description.ShouldBe("Guards."),
            item => item.Enabled.ShouldBeTrue());
    }

    private static ExtensionBindingResult Compile(Action<IAlvoBuilder> configure) => HandlerCatalog.Compile(configure);

    private static ExtensionApplyGuard Guard(PolicyCatalog current)
    {
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("tickets", t => t.BeforeWrite("guard", _before, when: "new.status == 'open'")));
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, HandlerCatalog.EveryService(), NullLogger<ExtensionBinder>.Instance);
        var catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns(current);

        return new ExtensionApplyGuard(registry, binder, CelFixtures.Compiler, catalogs);
    }

    private static PolicyCatalog WithoutStatus()
    {
        var tickets = HandlerCatalog.Schema.Entities.Single(entity => entity.Name == "tickets");
        return HandlerCatalog.CatalogOf(new SchemaModel([tickets with { Fields = [.. tickets.Fields.Where(field => field.Name != "status")] }]));
    }
}
