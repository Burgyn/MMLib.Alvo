using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using DescField = MMLib.Alvo.Descriptor.FieldType;

namespace MMLib.Alvo.Tests.Rules;

public class PolicyCatalogProviderSchemaTests
{
    /// <summary>
    /// The invariant the port exists for: one instance answers both questions, so the schema a data port
    /// validates against can never be a different apply's from the rules that judge the same request.
    /// </summary>
    [Fact]
    public void The_schema_registry_and_the_policy_catalog_provider_are_one_instance()
    {
        using var services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

        services.GetRequiredService<ISchemaRegistry>()
            .ShouldBeSameAs(services.GetRequiredService<IPolicyCatalogProvider>());
    }

    /// <summary>
    /// A host with its own schema source registers its own <see cref="ISchemaRegistry"/> and takes it
    /// over — the same escape hatch <see cref="IRoleCatalogProvider"/> gives an external identity source.
    /// </summary>
    [Fact]
    public void A_host_can_replace_the_schema_registry_without_touching_the_policy_catalog()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<ISchemaRegistry>(new FixedSchemaRegistry(new SchemaModel([])));
        collection.AddAlvo();
        using var services = collection.BuildServiceProvider();

        services.GetRequiredService<ISchemaRegistry>().ShouldBeOfType<FixedSchemaRegistry>();
        services.GetRequiredService<IPolicyCatalogProvider>().ShouldNotBeNull();
    }

    [Fact]
    public void An_unprimed_registry_declares_no_entity_rather_than_throwing()
    {
        using var services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

        services.GetRequiredService<ISchemaRegistry>().GetSchema().Entities.ShouldBeEmpty();
    }

    [Fact]
    public void Priming_the_policy_catalog_also_publishes_the_schema_it_was_compiled_against()
    {
        using var services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();
        var (descriptor, schema) = Fixture("vehicle");

        services.GetRequiredService<IPolicyCatalogProvider>().SetCurrent(
            descriptor.Name, PolicyCatalog.Build(descriptor, schema, services.GetRequiredService<ICelCompiler>()));

        var published = services.GetRequiredService<ISchemaRegistry>().GetSchema();
        published.ShouldBeSameAs(schema);
    }

    [Fact]
    public void Re_priming_publishes_the_new_schema_not_the_previous_one()
    {
        using var services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();
        var compiler = services.GetRequiredService<ICelCompiler>();
        var catalogs = services.GetRequiredService<IPolicyCatalogProvider>();
        var (first, firstSchema) = Fixture("vehicle");
        var (second, secondSchema) = Fixture("vehicle", extraField: "colour");

        catalogs.SetCurrent(first.Name, PolicyCatalog.Build(first, firstSchema, compiler));
        catalogs.SetCurrent(second.Name, PolicyCatalog.Build(second, secondSchema, compiler));

        var published = services.GetRequiredService<ISchemaRegistry>().GetSchema();
        published.ShouldBeSameAs(secondSchema);
        published.Entities[0].Fields.Select(f => f.Name).ShouldContain("colour");
    }

    private static (AlvoDescriptor Descriptor, SchemaModel Schema) Fixture(string entity, string? extraField = null)
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["plate"] = new() { Type = DescField.String },
        };
        if (extraField is not null)
        {
            fields[extraField] = new FieldDescriptor { Type = DescField.String };
        }

        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "primed-registry",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [entity] = new() { Fields = fields, Rules = new AccessRules { List = "true" } },
            },
        };

        return (descriptor, DescriptorToSchemaMapper.Map(descriptor));
    }

    private sealed class FixedSchemaRegistry(SchemaModel schema) : ISchemaRegistry
    {
        public SchemaModel GetSchema() => schema;
    }
}
