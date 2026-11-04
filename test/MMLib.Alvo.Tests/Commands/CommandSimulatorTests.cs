using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Expressions;
using NSubstitute;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>
/// The simulator evaluates a command's access rule (spec 2026-10-14 §7 HX-08 dashboard row; §6.2 simulator parity): it asks
/// the judge the command filter asks (Ruling 33).
/// </summary>
public sealed class CommandSimulatorTests
{
    private const string Blocked = "0b0e0c2a-1b1b-4c4c-8d8d-9e9e9e9e9e9e";

    private static readonly AlvoContext _staff = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated, Role.Application("staff") } };

    [Fact]
    public void A_caller_only_rule_is_answered_for_the_simulated_caller()
    {
        var simulator = Simulator(out _);

        simulator.Simulate("tickets", _staff).Single(verdict => verdict.Name == "quote_stay").Allowed.ShouldBe(true);
        simulator.Simulate("tickets", AlvoContext.Anonymous).Single(verdict => verdict.Name == "quote_stay").Allowed.ShouldBe(false);
    }

    [Fact]
    public void A_rule_reading_the_row_is_decided_per_row()
    {
        var verdict = Simulator(out _).Simulate("tickets", _staff).Single(candidate => candidate.Name == "resolve_ticket");

        verdict.Allowed.ShouldBeNull();
        verdict.Entity.ShouldBe("tickets");
        verdict.Access.ShouldBe("status == 'open' && 'staff' in @user.roles");
    }

    [Fact]
    public void A_negated_rule_previews_refused_for_a_caller_without_the_value() =>
        Simulator(out _).Simulate("tickets", _staff).Single(verdict => verdict.Name == "not_blocked").Allowed
            .ShouldBe(false, "the staff caller carries no tenant, so the required-context gate refuses before the negation");

    [Fact]
    public void An_entity_lists_its_own_commands_and_the_unbound_ones_only() =>
        Simulator(out _).Simulate("notes", _staff).Select(verdict => verdict.Name).ShouldBe(["quote_stay", "not_blocked"]);

    [Fact]
    public void The_simulator_answers_exactly_as_the_judge_admits()
    {
        var simulator = Simulator(out var judge);

        foreach (var caller in new[] { _staff, AlvoContext.Anonymous, AlvoContext.System(null), _staff with { Tenant = new TenantId(Guid.NewGuid()) } })
        {
            foreach (var verdict in simulator.Simulate("notes", caller))
            {
                var admitted = Record.Exception(() => judge.EnsureAdmitted(verdict.Name, null, caller)) is null;
                verdict.Allowed.ShouldBe(admitted, $"{verdict.Name} for {caller.User}");
            }
        }
    }

    [Fact]
    public void An_entity_bound_command_answers_exactly_as_the_judge_admits_it_on_a_row()
    {
        var simulator = Simulator(out var judge);
        var row = new AlvoRecord(new Dictionary<string, object?> { ["id"] = Guid.NewGuid(), ["status"] = "open" });

        foreach (var caller in new[] { _staff, AlvoContext.Anonymous, AlvoContext.System(null), _staff with { Tenant = new TenantId(Guid.NewGuid()) } })
        {
            foreach (var verdict in simulator.Simulate("tickets", caller).Where(verdict => verdict.Allowed is not null))
            {
                var admitted = Record.Exception(() => judge.EnsureAdmitted(verdict.Name, verdict.Entity is null ? null : row, caller)) is null;
                verdict.Allowed.ShouldBe(admitted, $"{verdict.Name} for {caller.User}");
            }
        }
    }

    [Fact]
    public void A_rule_reading_the_row_previews_refused_not_per_row_for_a_caller_without_what_it_reads()
    {
        var simulator = Simulator(out _);

        simulator.Simulate("tickets", _staff).Single(verdict => verdict.Name == "close_ticket").Allowed
            .ShouldBe(false, "the staff caller carries no tenant, so the required-context gate refuses before any row is read");
        simulator.Simulate("tickets", _staff with { Tenant = new TenantId(Guid.NewGuid()) }).Single(verdict => verdict.Name == "close_ticket").Allowed
            .ShouldBeNull();
    }

    private static CommandSimulator Simulator(out CommandAccessJudge judge)
    {
        var services = new ServiceCollection();
        services.AddAlvo();
        var provider = services.BuildServiceProvider();
        var commands = provider.GetRequiredService<CommandRegistry>();
        commands.Map("quote_stay", "/q", null, CommandAccess.Of("'staff' in @user.roles"));
        commands.Map("resolve_ticket", "/t/{id}", "tickets", CommandAccess.Of("status == 'open' && 'staff' in @user.roles"));
        commands.Map("not_blocked", "/n", null, CommandAccess.Of($"!(string(@tenant.id) == '{Blocked}')"));
        commands.Map("assign_ticket", "/a/{id}", "tickets", CommandAccess.Of("'staff' in @user.roles && string(@tenant.id) != ''"));
        commands.Map("close_ticket", "/c/{id}", "tickets", CommandAccess.Of("status == 'open' && string(@tenant.id) != ''"));
        var catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns(Catalog());
        var registry = provider.GetRequiredService<ExtensionRegistry>();
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, EveryService(), NullLogger<ExtensionBinder>.Instance);
        judge = new CommandAccessJudge(binder, catalogs, provider.GetRequiredService<IPredicateEvaluator>());

        return new CommandSimulator(registry, judge);
    }
}
