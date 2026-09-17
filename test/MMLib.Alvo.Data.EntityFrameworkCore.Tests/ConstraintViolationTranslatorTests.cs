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
            callerKeyed: false);

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
                () => Task.FromException(Collision()), Decoding(OnColumns("code")), Rows, Part, callerKeyed: false));

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

    private static Task Translated(SqlConstraintViolation decoded, IEntityType rows, bool callerKeyed) =>
        ConstraintViolationTranslator.TranslatedAsync(
            () => Task.FromException(Collision()), Decoding(decoded), rows, Part, callerKeyed);

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
