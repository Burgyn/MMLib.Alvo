using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// The reference implementation's leg of the projection suite. It has no <c>SELECT</c> list, so for it the
/// projection is a second field mask — which is exactly why it belongs here: the observable rule is that
/// all three implementations answer the same key set, and a reference that answered a different one would
/// make every driver's projection unprovable.
/// </summary>
public class InMemoryAlvoDataProjectionTests : AlvoDataProjectionTests
{
    protected override Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed)
    {
        var catalog = PolicyCatalog.Build(descriptor, schema, MMLib.Alvo.Tests.Expressions.CelFixtures.Compiler);
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent(descriptor.Name, catalog);
        var engine = new PolicyEngine(provider);
        var evaluator = new PredicateEvaluator();

        var data = new InMemoryAlvoData(engine, evaluator, schema);
        foreach (var (entity, rows) in seed)
        {
            data.Seed(entity, [.. rows]);
        }

        return Task.FromResult<IAlvoData>(data);
    }
}
