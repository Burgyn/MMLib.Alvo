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
/// Three groups: the DDL (the constraint exists in the engine, an empty validated field passes it, it holds over a stored
/// generated column, its name is stable across applies); a write the engine refuses, answered through the port; and an
/// apply over rows that break a validation — counted before the apply where the table and every column it reads already
/// exist, and otherwise refused by the engine and translated at the same pointer (HX-11 Ruling 22).
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

    /// <summary>
    /// Plans a migration and applies it through this engine's <see cref="IRuntimeSchemaWriter"/> — the executor the
    /// Management API, the dashboard, rollback, restore and the boot take — as the project's first revision.
    /// </summary>
    /// <param name="current">The schema the store currently holds.</param>
    /// <param name="desired">The schema to migrate it to, recorded as the appended version's schema.</param>
    protected abstract Task ApplyThroughTheRuntimeWriterAsync(SchemaModel current, SchemaModel desired);

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

    /// <summary>A write the engine refuses for a validation is the port's refusal, naming the field, and leaves nothing behind.</summary>
    [Fact]
    public async Task A_write_that_breaks_a_validation_is_refused_naming_the_field()
    {
        var data = await CreateAsync(Schema, Descriptor);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => data.CreateAsync("products", Row(stock: -1), Caller, cancellationToken: Ct));

        refusal.Kind.ShouldBe(AlvoConstraintKind.Check);
        refusal.Fields.ShouldBe(["stock"]);
        (await data.QueryAsync(new AlvoQuery { Entity = "products" }, Caller, Ct)).Items.ShouldBeEmpty();
    }

    /// <summary>A validation reading another field the row leaves empty follows CEL: the comparison is false (Ruling 15).</summary>
    [Fact]
    public async Task A_validation_reading_an_empty_other_field_refuses_the_row()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() => data.CreateAsync(
            "products",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["stock"] = 3L, ["sale_ends_at"] = Instant },
            Caller,
            cancellationToken: Ct));
        refusal.Fields.ShouldBe(["sale_ends_at"]);
    }

    /// <summary>A field the descriptor hides is never named by a refusal, whoever the caller is (Ruling 24).</summary>
    [Fact]
    public async Task A_hidden_field_s_refusal_names_nothing()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() => data.CreateAsync(
            "products",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["stock"] = 3L, ["margin"] = -1m },
            Caller,
            cancellationToken: Ct));
        (refusal.Kind, refusal.Fields.Count).ShouldBe((AlvoConstraintKind.Check, 0));
    }

    /// <summary>
    /// The descriptor decides what a refusal may name, not the caller's mask (Ruling 24): rating is hidden only from admins,
    /// so this caller reads it — and its broken validation still names nothing, because a name published to the callers who
    /// may read a field is published to those who may not.
    /// </summary>
    [Fact]
    public async Task A_field_hidden_from_other_roles_is_never_named_even_to_a_caller_who_reads_it()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var written = await data.CreateAsync(
            "products", new Dictionary<string, object?>(StringComparer.Ordinal) { ["stock"] = 1L, ["rating"] = 5L }, Caller, cancellationToken: Ct);
        written["rating"].ShouldBe(5L, "the caller's own mask does not hide rating");

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() => data.CreateAsync(
            "products", new Dictionary<string, object?>(StringComparer.Ordinal) { ["stock"] = 1L, ["rating"] = 6L }, Caller, cancellationToken: Ct));

        (refusal.Kind, refusal.Fields.Count).ShouldBe((AlvoConstraintKind.Check, 0));
    }

    /// <summary>
    /// A value a before-hook wrote is held to the validation like any other (Ruling 26): the engine cannot tell whose value
    /// it was, so the write is the caller's refusal at the field, and nothing is stored.
    /// </summary>
    [Fact]
    public async Task A_before_hook_value_that_breaks_a_validation_is_refused_at_the_field()
    {
        var data = await CreateAsync(Schema, DiscountingDescriptor);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => data.CreateAsync("products", Row(stock: 1), Caller, cancellationToken: Ct));

        refusal.Kind.ShouldBe(AlvoConstraintKind.Check);
        refusal.Fields.ShouldBe(["discount"]);
        (await data.QueryAsync(new AlvoQuery { Entity = "products" }, Caller, Ct)).Items.ShouldBeEmpty();
    }

    /// <summary><see cref="Descriptor"/> whose beforeCreate hook sets discount to stock + 50 — past its "value &lt;= 50".</summary>
    private static AlvoDescriptor DiscountingDescriptor => Descriptor with
    {
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            ["products"] = Descriptor.Entities["products"] with
            {
                Hooks = new EntityHooks
                {
                    BeforeCreate =
                    [
                        new BeforeHook
                        {
                            Action = new BeforeHookAction
                            {
                                Mutate = new Dictionary<string, ValueOrExpr>(StringComparer.Ordinal)
                                {
                                    ["discount"] = ValueOrExpr.FromExpression("new.stock + 50"),
                                },
                            },
                        },
                    ],
                },
            },
        },
    };

    /// <summary>An update the engine refuses rolls back: the stored row keeps its value.</summary>
    [Fact]
    public async Task An_update_that_breaks_it_leaves_the_row_as_it_was()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var id = (Guid)(await data.CreateAsync("products", Row(stock: 3), Caller, cancellationToken: Ct))["id"]!;

        await Should.ThrowAsync<AlvoConstraintViolationException>(() => data.UpdateAsync(
            "products", id, new Dictionary<string, object?>(StringComparer.Ordinal) { ["stock"] = -1L }, Caller, cancellationToken: Ct));

        (await data.GetAsync("products", id, Caller, Ct))!["stock"].ShouldBe(3L);
    }

    /// <summary>A duration validation holds in the engine at its bound: 31 days refused, 30 days written.</summary>
    [Fact]
    public async Task A_duration_validation_holds_in_the_engine()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() => data.CreateAsync(
            "products",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["stock"] = 1L,
                ["sale_starts_at"] = Instant,
                ["sale_ends_at"] = Instant.AddDays(31),
            },
            Caller,
            cancellationToken: Ct));
        refusal.Fields.ShouldBe(["sale_ends_at"]);
        await data.CreateAsync("products", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["stock"] = 1L,
            ["sale_starts_at"] = Instant,
            ["sale_ends_at"] = Instant.AddDays(30),
        }, Caller, cancellationToken: Ct);
    }

    /// <summary>A negative constant is a constant: -40 is written and -41 refused at the field (Ruling 16).</summary>
    [Fact]
    public async Task A_negative_bound_holds_and_reads_back_exactly()
    {
        var data = await CreateAsync(Schema, Descriptor);
        await data.CreateAsync("products", new Dictionary<string, object?>(StringComparer.Ordinal) { ["stock"] = 1L, ["temperature"] = -40L }, Caller, cancellationToken: Ct);
        (await Should.ThrowAsync<AlvoConstraintViolationException>(() => data.CreateAsync(
            "products", new Dictionary<string, object?>(StringComparer.Ordinal) { ["stock"] = 1L, ["temperature"] = -41L }, Caller, cancellationToken: Ct)))
            .Fields.ShouldBe(["temperature"]);
    }

    /// <summary>
    /// A child write whose rollup recompute pushes the parent past its validation is refused whole, pointed at the child
    /// field the rollup aggregates — the value the caller sent — never the parent's (Rulings 25, 45): the recompute is
    /// translated like the write itself, so the engine's refusal is never a 500.
    /// </summary>
    [Fact]
    public async Task A_rollup_pushing_a_parent_past_its_validation_is_refused_at_the_child_field()
    {
        var data = await CreateAsync(ReservedSchema, ReservedDescriptor);
        var product = (Guid)(await data.CreateAsync("products", Row(1), Caller, cancellationToken: Ct))["id"]!;
        await data.CreateAsync("reservations", Reservation(product, 1), Caller, cancellationToken: Ct);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => data.CreateAsync("reservations", Reservation(product, 1), Caller, cancellationToken: Ct));

        refusal.Kind.ShouldBe(AlvoConstraintKind.Check);
        refusal.Fields.ShouldBe(["quantity"]);
        (await data.QueryAsync(new AlvoQuery { Entity = "reservations" }, Caller, Ct)).Items.Count.ShouldBe(1);
    }

    /// <summary>
    /// Adding a validation to a table whose rows break it is refused when it is planned, at the validation, with the
    /// number of breaking rows (acceptance 2) — and nothing is applied.
    /// </summary>
    [Fact]
    public async Task Adding_a_validation_over_rows_that_break_it_is_refused_with_their_count()
    {
        var data = await CreateAsync(CheckSchemas.Unvalidated, Descriptor);
        foreach (var stock in new long[] { -1, -2, 5 })
        {
            await data.CreateAsync("products", Row(stock), Caller, cancellationToken: Ct);
        }

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(() => PlanAsync(CheckSchemas.Unvalidated, Schema));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/stock/validation");
        error.Message.ShouldStartWith("2 row(s) of 'products' break this validation");
        (await data.QueryAsync(new AlvoQuery { Entity = "products" }, Caller, Ct)).Items.Count.ShouldBe(3, "nothing was applied");
    }

    /// <summary>
    /// A count the database cannot run — a drifted table missing a column the applied schema still names — is refused at
    /// the validation it was counting for, never a provider exception, and fails closed: nothing is applied (Ruling 67).
    /// </summary>
    [Fact]
    public async Task A_count_the_database_cannot_run_is_refused_at_the_validation_and_applies_nothing()
    {
        var data = await CreateAsync(CheckSchemas.Unvalidated, Descriptor);
        await data.CreateAsync("products", Row(5), Caller, cancellationToken: Ct);
        (await ExecuteOutsideThePortAsync("ALTER TABLE \"products\" DROP COLUMN \"temperature\"")).ShouldBeNull();

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(() => PlanAsync(CheckSchemas.Unvalidated, Schema));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/temperature/validation");
        error.Message.ShouldStartWith("Could not count the rows of 'products' that break this validation");
        (await ExecuteOutsideThePortAsync("UPDATE \"products\" SET \"stock\" = -1")).ShouldBeNull("nothing was applied");
    }

    /// <summary>The counterweight: over compliant rows the same apply succeeds, and the engine holds the validation from then on.</summary>
    [Fact]
    public async Task Adding_a_validation_over_compliant_rows_applies_and_holds_from_then_on()
    {
        var data = await CreateAsync(CheckSchemas.Unvalidated, Descriptor);
        await data.CreateAsync("products", Row(5), Caller, cancellationToken: Ct);

        await ApplyAsync(CheckSchemas.Unvalidated, Schema);

        (await ExecuteOutsideThePortAsync("UPDATE \"products\" SET \"stock\" = -1")).ShouldNotBeNull();
    }

    /// <summary>
    /// A row written between the count and the apply is still refused — by the engine, translated by the apply into the
    /// same refusal at the validation, never a 500.
    /// </summary>
    [Fact]
    public async Task Rows_written_after_the_count_are_still_refused_at_the_validation()
    {
        var data = await CreateAsync(CheckSchemas.Unvalidated, Descriptor);
        var plan = await PlanAsync(CheckSchemas.Unvalidated, Schema);
        await data.CreateAsync("products", Row(-1), Caller, cancellationToken: Ct);

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(() => ExecuteAsync(plan));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/stock/validation");
        error.Message.ShouldStartWith("Rows of 'products' break this validation");
    }

    /// <summary>
    /// A validation on a field renamed in the same apply cannot be counted — the column does not exist under its new name
    /// yet — so the engine guards it: compliant rows apply, and violating rows are refused at the renamed field's
    /// validation (Review Focus 5).
    /// </summary>
    [Fact]
    public async Task Renaming_a_validated_field_over_compliant_rows_applies_and_over_violating_rows_is_refused_at_its_validation()
    {
        var data = await CreateAsync(Plain, PlainDescriptor);
        await data.CreateAsync("products", Row(5), Caller, cancellationToken: Ct);
        await ApplyAsync(Plain, PlainRenamed("on_hand >= 0", heldCheck: null));

        var violating = await CreateAsync(Plain, PlainDescriptor);
        await violating.CreateAsync("products", Row(-1), Caller, cancellationToken: Ct);
        var refusal = await Should.ThrowAsync<DescriptorValidationException>(() => ApplyAsync(Plain, PlainRenamed("on_hand >= 0", heldCheck: null)));
        refusal.Result.Errors.ShouldHaveSingleItem().Path.ShouldBe("/entities/products/fields/on_hand/validation");
    }

    /// <summary>
    /// The runtime writer answers an engine refusal exactly as the migrator does — at the validation, its pointer resolved
    /// from the schema it was appending, never from the plan (reviews I1, I3).
    /// </summary>
    [Fact]
    public async Task The_runtime_writer_refuses_a_violating_apply_at_the_validation_too()
    {
        var data = await CreateAsync(Plain, PlainDescriptor);
        await data.CreateAsync("products", Row(-1), Caller, cancellationToken: Ct);

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(
            () => ApplyThroughTheRuntimeWriterAsync(Plain, PlainRenamed("on_hand >= 0", heldCheck: null)));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/on_hand/validation");
        error.Message.ShouldStartWith("Rows of 'products' break this validation");
        (await data.QueryAsync(new AlvoQuery { Entity = "products" }, Caller, Ct)).Items.ShouldHaveSingleItem()["stock"].ShouldBe(-1L);
    }

    /// <summary>A validation on a field the same apply adds is not counted (the column does not exist yet): the DDL alone guards it.</summary>
    [Fact]
    public async Task A_validation_on_a_field_added_in_the_same_apply_is_guarded_by_the_ddl_alone()
    {
        var data = await CreateAsync(WithoutDiscount, Descriptor);
        await data.CreateAsync("products", Row(5), Caller, cancellationToken: Ct);

        await ApplyAsync(WithoutDiscount, Schema);

        (await InsertDiscountAsync("discount", "00000000-0000-0000-0000-000000000030", 51)).ShouldNotBeNull("the added field's validation is the engine's");
    }

    /// <summary>
    /// A validation that reads a sibling renamed in the same apply cannot be counted either (review I2): counting it would
    /// name a column that does not exist yet. The engine refuses it, and the refusal points at the validation — never a 500.
    /// </summary>
    [Fact]
    public async Task A_validation_reading_a_sibling_renamed_in_the_same_apply_is_refused_at_its_pointer_and_never_500()
    {
        var data = await CreateAsync(Plain, PlainDescriptor);
        await data.CreateAsync("products", new Dictionary<string, object?>(StringComparer.Ordinal) { ["stock"] = 1L, ["held"] = 5L }, Caller, cancellationToken: Ct);

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(() => ApplyAsync(Plain, PlainRenamed(onHandCheck: null, "held <= on_hand")));

        refusal.Result.Errors.ShouldHaveSingleItem().Path.ShouldBe("/entities/products/fields/held/validation");
    }

    /// <summary>
    /// Adding a validation to a populated table that carries an index — on SQLite, a table rebuild that copies the rows and
    /// re-creates the index — keeps every row and keeps the index enforced (Task 7 review m4).
    /// </summary>
    [Fact]
    public async Task Adding_a_validation_to_a_populated_indexed_table_keeps_its_rows_and_its_index()
    {
        var data = await CreateAsync(PlainIndexed(stockCheck: null), PlainDescriptor);
        foreach (var held in new long[] { 1, 2 })
        {
            await data.CreateAsync("products", new Dictionary<string, object?>(StringComparer.Ordinal) { ["stock"] = 3L, ["held"] = held }, Caller, cancellationToken: Ct);
        }

        await ApplyAsync(PlainIndexed(stockCheck: null), PlainIndexed("stock >= 0"));

        (await data.QueryAsync(new AlvoQuery { Entity = "products" }, Caller, Ct)).Items.Count.ShouldBe(2, "the rebuild copies every row");
        (await InsertHeldAsync("00000000-0000-0000-0000-000000000040", stock: 1, held: 1)).ShouldNotBeNull("the unique index survived the rebuild");
        (await InsertHeldAsync("00000000-0000-0000-0000-000000000041", stock: -1, held: 9)).ShouldNotBeNull("and the new validation holds");
        (await InsertHeldAsync("00000000-0000-0000-0000-000000000042", stock: 1, held: 9)).ShouldBeNull();
    }

    private Task<Exception?> InsertHeldAsync(string id, long stock, long held) =>
        ExecuteOutsideThePortAsync($"INSERT INTO \"products\" (\"id\", \"stock\", \"held\") VALUES ('{id}', {stock}, {held})");

    /// <summary><see cref="Schema"/> without the discount field.</summary>
    private static SchemaModel WithoutDiscount => new([
        Schema.Entities[0] with { Fields = [.. Schema.Entities[0].Fields.Where(declared => declared.Name != "discount")] },
    ]);

    /// <summary>
    /// A products entity with nothing a rename could break — no computed field reading stock, no hidden flag — for the
    /// facts that rename stock: stock (required) and held.
    /// </summary>
    private static SchemaModel Plain => new([PlainProducts(new FieldSchema { Name = "stock", Type = SchemaField.Integer, Required = true }, heldCheck: null)]);

    /// <summary><see cref="Plain"/> with stock renamed to on_hand, and the given validations.</summary>
    private static SchemaModel PlainRenamed(string? onHandCheck, string? heldCheck) => new([
        PlainProducts(
            new FieldSchema { Name = "on_hand", RenamedFrom = "stock", Type = SchemaField.Integer, Required = true, CheckExpression = onHandCheck },
            heldCheck),
    ]);

    /// <summary><see cref="Plain"/> with a unique index on held and the given validation on stock.</summary>
    private static SchemaModel PlainIndexed(string? stockCheck) => new([
        PlainProducts(new FieldSchema { Name = "stock", Type = SchemaField.Integer, Required = true, CheckExpression = stockCheck }, heldCheck: null)
            with { Indexes = [new IndexSchema(["held"], Unique: true)] },
    ]);

    private static EntitySchema PlainProducts(FieldSchema stock, string? heldCheck) => new()
    {
        Name = "products",
        Tenancy = TenancyMode.Global,
        Fields =
        [
            new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
            stock,
            new FieldSchema { Name = "held", Type = SchemaField.Integer, Nullable = true, CheckExpression = heldCheck },
        ],
    };

    /// <summary>The descriptor <see cref="Plain"/> is primed with: both fields writable, every rule open.</summary>
    private static AlvoDescriptor PlainDescriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "check-constraints-plain",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            ["products"] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["stock"] = new() { Type = DescField.Integer, Required = true },
                    ["held"] = new() { Type = DescField.Integer },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
            },
        },
    };

    private static Dictionary<string, object?> Reservation(Guid product, long quantity) =>
        new(StringComparer.Ordinal) { ["product_id"] = product, ["quantity"] = quantity };

    /// <summary>
    /// <see cref="Descriptor"/> plus reservations, whose quantities products.reserved sums under the validation
    /// "value &lt;= stock".
    /// </summary>
    private static AlvoDescriptor ReservedDescriptor => Descriptor with
    {
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            ["products"] = Descriptor.Entities["products"] with
            {
                Fields = new Dictionary<string, FieldDescriptor>(Descriptor.Entities["products"].Fields, StringComparer.Ordinal)
                {
                    ["reserved"] = new()
                    {
                        Type = DescField.Integer,
                        Rollup = new Rollup { From = "reservations", Op = RollupOp.Sum, Field = "quantity" },
                        Validation = "value <= stock",
                    },
                },
            },
            ["reservations"] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["product_id"] = new() { Type = DescField.Ref, Entity = "products", Required = true },
                    ["quantity"] = new() { Type = DescField.Integer, Required = true },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
            },
        },
    };

    /// <summary>The same two entities as a schema.</summary>
    private static SchemaModel ReservedSchema => new([
        Schema.Entities[0] with
        {
            Fields =
            [
                .. Schema.Entities[0].Fields,
                new FieldSchema
                {
                    Name = "reserved", Type = SchemaField.Integer, Nullable = true, CheckExpression = "reserved <= stock",
                    Rollup = new RollupSchema { From = "reservations", Op = RollupOperation.Sum, Field = "quantity", Via = "product_id" },
                },
            ],
        },
        new EntitySchema
        {
            Name = "reservations",
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema
                {
                    Name = "product_id", Type = SchemaField.Ref, Required = true,
                    Reference = new RefSchema("products", MMLib.Alvo.Schema.OnDelete.Cascade),
                },
                new FieldSchema { Name = "quantity", Type = SchemaField.Integer, Required = true },
            ],
        },
    ]);

    private static DateTimeOffset Instant => new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, object?> Row(long stock) => new(StringComparer.Ordinal) { ["stock"] = stock };

    /// <summary>The suite's descriptor, <see cref="CheckSchemas.Descriptor"/>.</summary>
    protected static AlvoDescriptor Descriptor => CheckSchemas.Descriptor;

    /// <summary>The suite's schema with every validation, <see cref="CheckSchemas.Validated"/>.</summary>
    protected static SchemaModel Schema => CheckSchemas.Validated;
}
