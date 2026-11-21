using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// A declared rename leaves every index and key under the name the model gives it (#523), one set of facts for both
/// engines, each judged by the engine's own catalog: the rename itself is not destructive, a later plan that drops an index
/// addresses one the database holds, and the old names are free for a new entity or field to take.
/// </summary>
/// <remarks>
/// <c>parts</c> is scoped, with a unique <c>code</c> (an index over <c>(tenant_id, code)</c>) and an indexed <c>bin</c>.
/// It carries no validation, except in the one fact that asks for it: a validation's CHECK constraint is re-created by every
/// rename, which on SQLite rebuilds the table — and a rebuild re-creates every index under its model name, so it would hide
/// the defect these facts pin from every other fact. The renames are <c>entity</c> (<c>parts</c> to <c>components</c>), <c>field</c>
/// (<c>code</c> to <c>sku</c> and <c>bin</c> to <c>shelf</c>) and <c>both</c>.
/// </remarks>
public abstract class RenamedConstraintNameFacts
{
    /// <summary>The container the engine's leg builds, with the driver registered.</summary>
    protected abstract IServiceProvider Services { get; }

    /// <summary>The ambient test cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Whether the leg can run at all (PostgreSQL needs a Linux Docker daemon).</summary>
    protected virtual bool Runnable => true;

    /// <summary>
    /// Whether the engine's catalog lists a table's primary key among its indexes, under the key's name — PostgreSQL's
    /// does; SQLite keeps the name only in the table's stored definition.
    /// </summary>
    protected virtual bool ListsThePrimaryKeyAsAnIndex => false;

    private ISchemaMigrator Migrator => Services.GetRequiredService<ISchemaMigrator>();

    /// <summary>The names of the indexes the engine's catalog holds on <paramref name="table"/>, its own internal ones excluded.</summary>
    /// <param name="table">The table.</param>
    protected abstract Task<IReadOnlyList<string>> IndexNamesAsync(string table);

    [Theory]
    [InlineData("entity")]
    [InlineData("field")]
    [InlineData("both")]
    public async Task A_rename_is_not_destructive_and_leaves_only_the_names_the_model_gives(string renames)
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");

        await MigrateAsync(new SchemaModel([]), Original());
        await MigrateAsync(Original(), Renamed(renames));

        await ShouldHoldExactlyAsync(Table(renames), UniqueIndex(Table(renames), Code(renames)), PlainIndex(Table(renames), Bin(renames)));
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("field")]
    [InlineData("both")]
    public async Task A_rename_that_also_rebuilds_the_table_leaves_only_the_names_the_model_gives(string renames)
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");

        await MigrateAsync(new SchemaModel([]), Original(validated: true));
        await MigrateAsync(Original(validated: true), Renamed(renames, validated: true));

        await ShouldHoldExactlyAsync(Table(renames), UniqueIndex(Table(renames), Code(renames)), PlainIndex(Table(renames), Bin(renames)));
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

        await ShouldHoldExactlyAsync(Table(renames));
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

        await ShouldHoldExactlyAsync(Table(renames));
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("field")]
    [InlineData("both")]
    public async Task A_rename_that_makes_a_unique_field_non_unique_in_the_same_plan_holds_only_the_new_index(string renames)
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");
        var flipped = new SchemaModel([.. Renamed(renames).Entities.Select(NonUniqueCode)]);

        await MigrateAsync(new SchemaModel([]), Original());
        await MigrateAsync(Original(), flipped);

        await ShouldHoldExactlyAsync(Table(renames), PlainIndex(Table(renames), Code(renames)), PlainIndex(Table(renames), Bin(renames)));
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("field")]
    [InlineData("both")]
    public async Task A_renamed_composite_unique_index_takes_its_new_name_and_drops_by_it(string renames)
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");
        var table = Table(renames);
        var composite = $"IX_{table}_tenant_id_{Code(renames)}_{Bin(renames)}";

        await MigrateAsync(new SchemaModel([]), WithComposite(Original(), "code", "bin"));
        await MigrateAsync(WithComposite(Original(), "code", "bin"), WithComposite(Renamed(renames), Code(renames), Bin(renames)));
        await ShouldHoldExactlyAsync(table, UniqueIndex(table, Code(renames)), PlainIndex(table, Bin(renames)), composite);

        await MigrateAsync(WithComposite(Renamed(renames), Code(renames), Bin(renames)), Renamed(renames));
        await ShouldHoldExactlyAsync(table, UniqueIndex(table, Code(renames)), PlainIndex(table, Bin(renames)));
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("field")]
    [InlineData("both")]
    public async Task After_a_rename_the_old_names_are_free_for_a_new_entity_or_field(string renames)
    {
        Assert.SkipUnless(Runnable, "This engine's leg needs a Linux Docker daemon.");
        var table = Table(renames);

        await MigrateAsync(new SchemaModel([]), Original());
        await MigrateAsync(Original(), Renamed(renames));
        await MigrateAsync(Renamed(renames), RenamedBesideTheOldNames(renames));

        var renamedIndexes = new[] { UniqueIndex(table, Code(renames)), PlainIndex(table, Bin(renames)) };
        var oldIndexes = new[] { UniqueIndex("parts", "code"), PlainIndex("parts", "bin") };
        if (renames == "field")
        {
            await ShouldHoldExactlyAsync(table, [.. renamedIndexes, .. oldIndexes]);
            return;
        }

        await ShouldHoldExactlyAsync(table, renamedIndexes);
        await ShouldHoldExactlyAsync("parts", oldIndexes);
    }

    private async Task ShouldHoldExactlyAsync(string table, params string[] indexes)
    {
        string[] expected = ListsThePrimaryKeyAsAnIndex ? [.. indexes, $"PK_{table}"] : indexes;
        (await IndexNamesAsync(table)).Order(StringComparer.Ordinal)
            .ShouldBe(expected.Order(StringComparer.Ordinal), $"the catalog holds exactly the names the model gives {table}");
    }

    private async Task MigrateAsync(SchemaModel from, SchemaModel to)
    {
        var options = new MigrationOptions { AllowDestructive = false };
        var plan = await Migrator.PlanAsync(from, to, options, Ct);
        await Migrator.ApplyAsync(plan, options, Ct);
    }

    private static string Table(string renames) => renames == "field" ? "parts" : "components";

    private static string Code(string renames) => renames == "entity" ? "code" : "sku";

    private static string Bin(string renames) => renames == "entity" ? "bin" : "shelf";

    private static string UniqueIndex(string table, string field) => $"IX_{table}_tenant_id_{field}";

    private static string PlainIndex(string table, string field) => $"IX_{table}_{field}";

    private static SchemaModel Original(bool validated = false) => new([Parts("parts", CodeField("code"), BinField("bin"), validated)]);

    private static SchemaModel Renamed(string renames, bool indexed = true, bool validated = false) =>
        new([RenamedParts(renames, indexed, validated)]);

    private static SchemaModel WithComposite(SchemaModel model, string code, string bin) =>
        new([.. model.Entities.Select(entity => entity with { Indexes = [new IndexSchema([code, bin], Unique: true)] })]);

    private static EntitySchema NonUniqueCode(EntitySchema entity) => entity with
    {
        Fields = [.. entity.Fields.Select(field => field.Unique ? field with { Unique = false, Indexed = true } : field)],
    };

    private static SchemaModel RenamedBesideTheOldNames(string renames)
    {
        var parts = RenamedParts(renames, indexed: true) with { RenamedFrom = null };
        parts = parts with { Fields = [.. parts.Fields.Select(field => field with { RenamedFrom = null })] };
        return renames == "field"
            ? new([parts with { Fields = [.. parts.Fields, CodeField("code"), BinField("bin")] }])
            : new([parts, Parts("parts", CodeField("code"), BinField("bin"))]);
    }

    private static EntitySchema RenamedParts(string renames, bool indexed, bool validated = false)
    {
        var fieldsRenamed = renames != "entity";
        var code = fieldsRenamed ? CodeField("sku") with { RenamedFrom = "code" } : CodeField("code");
        var bin = fieldsRenamed ? BinField("shelf") with { RenamedFrom = "bin" } : BinField("bin");
        var parts = Parts(Table(renames), code with { Unique = indexed }, bin with { Indexed = indexed }, validated);
        return renames == "field" ? parts : parts with { RenamedFrom = "parts" };
    }

    private static EntitySchema Parts(string name, FieldSchema code, FieldSchema bin, bool validated = false) => new()
    {
        Name = name,
        Tenancy = TenancyMode.Scoped,
        Fields = validated ? [Id(), code, bin, Stock(), TenantId()] : [Id(), code, bin, TenantId()],
    };

    private static FieldSchema CodeField(string name) => new() { Name = name, Type = FieldType.String, Nullable = true, Unique = true };

    private static FieldSchema BinField(string name) => new() { Name = name, Type = FieldType.String, Nullable = true, Indexed = true };

    private static FieldSchema Stock() => new() { Name = "stock", Type = FieldType.Integer, Required = true, CheckExpression = "stock >= 0" };

    private static FieldSchema Id() => new() { Name = "id", Type = FieldType.Uuid, Required = true };

    private static FieldSchema TenantId() => new() { Name = "tenant_id", Type = FieldType.Uuid, Required = true };
}
