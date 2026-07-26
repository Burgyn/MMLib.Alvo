using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Testing.Migrations;
using MMLib.Alvo.Tests.Expressions;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// End-to-end coverage for Important 4: <see cref="IPolicyCatalogProvider"/> is primed by
/// <see cref="RuntimeSchemaService"/> at apply time, never lazily inside <c>IPolicyEngine.Resolve</c>,
/// and a re-apply takes effect for the very next <c>Resolve</c> call — no process restart, no
/// blocking wait, no cached load failure.
/// </summary>
public class PolicyCatalogPrimingTests
{
    private const int Contenders = 8;
    private const int Reads = 10_000;

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
        var (service, provider, _) = CreateService();
        var engine = new PolicyEngine(provider);

        await service.ApplyAsync("demo", Descriptor("""{"list": "true"}"""), expectedRevision: 0, new MigrationOptions(), TestContext.Current.CancellationToken);

        engine.Resolve("orders", DataOperation.List, CelFixtures.Alice).IsDenied.ShouldBeFalse();
    }

    /// <summary>The security-relevant case: a re-apply that tightens (here, revokes) a rule takes effect without a restart.</summary>
    [Fact]
    public async Task A_re_apply_that_tightens_a_rule_takes_effect_without_a_restart()
    {
        var (service, provider, _) = CreateService();
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
        var (service, provider, _) = CreateService();
        var engine = new PolicyEngine(provider);
        await service.ApplyAsync("demo", Descriptor(null), expectedRevision: 0, new MigrationOptions(), TestContext.Current.CancellationToken);
        engine.Resolve("orders", DataOperation.List, CelFixtures.Alice).IsDenied.ShouldBeTrue();

        await service.ApplyAsync("demo", Descriptor("""{"list": "true"}"""), expectedRevision: 1, new MigrationOptions(), TestContext.Current.CancellationToken);

        engine.Resolve("orders", DataOperation.List, CelFixtures.Alice).IsDenied.ShouldBeFalse();
    }

    /// <summary>Finding 1: a rule that fails to compile must reject the apply before the schema/version becomes durable, leaving the previously primed catalog in effect.</summary>
    [Fact]
    public async Task An_apply_whose_rules_fail_to_compile_leaves_the_schema_and_the_previous_catalog_untouched()
    {
        var (service, provider, store) = CreateService();
        var engine = new PolicyEngine(provider);
        await service.ApplyAsync("demo", Descriptor("""{"list": "true"}"""), expectedRevision: 0, new MigrationOptions(), TestContext.Current.CancellationToken);
        engine.Resolve("orders", DataOperation.List, CelFixtures.Alice).IsDenied.ShouldBeFalse();

        var uncompilable = Descriptor("""{"list": "@user.role == 'admin'"}""");
        await Should.ThrowAsync<DescriptorValidationException>(
            () => service.ApplyAsync("demo", uncompilable, expectedRevision: 1, new MigrationOptions(), TestContext.Current.CancellationToken));

        var current = await store.GetCurrentAsync("demo", TestContext.Current.CancellationToken);
        current!.Revision.ShouldBe(1);
        current.DescriptorJson.ShouldBe(Descriptor("""{"list": "true"}"""));
        engine.Resolve("orders", DataOperation.List, CelFixtures.Alice).IsDenied.ShouldBeFalse();
    }

    /// <summary>Finding 4: a rules-only change (same fields, different rule text) plans empty but must still append a version — the old plan.IsEmpty no-op would silently lose it.</summary>
    [Fact]
    public async Task A_rules_only_change_appends_a_new_version()
    {
        var (service, _, store) = CreateService();
        await service.ApplyAsync("demo", Descriptor("""{"list": "true"}"""), expectedRevision: 0, new MigrationOptions(), TestContext.Current.CancellationToken);

        await service.ApplyAsync("demo", Descriptor(null), expectedRevision: 1, new MigrationOptions(), TestContext.Current.CancellationToken);

        (await store.ListAsync("demo", TestContext.Current.CancellationToken)).Count.ShouldBe(2);
    }

    /// <summary>Finding 4's mirror: re-applying a byte-identical descriptor must not append a version, even though it too plans empty.</summary>
    [Fact]
    public async Task Re_applying_an_identical_descriptor_appends_nothing()
    {
        var (service, _, store) = CreateService();
        var descriptor = Descriptor("""{"list": "true"}""");
        var first = await service.ApplyAsync("demo", descriptor, expectedRevision: 0, new MigrationOptions(), TestContext.Current.CancellationToken);

        var second = await service.ApplyAsync("demo", descriptor, expectedRevision: 1, new MigrationOptions(), TestContext.Current.CancellationToken);

        second.ShouldBe(first);
        (await store.ListAsync("demo", TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    /// <summary>Finding 3: this provider is a single global slot; priming it for a second project must throw rather than silently mixing the two projects' rules.</summary>
    [Fact]
    public void SetCurrent_for_a_different_project_throws()
    {
        var provider = new PolicyCatalogProvider();
        var catalog = NewCatalog();
        provider.SetCurrent("alpha", catalog);

        var exception = Should.Throw<InvalidOperationException>(() => provider.SetCurrent("beta", catalog));

        exception.Message.ShouldContain("alpha");
        exception.Message.ShouldContain("beta");
    }

    /// <summary>
    /// The project-identity guard and the publish are one atomic step under one lock. Eight threads
    /// re-prime the admitted project while eight more try to prime a second one and a ninth spins on
    /// <see cref="IPolicyCatalogProvider.Current"/>: every refusal must still name the admitted
    /// project, and a reader must only ever observe a catalog that project published — never the
    /// refused project's, and never an empty slot that would make <c>IPolicyEngine</c> deny mid-flight.
    /// </summary>
    [Fact]
    public async Task Concurrent_primes_publish_only_the_admitted_projects_catalogs()
    {
        var provider = new PolicyCatalogProvider();
        var admitted = Enumerable.Range(0, Contenders).Select(_ => NewCatalog()).ToHashSet();
        var observed = new ConcurrentBag<PolicyCatalog?>();
        var refusals = new ConcurrentBag<string>();
        provider.SetCurrent("alpha", admitted.First());

        using var start = new Barrier((Contenders * 2) + 1);
        await Task.WhenAll([
            .. admitted.Select(catalog => Primed(start, () => provider.SetCurrent("alpha", catalog))),
            .. Enumerable.Range(0, Contenders).Select(_ => Primed(start, () => Refuse(provider, refusals))),
            Reading(start, provider, observed)]);

        refusals.Count.ShouldBe(Contenders);
        refusals.ShouldAllBe(message => message.Contains("alpha", StringComparison.Ordinal));
        observed.ShouldAllBe(catalog => catalog != null && admitted.Contains(catalog));
        var current = provider.Current;
        current.ShouldNotBeNull();
        admitted.ShouldContain(current);
    }

    private static Task Primed(Barrier start, Action prime) => Task.Run(() =>
    {
        start.SignalAndWait();
        prime();
    });

    private static void Refuse(PolicyCatalogProvider provider, ConcurrentBag<string> refusals) =>
        refusals.Add(Should.Throw<InvalidOperationException>(() => provider.SetCurrent("beta", NewCatalog())).Message);

    private static Task Reading(Barrier start, PolicyCatalogProvider provider, ConcurrentBag<PolicyCatalog?> observed) =>
        Task.Run(() =>
        {
            start.SignalAndWait();
            for (var read = 0; read < Reads; read++)
            {
                observed.Add(provider.Current);
            }
        });

    private static PolicyCatalog NewCatalog()
    {
        var descriptor = AlvoDescriptor.Parse(Descriptor("""{"list": "true"}"""));
        return PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler());
    }

    private static (RuntimeSchemaService Service, PolicyCatalogProvider Provider, InMemoryDescriptorVersionStore Store) CreateService()
    {
        var store = new InMemoryDescriptorVersionStore();
        var writer = new InMemoryRuntimeSchemaWriter(store);
        var migrator = new InMemorySchemaMigrator();
        var validator = new DescriptorValidator();
        var provider = new PolicyCatalogProvider();
        var service = new RuntimeSchemaService(validator, migrator, store, writer, new CelCompiler(), provider);
        return (service, provider, store);
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
