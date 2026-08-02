using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Migrations.Internal;
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
        _runner = new SchemaMigrationRunner(BootPlan(), _migrator, _introspector, _store, new PolicyCatalogProvider());
    }

    /// <summary>
    /// Stage 0 over this fixture's descriptor source — the runner's first call, and the only place the
    /// descriptor is loaded, validated, mapped and compiled.
    /// </summary>
    /// <param name="logger">The logger stage 0 writes its unhonoured-block warning through.</param>
    private DescriptorBootPlan BootPlan(ILogger<DescriptorBootPlan>? logger = null)
        => new(
            _source,
            new DescriptorValidator(),
            new CelCompiler(),
            logger ?? NullLogger<DescriptorBootPlan>.Instance);

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
        var runner = new SchemaMigrationRunner(BootPlan(), migrator, _introspector, _store, new PolicyCatalogProvider());
        _store.GetCurrentAsync("fleet", Arg.Any<CancellationToken>()).Returns((AppliedSchema?)null);
        _introspector.IntrospectAsync(Arg.Any<CancellationToken>()).Returns(new SchemaModel([]));

        await runner.RunAsync(new MigrationOptions { DryRun = true }, TestContext.Current.CancellationToken);

        await migrator.DidNotReceive().ApplyAsync(Arg.Any<MigrationPlan>(), Arg.Any<MigrationOptions>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The same fleet, plus one declared-but-unhonoured top-level block — the minimum a descriptor needs to
    /// earn the §D warning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Purpose-built rather than <c>examples/complex-crm</c>, which is the fixture the pure-function facts
    /// use.</b> <c>complex-crm</c> is deliberately not appliable: it declares four refused <em>features</em>
    /// as well, so <see cref="SchemaMigrationRunner.RunAsync"/> over it throws before reaching the warning —
    /// the fact would fail for the wrong reason and then pass again once someone "fixed" the descriptor.
    /// </para>
    /// <para>
    /// <c>webhooks</c> is the block, and one endpoint is what makes it a declaration: <c>"webhooks": {}</c>
    /// would be an author saying they are not using the feature, which
    /// <see cref="UnhonouredSubsystems.DeclaredBy"/> correctly does not warn about.
    /// </para>
    /// </remarks>
    private const string FleetWithUnhonouredBlockJson = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "fleet",
          "entities": {
            "vehicles": {
              "fields": {
                "vin": { "type": "string", "required": true, "maxLength": 17 }
              }
            }
          },
          "webhooks": {
            "endpoints": {
              "vehicle-changed": {
                "url": "https://example.test/hooks/vehicle-changed",
                "secretRef": "vehicle-changed-secret"
              }
            }
          }
        }
        """;

    /// <summary>
    /// <b>Applying a descriptor that declares an unhonoured block writes a warning naming it.</b> This is the
    /// fact that the §D warning is <em>reached</em>, as opposed to correct.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without it, the whole user-visible deliverable had no coverage.</b> All four facts in
    /// <c>UnhonouredSubsystemsTests</c> call <c>UnhonouredSubsystems.Warn(logger, descriptor)</c> directly, so
    /// they prove a pure function is right and nothing proves anybody calls it — deleting the
    /// <c>UnhonouredSubsystems.Warn(_logger, descriptor)</c> line from the apply path left the entire suite
    /// green. Every other fact in this class builds the runner with
    /// <c>NullLogger&lt;DescriptorBootPlan&gt;.Instance</c>, which cannot observe a warning by construction,
    /// and the descriptors the real apply paths use declare no unhonoured block — so even a log-capturing
    /// world would have been silent.
    /// </para>
    /// <para>
    /// <b>It asserts the block is <em>named</em>, not that a warning was logged.</b> The latter passes on any
    /// wording, which is the vacuity <c>UnhonouredSubsystemsTests</c>' own remarks refuse; and it drives a
    /// genuine apply rather than the empty-plan no-op, because that is the run an author is looking at when
    /// they go hunting for the webhook that never fired.
    /// </para>
    /// <para>
    /// The logger is a real <see cref="LoggerFactory"/> over a capturing provider rather than the
    /// <see cref="ILogger"/> stage 0 takes, so the <c>LoggerMessage</c> source-generated delegate that
    /// actually formats this message is on the path.
    /// </para>
    /// <para>
    /// The warning itself is written by <see cref="DescriptorBootPlan"/> now, and
    /// <c>DescriptorBootPlanTests.A_declared_but_unhonoured_block_warns_on_every_boot_naming_it</c> pins it
    /// there. This fact is kept, and is not a duplicate of that one: it is what proves the <em>apply</em> path
    /// still runs stage 0 at all, which is the only reason an author of an appliable descriptor sees the line.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Applying_a_descriptor_that_declares_an_unhonoured_block_warns_naming_it()
    {
        _source.LoadAsync(Arg.Any<CancellationToken>()).Returns(FleetWithUnhonouredBlockJson);
        _store.GetCurrentAsync("fleet", Arg.Any<CancellationToken>()).Returns((AppliedSchema?)null);
        _introspector.IntrospectAsync(Arg.Any<CancellationToken>()).Returns(new SchemaModel([]));

        using var capturing = new CapturingLogger();
        using var loggers = LoggerFactory.Create(logging => logging.AddProvider(capturing));
        var runner = new SchemaMigrationRunner(
            BootPlan(loggers.CreateLogger<DescriptorBootPlan>()),
            _migrator,
            _introspector,
            _store,
            new PolicyCatalogProvider());

        var result = await runner.RunAsync(new MigrationOptions(), TestContext.Current.CancellationToken);

        result.Applied.ShouldBeTrue(
            "or this fact never reached the apply path whose warning it is asserting on");
        capturing.Warnings.ShouldHaveSingleItem(
                "one line for the whole set, and exactly one apply happened")
            .ShouldContain(
                "webhooks",
                Shouldly.Case.Sensitive,
                "the descriptor declares 'webhooks' and this build honours it nowhere; a warning that does not "
                + "name the block leaves the author debugging the endpoint they think is down");
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
