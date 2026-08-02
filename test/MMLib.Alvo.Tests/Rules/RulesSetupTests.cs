using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Rules;

public class RulesSetupTests
{
    [Fact]
    public void AddAlvo_resolves_the_policy_engine()
    {
        var services = new ServiceCollection();
        services.AddAlvo();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IPolicyEngine>().ShouldNotBeNull();
    }

    /// <summary>
    /// Task 13, Step 4: the policy catalog is primed at apply time, never registered eagerly —
    /// resolving <see cref="IPolicyEngine"/> from a fully configured container, after priming it
    /// from a real descriptor, must judge callers against that descriptor's actual rules rather
    /// than an empty fallback catalog.
    /// </summary>
    [Fact]
    public void AddAlvo_resolves_a_policy_engine_that_judges_against_the_primed_descriptor()
    {
        var services = new ServiceCollection();
        services.AddAlvo();
        using var provider = services.BuildServiceProvider();

        var descriptor = AlvoDescriptor.Parse("""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "orders": {
            "fields": { "owner_id": { "type": "uuid" } },
            "rules": { "list": "owner_id == @user.id" }
          } } }
        """);
        var schema = DescriptorToSchemaMapper.Map(descriptor);
        PolicyCatalogPriming.Prime(
            provider.GetRequiredService<IPolicyCatalogProvider>(),
            provider.GetRequiredService<ICelCompiler>(),
            descriptor.Name,
            descriptor,
            schema);

        var engine = provider.GetRequiredService<IPolicyEngine>();
        var context = new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

        var decision = engine.Resolve("orders", DataOperation.List, context);

        decision.IsDenied.ShouldBeFalse();
        decision.Using.ShouldNotBeNull();

        var deniedForUnknownEntity = engine.Resolve("no_such_entity", DataOperation.List, context);
        deniedForUnknownEntity.IsDenied.ShouldBeTrue();
    }

    [Fact]
    public void The_schema_registry_resolves_to_the_policy_catalog_provider_instance()
    {
        var services = new ServiceCollection();
        services.AddAlvoRules();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISchemaRegistry>()
            .ShouldBeSameAs(provider.GetRequiredService<IPolicyCatalogProvider>());
    }
}
