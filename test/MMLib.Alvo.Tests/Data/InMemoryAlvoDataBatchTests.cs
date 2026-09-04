using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// The concrete run of <see cref="AlvoDataBatchTests"/> over <see cref="InMemoryAlvoData"/> — the reference
/// the shipped backends are held to, so it may not be laxer: a batch it committed row by row, or a per-row
/// check it skipped, would teach a driver author the wrong contract from the inherited suite.
/// </summary>
public sealed class InMemoryAlvoDataBatchTests : AlvoDataBatchTests
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
