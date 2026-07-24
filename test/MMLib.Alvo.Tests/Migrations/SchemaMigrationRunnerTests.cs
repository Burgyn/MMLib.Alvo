using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Migrations;
using NSubstitute;
using NSubstitute.Core;
using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Migrations;

public sealed class SchemaMigrationRunnerTests
{
    private const string FleetDescriptorJson = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "fleet",
          "entities": {
            "vehicles": {
              "fields": {
                "vin": { "type": "string", "required": true, "maxLength": 17 },
                "make": { "type": "string" }
              }
            }
          }
        }
        """;

    private static readonly string[] _expectedFieldNames = ["id", "vin", "make"];

    private readonly IDescriptorSource _source = Substitute.For<IDescriptorSource>();
    private readonly ISchemaIntrospector _introspector = Substitute.For<ISchemaIntrospector>();
    private readonly IAppliedSchemaStore _store = Substitute.For<IAppliedSchemaStore>();
    private readonly InMemorySchemaMigrator _migrator = new();
    private readonly SchemaMigrationRunner _runner;

    public SchemaMigrationRunnerTests()
    {
        _source.LoadAsync(Arg.Any<CancellationToken>()).Returns(FleetDescriptorJson);
        _runner = new SchemaMigrationRunner(_source, _migrator, _introspector, _store);
    }

    [Fact]
    public async Task First_run_against_empty_database_applies_create_plan_and_saves_revision_1()
    {
        _store.GetCurrentAsync("fleet", Arg.Any<CancellationToken>()).Returns((AppliedSchema?)null);
        _introspector.IntrospectAsync(Arg.Any<CancellationToken>()).Returns(new SchemaModel([]));

        var result = await _runner.RunAsync(new MigrationOptions(), TestContext.Current.CancellationToken);

        result.Applied.ShouldBeTrue();
        result.Plan.IsEmpty.ShouldBeFalse();
        result.Plan.HasDestructiveChanges.ShouldBeFalse();

        var saved = SavedSchemas().ShouldHaveSingleItem();
        saved.Revision.ShouldBe(1);
        saved.DescriptorJson.ShouldBe(FleetDescriptorJson);
        VehicleFieldNames(saved.Schema).ShouldBe(_expectedFieldNames);
    }

    [Fact]
    public async Task Second_run_with_unchanged_descriptor_is_a_true_no_op()
    {
        var previouslyAppliedSchema = MapFleetDescriptor();
        _store.GetCurrentAsync("fleet", Arg.Any<CancellationToken>())
            .Returns(new AppliedSchema(previouslyAppliedSchema, FleetDescriptorJson, 1, DateTimeOffset.UtcNow));

        var result = await _runner.RunAsync(new MigrationOptions(), TestContext.Current.CancellationToken);

        result.Applied.ShouldBeFalse();
        result.Plan.IsEmpty.ShouldBeTrue();
        SavedSchemas().ShouldBeEmpty();
    }

    [Fact]
    public async Task Destructive_change_without_AllowDestructive_is_refused_and_not_saved()
    {
        var currentWithExtraField = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields =
                [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "vin", Type = FieldType.String, Required = true, MaxLength = 17 },
                    new FieldSchema { Name = "make", Type = FieldType.String, Nullable = true },
                    new FieldSchema { Name = "license_plate", Type = FieldType.String, Nullable = true },
                ],
            },
        ]);
        _store.GetCurrentAsync("fleet", Arg.Any<CancellationToken>())
            .Returns(new AppliedSchema(currentWithExtraField, FleetDescriptorJson, 1, DateTimeOffset.UtcNow));

        var result = await _runner.RunAsync(new MigrationOptions(), TestContext.Current.CancellationToken);

        result.Applied.ShouldBeFalse();
        result.WasDryRun.ShouldBeFalse();
        result.Plan.HasDestructiveChanges.ShouldBeTrue();
        await _store.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<AppliedSchema>(), Arg.Any<CancellationToken>());

        DestructiveChangeGuard.Describe(result.Plan).ShouldContain("vehicles.license_plate");
    }

    private static SchemaModel MapFleetDescriptor()
        => DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(FleetDescriptorJson));

    private static List<string> VehicleFieldNames(SchemaModel schema)
        => schema.Entities.Single(e => e.Name == "vehicles").Fields.Select(f => f.Name).ToList();

    private List<AppliedSchema> SavedSchemas()
        => _store.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IAppliedSchemaStore.SaveAsync))
            .Select(call => (AppliedSchema)call.GetArguments()[1]!)
            .ToList();
}
