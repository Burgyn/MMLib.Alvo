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

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// HX-11 Ruling 73 on every core path that plans an apply: a validation added over a rollup whose definition the same apply
/// changes is refused before planning, at the validation's pointer — the runtime apply, its preview, a rollback, the
/// code-first runner and the boot.
/// </summary>
public sealed class RollupValidationOrderApplyTests
{
    private const string Pointer = "/entities/products/fields/reserved/validation";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_runtime_apply_is_refused_before_it_plans_and_appends_nothing()
    {
        var world = await RuntimeSchemaWorld.WithAppliedAsync(Inventory(Held), Ct);

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(
            () => world.Service.ApplyAsync(RuntimeSchemaWorld.Project, Inventory(Released, validated: true), 1, new MigrationOptions(), Ct));

        refusal.Result.Errors.ShouldHaveSingleItem().Path.ShouldBe(Pointer);
        (await world.Versions.ListAsync(RuntimeSchemaWorld.Project, Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_preview_is_refused_the_same_way()
    {
        var world = await RuntimeSchemaWorld.WithAppliedAsync(Inventory(Held), Ct);

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(
            () => world.Service.PreviewAsync(RuntimeSchemaWorld.Project, Inventory(Released, validated: true), 1, new MigrationOptions(), Ct));

        refusal.Result.Errors.ShouldHaveSingleItem().Path.ShouldBe(Pointer);
    }

    /// <summary>The counterweight: the rollup change alone, then the validation, is the order that works.</summary>
    [Fact]
    public async Task The_rollup_change_then_the_validation_is_applied()
    {
        var world = await RuntimeSchemaWorld.WithAppliedAsync(Inventory(Held), Ct);

        await world.Service.ApplyAsync(RuntimeSchemaWorld.Project, Inventory(Released), 1, new MigrationOptions(), Ct);
        var applied = await world.Service.ApplyAsync(RuntimeSchemaWorld.Project, Inventory(Released, validated: true), 2, new MigrationOptions(), Ct);

        applied.Revision.ShouldBe(3);
    }

    /// <summary>A rollback restoring a validation over a rollup it also restores is an apply like any other.</summary>
    [Fact]
    public async Task A_rollback_that_restores_both_is_refused()
    {
        var world = await RuntimeSchemaWorld.WithAppliedAsync(Inventory(Released, validated: true), Ct);
        await world.Service.ApplyAsync(RuntimeSchemaWorld.Project, Inventory(Held), 1, new MigrationOptions { AllowDestructive = true }, Ct);

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(
            () => world.Service.RollbackAsync(RuntimeSchemaWorld.Project, 1, new MigrationOptions { AllowDestructive = true }, Ct));

        refusal.Result.Errors.ShouldHaveSingleItem().Path.ShouldBe(Pointer);
        (await world.Versions.ListAsync(RuntimeSchemaWorld.Project, Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_code_first_runner_is_refused()
    {
        var source = Substitute.For<IDescriptorSource>();
        source.LoadAsync(Arg.Any<CancellationToken>()).Returns(Inventory(Released, validated: true, project: "inventory"));
        var store = Substitute.For<IAppliedSchemaStore>();
        var held = Inventory(Held, project: "inventory");
        store.GetCurrentAsync("inventory", Arg.Any<CancellationToken>())
            .Returns(new AppliedSchema(BootServiceWorld.SchemaOf(held), held, 1, DateTimeOffset.UtcNow));
        var runner = new SchemaMigrationRunner(
            new DescriptorBootPlan(source, new DescriptorValidator(), new CelCompiler(), NullLogger<DescriptorBootPlan>.Instance),
            new InMemorySchemaMigrator(), Substitute.For<ISchemaIntrospector>(), store, new PolicyCatalogProvider());

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(() => runner.RunAsync(new MigrationOptions { DryRun = true }, Ct));

        refusal.Result.Errors.ShouldHaveSingleItem().Path.ShouldBe(Pointer);
    }

    [Fact]
    public async Task A_boot_that_would_apply_both_is_refused()
    {
        using var world = new BootServiceWorld()
            .Applied(Inventory(Held, project: BootServiceWorld.Project))
            .BootingFrom(Inventory(Released, validated: true, project: BootServiceWorld.Project));

        (await world.BootAsync()).ShouldBeOfType<DescriptorValidationException>()
            .Result.Errors.ShouldHaveSingleItem().Path.ShouldBe(Pointer);
        world.RecordedRevisions().ShouldBe([1]);
    }

    /// <summary>A boot that applies nothing never plans the apply, so it is not refused (Ruling 66's reasoning).</summary>
    [Fact]
    public async Task A_skipping_boot_keeps_serving()
    {
        using var world = new BootServiceWorld()
            .Applied(Inventory(Held, project: BootServiceWorld.Project))
            .BootingFrom(Inventory(Released, validated: true, project: BootServiceWorld.Project));
        world.Options.Startup = AlvoSchemaStartupMode.Skip;

        await world.BootOrFailAsync();

        world.State.Phase.ShouldBe(AlvoBootPhase.Ready);
    }

    private const string Held = "status == 'held'";
    private const string Released = "status == 'released'";

    private static string Inventory(string where, bool validated = false, string project = RuntimeSchemaWorld.Project)
    {
        // The validated revision also adds a plain field, so a planner that does not diff validations (the in-memory one)
        // still sees a non-empty plan and the boot decides to apply.
        var validation = validated ? """, "validation": "value <= stock" }, "label": { "type": "string" """ : string.Empty;
        return $$"""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "{{project}}",
              "entities": {
                "products": {
                  "fields": {
                    "stock": { "type": "integer" },
                    "reserved": { "type": "integer", "rollup": { "from": "reservations", "op": "sum", "field": "quantity", "where": "{{where}}" }{{validation}} }
                  }
                },
                "reservations": {
                  "fields": {
                    "product_id": { "type": "ref", "entity": "products", "required": true },
                    "quantity": { "type": "integer", "required": true },
                    "status": { "type": "enum", "values": ["held", "released"] }
                  }
                }
              }
            }
            """;
    }
}
