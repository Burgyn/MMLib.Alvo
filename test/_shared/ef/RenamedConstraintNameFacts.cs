using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// A declared rename leaves every index and key under the name the model gives it (#523), one set of facts for both
/// engines: the rename itself is not destructive, a later plan that drops an index addresses one the database holds, and
/// the old names are free for a new entity or field to take.
/// </summary>
/// <remarks>
/// <c>parts</c> is scoped, with a unique <c>code</c> (an index over <c>(tenant_id, code)</c>), an indexed <c>bin</c> and a
/// validated <c>stock</c>, whose CHECK constraint every rename re-creates — on SQLite by rebuilding the table in the same
/// plan, after the index renames. The renames are <c>entity</c> (<c>parts</c> to <c>components</c>), <c>field</c> (<c>code</c> to <c>sku</c> and
/// <c>bin</c> to <c>shelf</c>) and <c>both</c>.
/// </remarks>
public abstract class RenamedConstraintNameFacts
{
    /// <summary>The container the engine's leg builds, with the driver registered.</summary>
    protected abstract IServiceProvider Services { get; }

    /// <summary>The ambient test cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Whether the leg can run at all (PostgreSQL needs a Linux Docker daemon).</summary>
    protected virtual bool Runnable => true;

    private ISchemaMigrator Migrator => Services.GetRequiredService<ISchemaMigrator>();

    [Theory]
    [InlineData("entity")]
    [InlineData("field")]
    [InlineData("both")]
    public async Task A_rename_is_not_destructive_and_replans_empty(string renames)
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");

        await MigrateAsync(new SchemaModel([]), Original());
        await MigrateAsync(Original(), Renamed(renames));

        (await Migrator.PlanAsync(Renamed(renames), Renamed(renames), new MigrationOptions(), Ct)).IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("field")]
    [InlineData("both")]
    public async Task After_a_rename_a_later_plan_drops_each_index_by_its_model_name(string renames)
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");

        await MigrateAsync(new SchemaModel([]), Original());
        await MigrateAsync(Original(), Renamed(renames));
        await MigrateAsync(Renamed(renames), Renamed(renames, indexed: false));

        (await Migrator.PlanAsync(Renamed(renames, indexed: false), Renamed(renames, indexed: false), new MigrationOptions(), Ct))
            .IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("field")]
    [InlineData("both")]
    public async Task A_rename_that_drops_the_indexes_in_the_same_plan_drops_them_by_the_names_the_database_holds(string renames)
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");

        await MigrateAsync(new SchemaModel([]), Original());
        await MigrateAsync(Original(), Renamed(renames, indexed: false));
        await MigrateAsync(Renamed(renames, indexed: false), RenamedBesideTheOldNames(renames));

        (await Migrator.PlanAsync(RenamedBesideTheOldNames(renames), RenamedBesideTheOldNames(renames), new MigrationOptions(), Ct))
            .IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("field")]
    [InlineData("both")]
    public async Task After_a_rename_the_old_names_are_free_for_a_new_entity_or_field(string renames)
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");

        await MigrateAsync(new SchemaModel([]), Original());
        await MigrateAsync(Original(), Renamed(renames));
        await MigrateAsync(Renamed(renames), RenamedBesideTheOldNames(renames));

        (await Migrator.PlanAsync(RenamedBesideTheOldNames(renames), RenamedBesideTheOldNames(renames), new MigrationOptions(), Ct))
            .IsEmpty.ShouldBeTrue();
    }

    private async Task MigrateAsync(SchemaModel from, SchemaModel to)
    {
        var options = new MigrationOptions { AllowDestructive = false };
        var plan = await Migrator.PlanAsync(from, to, options, Ct);
        await Migrator.ApplyAsync(plan, options, Ct);
    }

    private static SchemaModel Original() => new([Parts("parts", Code("code"), Bin("bin"))]);

    private static SchemaModel Renamed(string renames, bool indexed = true) => new([RenamedParts(renames, indexed)]);

    private static SchemaModel RenamedBesideTheOldNames(string renames)
    {
        var parts = RenamedParts(renames, indexed: true) with { RenamedFrom = null };
        parts = parts with { Fields = [.. parts.Fields.Select(field => field with { RenamedFrom = null })] };
        return renames == "field"
            ? new([parts with { Fields = [.. parts.Fields, Code("code"), Bin("bin")] }])
            : new([parts, Parts("parts", Code("code"), Bin("bin"))]);
    }

    private static EntitySchema RenamedParts(string renames, bool indexed)
    {
        var fieldsRenamed = renames != "entity";
        var code = fieldsRenamed ? Code("sku") with { RenamedFrom = "code" } : Code("code");
        var bin = fieldsRenamed ? Bin("shelf") with { RenamedFrom = "bin" } : Bin("bin");
        var parts = Parts(renames == "field" ? "parts" : "components", code with { Unique = indexed }, bin with { Indexed = indexed });
        return renames == "field" ? parts : parts with { RenamedFrom = "parts" };
    }

    private static EntitySchema Parts(string name, FieldSchema code, FieldSchema bin) => new()
    {
        Name = name,
        Tenancy = TenancyMode.Scoped,
        Fields = [Id(), code, bin, Stock(), TenantId()],
    };

    private static FieldSchema Code(string name) => new() { Name = name, Type = FieldType.String, Nullable = true, Unique = true };

    private static FieldSchema Bin(string name) => new() { Name = name, Type = FieldType.String, Nullable = true, Indexed = true };

    private static FieldSchema Stock() => new() { Name = "stock", Type = FieldType.Integer, Required = true, CheckExpression = "stock >= 0" };

    private static FieldSchema Id() => new() { Name = "id", Type = FieldType.Uuid, Required = true };

    private static FieldSchema TenantId() => new() { Name = "tenant_id", Type = FieldType.Uuid, Required = true };
}
