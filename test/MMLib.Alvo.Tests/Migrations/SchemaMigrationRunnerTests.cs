using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules.Internal;
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
        _runner = new SchemaMigrationRunner(_source, new DescriptorValidator(), _migrator, _introspector, _store, new CelCompiler(), new PolicyCatalogProvider(), NullLogger<SchemaMigrationRunner>.Instance);
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

    [Fact]
    public async Task Invalid_descriptor_throws_before_parsing()
    {
        _source.LoadAsync(Arg.Any<CancellationToken>()).Returns("""{ "apiVersion": "alvo.dev/v1", "entities": {} }""");

        await Should.ThrowAsync<DescriptorValidationException>(
            () => _runner.RunAsync(new MigrationOptions(), TestContext.Current.CancellationToken));

        await _introspector.DidNotReceive().IntrospectAsync(Arg.Any<CancellationToken>());
        await _store.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<AppliedSchema>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DryRun_previews_a_non_empty_non_destructive_plan_without_applying_or_saving()
    {
        _store.GetCurrentAsync("fleet", Arg.Any<CancellationToken>()).Returns((AppliedSchema?)null);
        _introspector.IntrospectAsync(Arg.Any<CancellationToken>()).Returns(new SchemaModel([]));

        var result = await _runner.RunAsync(new MigrationOptions { DryRun = true }, TestContext.Current.CancellationToken);

        result.Applied.ShouldBeFalse();
        result.WasDryRun.ShouldBeTrue();
        result.Plan.IsEmpty.ShouldBeFalse();
        SavedSchemas().ShouldBeEmpty();
    }

    [Fact]
    public async Task DryRun_never_invokes_the_migrators_ApplyAsync()
    {
        var migrator = Substitute.For<ISchemaMigrator>();
        var nonEmptyPlan = new MigrationPlan
        {
            Steps = [new MigrationStep(new SchemaChange { Kind = SchemaChangeKind.AddField, Entity = "vehicles", Field = "color" }, IsDestructive: false, Reason: null)],
        };
        migrator.PlanAsync(Arg.Any<SchemaModel>(), Arg.Any<SchemaModel>(), Arg.Any<MigrationOptions>(), Arg.Any<CancellationToken>())
            .Returns(nonEmptyPlan);
        var runner = new SchemaMigrationRunner(_source, new DescriptorValidator(), migrator, _introspector, _store, new CelCompiler(), new PolicyCatalogProvider(), NullLogger<SchemaMigrationRunner>.Instance);
        _store.GetCurrentAsync("fleet", Arg.Any<CancellationToken>()).Returns((AppliedSchema?)null);
        _introspector.IntrospectAsync(Arg.Any<CancellationToken>()).Returns(new SchemaModel([]));

        await runner.RunAsync(new MigrationOptions { DryRun = true }, TestContext.Current.CancellationToken);

        await migrator.DidNotReceive().ApplyAsync(Arg.Any<MigrationPlan>(), Arg.Any<MigrationOptions>(), Arg.Any<CancellationToken>());
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
