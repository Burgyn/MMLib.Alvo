using Microsoft.Extensions.Options;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Lifecycle;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;

namespace MMLib.Alvo.Tests.Lifecycle;

/// <summary>
/// The data path's lifecycle reads the compiled machine off the write's own decision — the catalog revision its rules
/// came from — and never the catalog an apply may have published since.
/// </summary>
public sealed class EntityLifecycleServiceTests
{
    private const string Machine =
        """, "stateMachine": {"field": "status", "transitions": {"confirm": {"from": ["draft"], "to": "confirmed"}}}""";

    private const string Timers = """, "timers": {"reminder": {"at": "starts_at"}}""";

    private static readonly AlvoContext _caller = AlvoContext.System(null);

    private readonly EntityLifecycleService _service = new(new CelFunctionBudgetPolicy(Options.Create(new AlvoExtensionOptions()), TimeProvider.System), AlvoChainDepthLimit.Default);

    [Fact]
    public void The_guard_judges_the_decision_s_catalog_not_the_one_published_since()
    {
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent("guards", Catalog(Machine));
        var decision = new PolicyEngine(provider).Resolve("bookings", DataOperation.Update, _caller);
        provider.SetCurrent("guards", Catalog(string.Empty));

        Should.Throw<AlvoConstraintViolationException>(() => _service.EnsureTransition(decision, DataOperation.Update, Row("done"), Row("draft"), _caller))
            .Kind.ShouldBe(AlvoConstraintKind.Transition);
        Should.NotThrow(() => _service.EnsureTransition(decision, DataOperation.Update, Row("confirmed"), Row("draft"), _caller));
    }

    [Fact]
    public void An_entity_without_a_machine_admits_every_move() =>
        Should.NotThrow(() => _service.EnsureTransition(Decision(string.Empty), DataOperation.Update, Row("done"), Row("draft"), _caller));

    [Fact]
    public void Only_an_entity_declaring_timers_arms_them()
    {
        _service.ArmsTimers(Decision(Timers)).ShouldBeTrue();
        _service.ArmsTimers(Decision(Machine)).ShouldBeFalse();
    }

    [Fact]
    public void A_denied_decision_never_reaches_the_guard() =>
        Should.Throw<InvalidOperationException>(() => _service.EnsureTransition(
            PolicyDecision.Deny("no"), DataOperation.Update, Row("done"), Row("draft"), _caller));

    private static PolicyDecision Decision(string members)
    {
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent("guards", Catalog(members));
        return new PolicyEngine(provider).Resolve("bookings", DataOperation.Update, _caller);
    }

    private static AlvoRecord Row(string state) => new(new Dictionary<string, object?> { ["status"] = state });

    private static PolicyCatalog Catalog(string members)
    {
        var descriptor = AlvoDescriptor.Parse(
            """{"apiVersion": "alvo.dev/v1", "name": "guards", "entities": {"bookings": {"fields": {"""
            + """ "status": {"type": "enum", "values": ["draft", "confirmed", "done"]}, "starts_at": {"type": "datetime"}}"""
            + members
            + """, "rules": {"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}}}}""");
        return PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler());
    }
}
