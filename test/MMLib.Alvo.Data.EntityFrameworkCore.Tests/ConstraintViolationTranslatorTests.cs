using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Schema;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// What a caller is told when the engine refuses a write on a constraint: which kind it was, which of the
/// entity's own fields it names, and — just as load-bearing — when it names nothing and the provider's own
/// exception is left to propagate untranslated.
/// </summary>
/// <remarks>
/// The dialect is scripted rather than real, because the split of labour is the point: a driver owns the
/// decoding and this type owns resolving whatever came back against the model. The model, by contrast, is
/// the real runtime one — the index names it is asked about are read off that model rather than spelled
/// here, so EF's own naming convention cannot drift away from what these facts ask about.
/// </remarks>
public class ConstraintViolationTranslatorTests
{
    /// <summary>The write actually runs, on the overload with no result to give it away.</summary>
    [Fact]
    public async Task The_void_overload_runs_the_write()
    {
        var ran = false;

        await ConstraintViolationTranslator.TranslatedAsync(
            () =>
            {
                ran = true;
                return Task.CompletedTask;
            },
            Decoding(null),
            Rows,
            Part,
            callerKeyed: false,
            Applied,
            Nothing);

        ran.ShouldBeTrue();
    }

    /// <summary>
    /// And a violation it recognises is thrown rather than swallowed — on the same overload, whose caller
    /// has no return value with which to notice a write that silently did nothing.
    /// </summary>
    [Fact]
    public async Task The_void_overload_throws_the_translated_violation()
    {
        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(
            async () => await ConstraintViolationTranslator.TranslatedAsync(
                () => Task.FromException(Collision()), Decoding(OnColumns("code")), Rows, Part, callerKeyed: false, Applied, Nothing));

        refused.Kind.ShouldBe(AlvoConstraintKind.Unique);
        refused.Fields.ShouldBe(["code"]);
    }

    /// <summary>
    /// PostgreSQL reports a constraint name and no columns at all, so the name has to resolve against the
    /// model's own index or that engine answers 500 where SQLite answers 409.
    /// </summary>
    [Fact]
    public async Task A_constraint_named_without_columns_resolves_through_the_models_index()
    {
        var rows = Rows;
        var index = rows.GetIndexes().Single(candidate => candidate.IsUnique).GetDatabaseName()!;

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(
            async () => await Translated(OnConstraint(index), rows, callerKeyed: false));

        refused.Fields.ShouldBe(["code"]);
    }

    /// <summary>
    /// A primary key is a constraint but not an index, so <c>GetIndexes</c> carries nothing for it and the
    /// row key has to be resolved on the side. Without that, the one collision a create-or-replace caller
    /// can actually repair fell through to the raw provider exception.
    /// </summary>
    [Fact]
    public async Task A_primary_key_named_without_columns_resolves_to_the_row_key()
    {
        var rows = Rows;
        var key = rows.FindPrimaryKey()!.GetName()!;

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(
            async () => await Translated(OnConstraint(key), rows, callerKeyed: true));

        refused.Fields.ShouldBe(["id"]);
    }

    /// <summary>
    /// A conflict confined to columns the caller may not write is a broken invariant rather than a request
    /// they can repair, so the provider's exception keeps propagating with its own stack trace.
    /// </summary>
    [Fact]
    public async Task A_collision_confined_to_framework_columns_keeps_propagating()
        => await Should.ThrowAsync<SqliteException>(
            async () => await Translated(OnColumns("tenant_id"), Rows, callerKeyed: false));

    /// <summary>
    /// The row key is a framework column on every write that mints its own key — <c>callerKeyed</c> lifts the
    /// exclusion for <c>id</c> alone, and only on the write that supplied it.
    /// </summary>
    [Fact]
    public async Task A_row_key_the_caller_did_not_choose_keeps_propagating()
        => await Should.ThrowAsync<SqliteException>(
            async () => await Translated(OnColumns("id"), Rows, callerKeyed: false));

    /// <summary>And on the write that did choose it, the same collision is an ordinary conflict.</summary>
    [Fact]
    public async Task A_row_key_the_caller_chose_is_reported_as_a_conflict()
    {
        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(
            async () => await Translated(OnColumns("id"), Rows, callerKeyed: true));

        refused.Fields.ShouldBe(["id"]);
    }

    /// <summary>A broken validation on the written entity's own field names that field (HX-11 Ruling 24).</summary>
    [Fact]
    public async Task A_check_on_the_written_entity_names_its_validated_field()
    {
        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(
            async () => await Checked(CheckConstraintNames.For("part", "code"), Part, Nothing));

        refused.Kind.ShouldBe(AlvoConstraintKind.Check);
        refused.Fields.ShouldBe(["code"]);
    }

    /// <summary>A field the descriptor hides is never named, whatever this caller's own mask resolves to.</summary>
    [Fact]
    public async Task A_check_on_a_hidden_field_names_nothing() =>
        (await Should.ThrowAsync<AlvoConstraintViolationException>(
            async () => await Checked(CheckConstraintNames.For("part", "code"), Part, new HashSet<string>(StringComparer.Ordinal) { "code" })))
            .Fields.ShouldBeEmpty();

    /// <summary>
    /// A CHECK this schema cannot place — no name, or a name no validated field recomputes to — is still the caller's 422
    /// naming nothing, never the provider's exception: the descriptor's integrity held.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("ck_someone_elses")]
    public async Task A_check_that_cannot_be_placed_is_still_a_refusal_naming_nothing(string? name)
    {
        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(async () => await Checked(name, Part, Nothing));

        (refused.Kind, refused.Fields.Count).ShouldBe((AlvoConstraintKind.Check, 0));
    }

    /// <summary>
    /// A parent's validation broken by a rollup recompute points at the child field the rollup aggregates — the value the
    /// caller sent — and never at the parent's field (HX-11 Ruling 25).
    /// </summary>
    [Fact]
    public async Task A_parent_check_broken_through_a_sum_names_the_child_field()
    {
        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(
            async () => await Checked(CheckConstraintNames.For("bin", "reserved"), Line, Nothing));

        refused.Fields.ShouldBe(["quantity"]);
    }

    /// <summary>A <c>count</c> aggregates no child field, so there is nothing of the caller's to point at.</summary>
    [Fact]
    public async Task A_parent_check_broken_through_a_count_names_nothing() =>
        (await Should.ThrowAsync<AlvoConstraintViolationException>(
            async () => await Checked(CheckConstraintNames.For("bin", "lines"), Line, Nothing)))
            .Fields.ShouldBeEmpty();

    /// <summary>The child field the rollup reads is named only when the caller may know of it.</summary>
    [Fact]
    public async Task A_parent_check_broken_through_a_hidden_child_field_names_nothing() =>
        (await Should.ThrowAsync<AlvoConstraintViolationException>(
            async () => await Checked(CheckConstraintNames.For("bin", "reserved"), Line, new HashSet<string>(StringComparer.Ordinal) { "quantity" })))
            .Fields.ShouldBeEmpty();

    /// <summary>
    /// A delete sends no body, so a parent's validation its rollup recompute breaks points at nothing: the child field is
    /// not a value this caller sent and could change (HX-11 Ruling 25).
    /// </summary>
    [Fact]
    public async Task A_parent_check_broken_by_a_delete_names_nothing()
    {
        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(
            async () => await Checked(CheckConstraintNames.For("bin", "reserved"), Line, Nothing, bodySent: false));

        (refused.Kind, refused.Fields.Count).ShouldBe((AlvoConstraintKind.Check, 0));
    }

    /// <summary>
    /// A rollup over its own entity is still a parent's field: a child write that pushes its parent past the validation
    /// names the child field it sent, never the parent's rollup field (HX-11 Ruling 25, security review m-1).
    /// </summary>
    [Fact]
    public async Task A_self_rollup_s_check_names_the_child_field_never_the_rollup_field() =>
        (await Should.ThrowAsync<AlvoConstraintViolationException>(
            async () => await Checked(CheckConstraintNames.For("task", "effort_total"), Subtasks, Nothing)))
            .Fields.ShouldBe(["effort"]);

    /// <summary>A <c>count</c> over its own entity aggregates no child field, so it names nothing.</summary>
    [Fact]
    public async Task A_self_count_s_check_names_nothing() =>
        (await Should.ThrowAsync<AlvoConstraintViolationException>(
            async () => await Checked(CheckConstraintNames.For("task", "subtask_count"), Subtasks, Nothing)))
            .Fields.ShouldBeEmpty();

    private static Task Checked(string? constraintName, EntitySchema written, IReadOnlySet<string> unnameable, bool bodySent = true) =>
        ConstraintViolationTranslator.TranslatedAsync(
            () => Task.FromException(Collision()),
            Decoding(new SqlConstraintViolation { Kind = AlvoConstraintKind.Check, ConstraintName = constraintName }),
            Rows,
            written,
            callerKeyed: false,
            Applied,
            unnameable,
            bodySent);

    private static Task Translated(SqlConstraintViolation decoded, IEntityType rows, bool callerKeyed) =>
        ConstraintViolationTranslator.TranslatedAsync(
            () => Task.FromException(Collision()), Decoding(decoded), rows, Part, callerKeyed, Applied, Nothing);

    private static IReadOnlySet<string> Nothing { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// <see cref="Part"/> with its <c>code</c> validated, and a bin whose validated <c>reserved</c> sums the lines'
    /// <c>quantity</c> and whose validated <c>lines</c> counts them.
    /// </summary>
    private static SchemaModel Applied => new([
        Part with { Fields = [.. Part.Fields.Select(declared => declared.Name == "code" ? declared with { CheckExpression = "code != ''" } : declared)] },
        new EntitySchema
        {
            Name = "bin",
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                new FieldSchema
                {
                    Name = "reserved", Type = FieldType.Integer, CheckExpression = "reserved <= 10",
                    Rollup = new RollupSchema { From = "line", Op = RollupOperation.Sum, Field = "quantity", Via = "bin_id" },
                },
                new FieldSchema
                {
                    Name = "lines", Type = FieldType.Integer, CheckExpression = "lines <= 3",
                    Rollup = new RollupSchema { From = "line", Op = RollupOperation.Count, Via = "bin_id" },
                },
            ],
        },
        Line,
        Subtasks,
    ]);

    /// <summary>A task whose validated rollups aggregate its own subtasks: a count, and a sum of their <c>effort</c>.</summary>
    private static EntitySchema Subtasks => new()
    {
        Name = "task",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "parent_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "effort", Type = FieldType.Integer, Nullable = true },
            new FieldSchema
            {
                Name = "subtask_count", Type = FieldType.Integer, CheckExpression = "subtask_count <= 10",
                Rollup = new RollupSchema { From = "task", Op = RollupOperation.Count, Via = "parent_id" },
            },
            new FieldSchema
            {
                Name = "effort_total", Type = FieldType.Integer, CheckExpression = "effort_total <= 100",
                Rollup = new RollupSchema { From = "task", Op = RollupOperation.Sum, Field = "effort", Via = "parent_id" },
            },
        ],
    };

    private static EntitySchema Line => new()
    {
        Name = "line",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "bin_id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "quantity", Type = FieldType.Integer, Required = true },
        ],
    };

    private static SqlConstraintViolation OnColumns(params string[] columns) =>
        new() { Kind = AlvoConstraintKind.Unique, Columns = columns };

    private static SqlConstraintViolation OnConstraint(string constraintName) =>
        new() { Kind = AlvoConstraintKind.Unique, ConstraintName = constraintName };

    private static SqliteException Collision() => new("constraint failed", 19);

    private static DecodingSqlDialect Decoding(SqlConstraintViolation? decoded) => new(decoded);

    /// <summary>
    /// One tenant-scoped entity with one unique field, so its unique index spans <c>(tenant_id, code)</c> —
    /// the shape #137 gives every unique constraint, and the one whose managed half has to be stripped.
    /// </summary>
    private static EntitySchema Part => new()
    {
        Name = "part",
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "code", Type = FieldType.String, Required = true, Unique = true, MaxLength = 32 },
        ],
    };

    private static IEntityType Rows => ReadModelFixture.Rows(Part);

    /// <summary>
    /// A dialect that decodes whatever the test scripted, so the model-side half of the translation is the
    /// only thing under test.
    /// </summary>
    private sealed class DecodingSqlDialect(SqlConstraintViolation? decoded) : IAlvoSqlDialect
    {
        public string RowLockClause(PreImageMutation mutation) => string.Empty;

        public string RenderTable(EntitySchema entity, PreImageMutation? lockedPreImageFor) =>
            AlvoSqlIdentifier.Quote(entity!.Name);

        public string RenderColumn(string columnName) => AlvoSqlIdentifier.Quote(columnName);

        public string RenderNullProjection(string storeType) => $"CAST(NULL AS {storeType})";

        public SqlConstraintViolation? DecodeConstraintViolation(DbException failure) => decoded;
    }
}
