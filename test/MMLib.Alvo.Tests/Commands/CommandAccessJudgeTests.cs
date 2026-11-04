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
/// The one judge of command access (Ruling 33): a caller lacking a context value the rule reads is refused before the rule
/// runs — so a negation over an absent value never admits.
/// </summary>
public sealed class CommandAccessJudgeTests
{
    private const string Blocked = "0b0e0c2a-1b1b-4c4c-8d8d-9e9e9e9e9e9e";

    private static readonly AlvoContext _tenantless = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    [Fact]
    public void A_negated_tenant_rule_refuses_a_tenantless_caller_instead_of_admitting_it()
    {
        var judge = Judge(out _);

        Should.Throw<AlvoAuthorizationException>(() => judge.EnsureAdmitted("not_blocked", null, _tenantless))
            .Message.ShouldBe(CommandAccessJudge.MissingTenant);
        Should.Throw<AlvoAuthorizationException>(() => judge.EnsureAdmitted("not_blocked", null, AlvoContext.Anonymous));
    }

    [Fact]
    public void A_negated_tenant_rule_admits_another_tenant_and_refuses_the_blocked_one()
    {
        var judge = Judge(out _);

        Should.NotThrow(() => judge.EnsureAdmitted("not_blocked", null, _tenantless with { Tenant = new TenantId(Guid.NewGuid()) }));
        Should.Throw<AlvoAuthorizationException>(() => judge.EnsureAdmitted("not_blocked", null, _tenantless with { Tenant = new TenantId(Guid.Parse(Blocked)) }))
            .Message.ShouldBe(CommandAccessJudge.Refused);
    }

    [Fact]
    public void A_negated_identity_rule_refuses_the_anonymous_caller() =>
        Should.Throw<AlvoAuthorizationException>(() => Judge(out _).EnsureAdmitted("not_banned", null, AlvoContext.Anonymous))
            .Message.ShouldBe(CommandAccessJudge.MissingUser);

    [Fact]
    public void The_preview_refuses_whom_the_judge_refuses()
    {
        var judge = Judge(out _);

        judge.Preview("not_blocked", _tenantless).ShouldBe(false);
        judge.Preview("not_banned", AlvoContext.Anonymous).ShouldBe(false);
        judge.Preview("not_banned", _tenantless).ShouldBe(true);
    }

    [Fact]
    public void An_entity_command_judged_without_its_row_is_refused_instead_of_reading_an_empty_one()
    {
        var judge = Judge(out _);

        Should.Throw<AlvoAuthorizationException>(() => judge.EnsureAdmitted("not_resolved", null, _tenantless))
            .Message.ShouldBe(CommandAccessJudge.RowMissing);
        Should.NotThrow(() => judge.EnsureAdmitted("not_resolved", Ticket("open"), _tenantless));
        Should.Throw<AlvoAuthorizationException>(() => judge.EnsureAdmitted("not_resolved", Ticket("resolved"), _tenantless))
            .Message.ShouldBe(CommandAccessJudge.Refused);
    }

    [Fact]
    public void A_command_with_no_bound_catalog_is_refused_as_not_bound()
    {
        var judge = Judge(out var catalogs);
        catalogs.Current.Returns((PolicyCatalog?)null);

        Should.Throw<AlvoAuthorizationException>(() => judge.EnsureAdmitted("not_banned", null, _tenantless)).Message.ShouldBe(CommandAccessJudge.NotBound);
        judge.Preview("not_banned", _tenantless).ShouldBe(false);
    }

    private static CommandAccessJudge Judge(out IPolicyCatalogProvider catalogs)
    {
        var services = new ServiceCollection();
        services.AddAlvo();
        var provider = services.BuildServiceProvider();
        var commands = provider.GetRequiredService<CommandRegistry>();
        commands.Map("not_blocked", "/a", null, CommandAccess.Of($"!(string(@tenant.id) == '{Blocked}')"));
        commands.Map("not_banned", "/b", null, CommandAccess.Of("!(string(@user.id) == '6c1f9a3e-0000-4000-8000-000000000001')"));
        commands.Map("not_resolved", "/c/{id}", "tickets", CommandAccess.Of("!(status == 'resolved')"));
        catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns(Catalog());
        var binder = new ExtensionBinder(provider.GetRequiredService<ExtensionRegistry>(), CelFixtures.Compiler, EveryService(), NullLogger<ExtensionBinder>.Instance);

        return new CommandAccessJudge(binder, catalogs, provider.GetRequiredService<IPredicateEvaluator>());
    }

    private static AlvoRecord Ticket(string status) =>
        new(new Dictionary<string, object?> { ["id"] = Guid.NewGuid(), ["title"] = "Printer jam", ["status"] = status });
}
