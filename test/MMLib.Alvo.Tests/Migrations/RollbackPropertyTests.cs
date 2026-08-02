using CsCheck;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Migrations;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// Property: rolling back from a field-set superset restores the exact prior field set. Driven at
/// the <see cref="RuntimeSchemaService"/> level (real <see cref="DescriptorValidator"/> +
/// <see cref="DescriptorToSchemaMapper"/> + <see cref="InMemoryDescriptorVersionStore"/>/<see
/// cref="InMemoryRuntimeSchemaWriter"/>/<see cref="InMemorySchemaMigrator"/> fakes), not at the bare
/// <see cref="SchemaDiff"/> level: <see cref="InMemorySchemaMigrator"/> applies a plan by projecting
/// the desired model rather than by interpreting its steps (see <see cref="MigrationPlan.Sql"/>'s
/// remarks), so a diff-only property there would be tautological. Generating valid descriptor JSON
/// turned out to be practical (a fixed base field plus a randomized subset of a small field pool),
/// so the property drives the full apply/apply/rollback path a real caller would use.
/// </summary>
public sealed class RollbackPropertyTests
{
    private static readonly FieldSpec[] _fieldPool =
    [
        new("notes", """{"type":"string","maxLength":200}"""),
        new("bio", """{"type":"text"}"""),
        new("score", """{"type":"integer"}"""),
        new("active", """{"type":"boolean"}"""),
        new("external_ref", """{"type":"uuid"}"""),
        new("archived_at", """{"type":"datetime"}"""),
        new("birth_date", """{"type":"date"}"""),
        new("nickname", """{"type":"string"}"""),
    ];

    private static readonly Gen<(FieldSpec[] Shuffled, int SplitA, int SplitB)> _fieldSplitGen =
        Gen.Shuffle(_fieldPool)
            .SelectMany(shuffled => Gen.Int[0, _fieldPool.Length - 1], (shuffled, splitA) => (shuffled, splitA))
            .SelectMany(t => Gen.Int[t.splitA + 1, _fieldPool.Length], (t, splitB) => (t.shuffled, t.splitA, splitB));

    [Fact]
    public async Task Rollback_from_a_superset_restores_the_prior_field_set()
    {
        var ct = TestContext.Current.CancellationToken;

        await _fieldSplitGen.SampleAsync(sample => AssertRollbackRestoresFieldSetAsync(sample, ct));
    }

    private static async Task AssertRollbackRestoresFieldSetAsync((FieldSpec[] Shuffled, int SplitA, int SplitB) sample, CancellationToken ct)
    {
        var fieldsA = sample.Shuffled.Take(sample.SplitA).ToArray();
        var fieldsB = sample.Shuffled.Take(sample.SplitB).ToArray();
        var jsonA = DescriptorJson(fieldsA);
        var jsonB = DescriptorJson(fieldsB);

        var (service, store) = CreateService();
        await service.ApplyAsync("demo", jsonA, expectedRevision: 0, new MigrationOptions(), ct);
        await service.ApplyAsync("demo", jsonB, expectedRevision: 1, new MigrationOptions(), ct);

        await service.RollbackAsync("demo", targetRevision: 1, new MigrationOptions { AllowDestructive = true }, ct);

        var current = await store.GetCurrentAsync("demo", ct);
        current!.DescriptorJson.ShouldBe(jsonA);
        FieldNames(current.Schema).ShouldBe(ExpectedFieldNames(fieldsA), ignoreOrder: true);
    }

    // DescriptorToSchemaMapper injects the framework-managed "id" column for every physical
    // entity that does not declare one itself (see DescriptorToSchemaMapper.MapEntity).
    private static HashSet<string> ExpectedFieldNames(IEnumerable<FieldSpec> extraFields) =>
        extraFields.Select(f => f.Name).Append("title").Append("id").ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> FieldNames(SchemaModel schema) =>
        schema.Entities.Single(e => e.Name == "tasks").Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

    private static string DescriptorJson(IEnumerable<FieldSpec> extraFields)
    {
        var fields = new JsonObject { ["title"] = new JsonObject { ["type"] = "string", ["required"] = true } };
        foreach (var field in extraFields)
        {
            fields[field.Name] = JsonNode.Parse(field.TypeJson);
        }

        var descriptor = new JsonObject
        {
            ["apiVersion"] = "alvo.dev/v1",
            ["name"] = "demo",
            ["entities"] = new JsonObject { ["tasks"] = new JsonObject { ["fields"] = fields } },
        };
        return descriptor.ToJsonString();
    }

    // Mirrors RuntimeSchemaServiceTests.CreateService: the same store instance must back both the
    // writer (which delegates its append there) and the service (its version-history read port).
    private static (RuntimeSchemaService Service, InMemoryDescriptorVersionStore Store) CreateService()
    {
        var store = new InMemoryDescriptorVersionStore();
        var writer = new InMemoryRuntimeSchemaWriter(store);
        var migrator = new InMemorySchemaMigrator();
        var validator = new DescriptorValidator();
        return (new RuntimeSchemaService(validator, migrator, store, writer, new CelCompiler(), new PolicyCatalogProvider()), store);
    }

    private sealed record FieldSpec(string Name, string TypeJson);
}
