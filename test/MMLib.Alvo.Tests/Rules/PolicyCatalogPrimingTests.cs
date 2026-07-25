using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Testing.Migrations;
using MMLib.Alvo.Tests.Expressions;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// End-to-end coverage for Important 4: <see cref="IPolicyCatalogProvider"/> is primed by
/// <see cref="RuntimeSchemaService"/> at apply time, never lazily inside <c>IPolicyEngine.Resolve</c>,
/// and a re-apply takes effect for the very next <c>Resolve</c> call — no process restart, no
/// blocking wait, no cached load failure.
/// </summary>
public class PolicyCatalogPrimingTests
{
    [Fact]
    public void An_unprimed_provider_denies_with_a_clear_reason()
    {
        var engine = new PolicyEngine(new PolicyCatalogProvider());

        var decision = engine.Resolve("orders", DataOperation.List, CelFixtures.Alice);

        decision.IsDenied.ShouldBeTrue();
        decision.DenyReason.ShouldNotBeNull();
        decision.DenyReason.ShouldContain("applied");
    }

    [Fact]
    public async Task A_successful_apply_primes_the_catalog_for_the_very_next_resolve()
    {
        var (service, provider) = CreateService();
        var engine = new PolicyEngine(provider);

        await service.ApplyAsync("demo", Descriptor("""{"list": "true"}"""), expectedRevision: 0, new MigrationOptions(), TestContext.Current.CancellationToken);

        engine.Resolve("orders", DataOperation.List, CelFixtures.Alice).IsDenied.ShouldBeFalse();
    }

    /// <summary>The security-relevant case: a re-apply that tightens (here, revokes) a rule takes effect without a restart.</summary>
    [Fact]
    public async Task A_re_apply_that_tightens_a_rule_takes_effect_without_a_restart()
    {
        var (service, provider) = CreateService();
        var engine = new PolicyEngine(provider);
        await service.ApplyAsync("demo", Descriptor("""{"list": "true"}"""), expectedRevision: 0, new MigrationOptions(), TestContext.Current.CancellationToken);
        engine.Resolve("orders", DataOperation.List, CelFixtures.Alice).IsDenied.ShouldBeFalse();

        await service.ApplyAsync("demo", Descriptor(null), expectedRevision: 1, new MigrationOptions(), TestContext.Current.CancellationToken);

        engine.Resolve("orders", DataOperation.List, CelFixtures.Alice).IsDenied.ShouldBeTrue();
    }

    /// <summary>The mirror case: a re-apply that loosens a rule (previously absent, now <c>"true"</c>) also takes effect immediately.</summary>
    [Fact]
    public async Task A_re_apply_that_loosens_a_rule_also_takes_effect_without_a_restart()
    {
        var (service, provider) = CreateService();
        var engine = new PolicyEngine(provider);
        await service.ApplyAsync("demo", Descriptor(null), expectedRevision: 0, new MigrationOptions(), TestContext.Current.CancellationToken);
        engine.Resolve("orders", DataOperation.List, CelFixtures.Alice).IsDenied.ShouldBeTrue();

        await service.ApplyAsync("demo", Descriptor("""{"list": "true"}"""), expectedRevision: 1, new MigrationOptions(), TestContext.Current.CancellationToken);

        engine.Resolve("orders", DataOperation.List, CelFixtures.Alice).IsDenied.ShouldBeFalse();
    }

    private static (RuntimeSchemaService Service, PolicyCatalogProvider Provider) CreateService()
    {
        var store = new InMemoryDescriptorVersionStore();
        var writer = new InMemoryRuntimeSchemaWriter(store);
        var migrator = new InMemorySchemaMigrator();
        var validator = new DescriptorValidator();
        var provider = new PolicyCatalogProvider();
        var service = new RuntimeSchemaService(validator, migrator, store, writer, new CelCompiler(), provider);
        return (service, provider);
    }

    private static string Descriptor(string? rulesJson) => $$"""
    {
      "apiVersion": "alvo.dev/v1",
      "name": "demo",
      "entities": {
        "orders": {
          "fields": {
            "title": { "type": "string", "required": true }
          }{{(rulesJson is null ? "" : $$""", "rules": {{rulesJson}}""")}}
        }
      }
    }
    """;
}
