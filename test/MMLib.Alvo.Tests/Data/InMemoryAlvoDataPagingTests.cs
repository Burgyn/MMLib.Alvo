using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// The concrete run of <see cref="AlvoDataPagingTests"/> over <see cref="InMemoryAlvoData"/> — the leg the
/// reference implementation was missing: its <c>Offset</c>, <c>Limit + 1</c> over-fetch, and cursor code had
/// no direct coverage until this ran, even though the shipped SQLite/PostgreSQL backends already were.
/// </summary>
public class InMemoryAlvoDataPagingTests : AlvoDataPagingTests
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
