using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Tests.Data;

/// <summary>The in-memory reference's leg of the literal text tests: ordinal string tests, the answer the engines are held to.</summary>
public sealed class InMemoryAlvoDataLiteralTextTests : AlvoDataLiteralTextTests
{
    protected override Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed)
    {
        var catalog = PolicyCatalog.Build(descriptor, schema, MMLib.Alvo.Tests.Expressions.CelFixtures.Compiler);
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent(descriptor.Name, catalog);

        var data = new InMemoryAlvoData(new PolicyEngine(provider), new PredicateEvaluator(), schema);
        foreach (var (entity, rows) in seed)
        {
            data.Seed(entity, [.. rows]);
        }

        return Task.FromResult<IAlvoData>(data);
    }
}
