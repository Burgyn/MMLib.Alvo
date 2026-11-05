using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// A field's validation is a CHECK constraint the engine itself enforces (HX-11), asked of both shipped drivers. Not in
/// the adversarial suite: the in-memory reference implementation has no engine to refuse a write (Ruling 28).
/// </summary>
/// <remarks>
/// The facts here need only the DDL: that the constraint exists in the engine, that an empty validated field passes it,
/// that it holds over a stored generated column, and that its name is stable across applies. How a write the engine
/// refuses is answered through the port, and how an apply over breaking rows is refused, are facts of their own.
/// </remarks>
public abstract class AlvoDataCheckConstraintTests
{
    /// <summary>
    /// Stands up an engine over the products entity of <see cref="Descriptor"/> — the seam
    /// <see cref="AlvoDataComputedRollupTests"/> stands its engine up with.
    /// </summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor);

    /// <summary>Runs one statement outside the port and answers the engine's failure, or null when it accepted it.</summary>
    /// <param name="sql">The statement. Identifiers are double-quoted, which both engines accept.</param>
    protected abstract Task<Exception?> ExecuteOutsideThePortAsync(string sql);

    /// <summary>Plans a migration through this engine's <see cref="ISchemaMigrator"/>.</summary>
    /// <param name="current">The schema the store currently holds.</param>
    /// <param name="desired">The schema to migrate it to.</param>
    protected abstract Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired);

    /// <summary>Plans and applies a migration through this engine's <see cref="ISchemaMigrator"/>.</summary>
    /// <param name="current">The schema the store currently holds.</param>
    /// <param name="desired">The schema to migrate it to.</param>
    /// <param name="options">The options the plan is applied with — a rollback allows a destructive step.</param>
    protected abstract Task ApplyAsync(SchemaModel current, SchemaModel desired, MigrationOptions options);

    /// <summary>Applies an already planned migration.</summary>
    /// <param name="plan">The plan <see cref="PlanAsync"/> produced.</param>
    protected abstract Task ExecuteAsync(MigrationPlan plan);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task ApplyAsync(SchemaModel current, SchemaModel desired) => ApplyAsync(current, desired, new MigrationOptions());

    /// <summary>The one caller every fact writes as: none of them is about authorization.</summary>
    protected static AlvoContext Caller { get; } = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    /// <summary>The constraint is the engine's: a write that never passes through Alvo is refused too.</summary>
    [Fact]
    public async Task The_engine_itself_refuses_an_out_of_band_write_that_breaks_it()
    {
        await CreateAsync(Schema, Descriptor);
        (await ExecuteOutsideThePortAsync(
            "INSERT INTO \"products\" (\"id\", \"stock\", \"discount\") VALUES ('00000000-0000-0000-0000-000000000001', -5, NULL)"))
            .ShouldNotBeNull("a CHECK is the engine's, so a write that bypasses Alvo is refused too");
    }

    /// <summary>The counterweight: the same out-of-band write with a compliant value is accepted, so the probe itself works.</summary>
    [Fact]
    public async Task The_engine_accepts_the_same_out_of_band_write_when_it_keeps_the_validation()
    {
        await CreateAsync(Schema, Descriptor);
        (await ExecuteOutsideThePortAsync(
            "INSERT INTO \"products\" (\"id\", \"stock\", \"discount\") VALUES ('00000000-0000-0000-0000-000000000003', 5, NULL)"))
            .ShouldBeNull("the probe itself has to work, or the refusal above proves nothing");
    }

    /// <summary>An optional validated field left empty passes its constraint (Ruling 15).</summary>
    [Fact]
    public async Task An_empty_validated_field_passes_its_check()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var row = await data.CreateAsync("products", Row(stock: 3), Caller, cancellationToken: Ct);
        row["discount"].ShouldBeNull();
    }

    /// <summary>A validation on a computed field is a CHECK over the stored generated column (Ruling 47).</summary>
    [Fact]
    public async Task A_validation_on_a_computed_field_holds_in_the_engine()
    {
        await CreateAsync(Schema, Descriptor);
        (await ExecuteOutsideThePortAsync(
            "INSERT INTO \"products\" (\"id\", \"stock\", \"discount\") VALUES ('00000000-0000-0000-0000-000000000002', 300, NULL)"))
            .ShouldNotBeNull("stock_value = stock + stock is computed, and its validation 'value <= 500' is a CHECK over the generated column");
    }

    /// <summary>A constraint's name and text are stable, so re-planning an unchanged schema plans nothing (Ruling 19).</summary>
    [Fact]
    public async Task Re_applying_an_unchanged_descriptor_plans_no_step()
    {
        await CreateAsync(Schema, Descriptor);
        (await PlanAsync(Schema, Schema)).Steps.ShouldBeEmpty("a stable name and stable canonical text never re-create a constraint (Ruling 19)");
    }

    /// <summary>
    /// Adding and dropping a validation are each one non-destructive step naming the validated field — found by
    /// recomputing the constraint's name, never by parsing it (Ruling 19).
    /// </summary>
    [Fact]
    public async Task Adding_and_dropping_a_validation_are_steps_naming_the_field()
    {
        await CreateAsync(Schema, Descriptor);

        var added = (await PlanAsync(WithoutDiscountValidation, Schema)).Steps.ShouldHaveSingleItem();
        var dropped = (await PlanAsync(Schema, WithoutDiscountValidation)).Steps.ShouldHaveSingleItem();

        (added.Change.Kind, added.Change.Field, added.Change.Detail, added.IsDestructive)
            .ShouldBe((SchemaChangeKind.AlterField, "discount", "Adds the field's validation as a CHECK constraint.", false));
        (dropped.Change.Kind, dropped.Change.Field, dropped.Change.Detail, dropped.IsDestructive)
            .ShouldBe((SchemaChangeKind.AlterField, "discount", "Drops a field's validation.", false));
    }

    /// <summary>
    /// Changing a validation's expression keeps the name, so it plans a drop and then an add of the same constraint — two
    /// non-destructive steps naming the field — and the engine then holds the new bound.
    /// </summary>
    [Fact]
    public async Task Changing_a_validation_drops_and_re_adds_its_constraint()
    {
        await CreateAsync(Schema, Descriptor);
        var tightened = WithDiscountValidation("discount <= 40");

        var steps = (await PlanAsync(Schema, tightened)).Steps;

        steps.Select(step => (step.Change.Kind, step.Change.Field, step.Change.Detail, step.IsDestructive)).ShouldBe(
        [
            (SchemaChangeKind.AlterField, "discount", "Drops a field's validation.", false),
            (SchemaChangeKind.AlterField, "discount", "Adds the field's validation as a CHECK constraint.", false),
        ]);
        await ApplyAsync(Schema, tightened);
        (await InsertDiscountAsync("discount", "00000000-0000-0000-0000-000000000010", 45)).ShouldNotBeNull("the new bound is the engine's");
        (await InsertDiscountAsync("discount", "00000000-0000-0000-0000-000000000011", 40)).ShouldBeNull();
    }

    private static SchemaModel WithoutDiscountValidation => WithDiscountValidation(null);

    private static SchemaModel WithDiscountValidation(string? check) => new([
        Schema.Entities[0] with
        {
            Fields = [.. Schema.Entities[0].Fields.Select(declared => declared.Name == "discount" ? declared with { CheckExpression = check } : declared)],
        },
    ]);

    private Task<Exception?> InsertDiscountAsync(string column, string id, long value) =>
        ExecuteOutsideThePortAsync($"INSERT INTO \"products\" (\"id\", \"stock\", \"{column}\") VALUES ('{id}', 1, {value})");

    /// <summary>
    /// Renaming a validated field re-creates its constraint under the name the new field gives it (Ruling 19): the engine
    /// still refuses a write that breaks it, and planning the renamed schema again plans nothing.
    /// </summary>
    [Fact]
    public async Task Renaming_a_validated_field_keeps_it_enforced_under_its_new_name()
    {
        await CreateAsync(Schema, Descriptor);

        await ApplyAsync(Schema, Renamed(declared: true));

        (await ExecuteOutsideThePortAsync(
            "INSERT INTO \"products\" (\"id\", \"stock\", \"rebate\") VALUES ('00000000-0000-0000-0000-000000000004', 1, 51)"))
            .ShouldNotBeNull("the renamed field's validation is still the engine's");
        (await ExecuteOutsideThePortAsync(
            "INSERT INTO \"products\" (\"id\", \"stock\", \"rebate\") VALUES ('00000000-0000-0000-0000-000000000005', 1, 50)"))
            .ShouldBeNull("and a row that keeps it is written under the new column name");
        (await ExecuteOutsideThePortAsync(
            "INSERT INTO \"products\" (\"id\", \"stock\", \"rebate\") VALUES ('00000000-0000-0000-0000-000000000006', -1, NULL)"))
            .ShouldNotBeNull("a rename releases every validation of the entity, so the untouched stock >= 0 must be added back");
        (await ExecuteOutsideThePortAsync(
            "INSERT INTO \"products\" (\"id\", \"stock\", \"rebate\") VALUES ('00000000-0000-0000-0000-000000000007', 300, NULL)"))
            .ShouldNotBeNull("and so must the computed stock_value <= 500");
        (await PlanAsync(Renamed(declared: false), Renamed(declared: true))).Steps
            .ShouldBeEmpty("re-applying a descriptor that still says renamedFrom re-creates nothing");
    }

    /// <summary>
    /// A rename, its rollback (a destructive drop of the new column and an add of the old one) and the rename again each
    /// leave the validation enforced under the name the field has at that moment — the old-name and new-name hand-off,
    /// both ways.
    /// </summary>
    [Fact]
    public async Task Renaming_rolling_back_and_renaming_again_keeps_the_validation_enforced()
    {
        await CreateAsync(Schema, Descriptor);

        await ApplyAsync(Schema, Renamed(declared: true));
        (await InsertDiscountAsync("rebate", "00000000-0000-0000-0000-000000000020", 51)).ShouldNotBeNull("enforced after the rename");

        await ApplyAsync(Renamed(declared: false), Schema, new MigrationOptions { AllowDestructive = true });
        (await InsertDiscountAsync("discount", "00000000-0000-0000-0000-000000000021", 51)).ShouldNotBeNull("enforced after the rollback");
        (await InsertDiscountAsync("discount", "00000000-0000-0000-0000-000000000022", 50)).ShouldBeNull();

        await ApplyAsync(Schema, Renamed(declared: true));
        (await InsertDiscountAsync("rebate", "00000000-0000-0000-0000-000000000023", 51)).ShouldNotBeNull("enforced after renaming again");
        (await InsertDiscountAsync("rebate", "00000000-0000-0000-0000-000000000024", 50)).ShouldBeNull();
    }

    private static SchemaModel Renamed(bool declared) => new([
        Schema.Entities[0] with
        {
            Fields =
            [
                .. Schema.Entities[0].Fields.Select(field => field.Name == "discount"
                    ? field with { Name = "rebate", RenamedFrom = declared ? "discount" : null, CheckExpression = "rebate <= 50" }
                    : field),
            ],
        },
    ]);

    private static Dictionary<string, object?> Row(long stock) => new(StringComparer.Ordinal) { ["stock"] = stock };

    /// <summary>
    /// products: stock (validation "value >= 0"), discount (optional, "value &lt;= 50"), margin (hidden, "value >= 0"),
    /// sale_starts_at / sale_ends_at (sale_ends_at: "value - sale_starts_at &lt;= duration('720h')"), and the computed
    /// stock_value (stock + stock — a computed field carries no numeric constant, "value &lt;= 500").
    /// </summary>
    protected static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "check-constraints",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            ["products"] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["stock"] = new() { Type = DescField.Integer, Required = true, Validation = "value >= 0" },
                    ["discount"] = new() { Type = DescField.Integer, Validation = "value <= 50" },
                    ["margin"] = new() { Type = DescField.Decimal, Precision = 10, Scale = 2, Hidden = BoolOrCel.FromBoolean(true), Validation = "value >= 0" },
                    ["sale_starts_at"] = new() { Type = DescField.DateTime },
                    ["sale_ends_at"] = new() { Type = DescField.DateTime, Validation = "value - sale_starts_at <= duration('720h')" },
                    ["stock_value"] = new() { Type = DescField.Integer, Computed = "stock + stock", Validation = "value <= 500" },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
            },
        },
    };

    /// <summary>The same entity as a schema; each CheckExpression is the canonical form the mapper would write.</summary>
    protected static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = "products",
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "stock", Type = SchemaField.Integer, Required = true, CheckExpression = "stock >= 0" },
                new FieldSchema { Name = "discount", Type = SchemaField.Integer, Nullable = true, CheckExpression = "discount <= 50" },
                new FieldSchema { Name = "margin", Type = SchemaField.Decimal, Nullable = true, Precision = 10, Scale = 2, CheckExpression = "margin >= 0" },
                new FieldSchema { Name = "sale_starts_at", Type = SchemaField.DateTime, Nullable = true },
                new FieldSchema
                {
                    Name = "sale_ends_at", Type = SchemaField.DateTime, Nullable = true,
                    CheckExpression = "sale_ends_at - sale_starts_at <= duration('2592000s')",
                },
                new FieldSchema
                {
                    Name = "stock_value", Type = SchemaField.Integer, Nullable = true,
                    ComputedExpression = "stock + stock", CheckExpression = "stock_value <= 500",
                },
            ],
        },
    ]);
}
