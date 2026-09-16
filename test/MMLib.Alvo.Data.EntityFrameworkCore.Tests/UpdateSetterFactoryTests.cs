using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The <c>SET</c> clause an update writes, asserted on the statement EF emits. A defect here writes the
/// wrong column or the wrong value, silently, inside the caller's own transaction — and every outcome-level
/// fact above this seam would still pass, because the row really was updated.
/// </summary>
/// <remarks>
/// The statement is recorded and <b>suppressed</b> by an interceptor, so no table has to exist: what is
/// under test is the text and the bound values EF composed, not what an engine did with them.
/// </remarks>
public class UpdateSetterFactoryTests
{
    /// <summary>
    /// The load-bearing property of the overload this factory selects by reflection: a value is handed over
    /// <em>as a value</em>, so it becomes a bind parameter carrying the column's own type. Its sibling
    /// overload takes an <see cref="System.Linq.Expressions.Expression"/> for the value, which EF is free to
    /// inline into the SQL text — so an overload chosen by order rather than by discriminator would be a
    /// silent first-order injection of every value a caller patches.
    /// </summary>
    [Fact]
    public async Task A_patch_sets_the_named_column_through_a_bind_parameter()
    {
        var recorded = await Update(new Dictionary<string, object?>(StringComparer.Ordinal) { ["plate"] = "ACME-1" });

        recorded.Sql.ShouldContain("UPDATE");
        recorded.Sql.ShouldContain("\"plate\" = @");
        recorded.Sql.ShouldNotContain("ACME-1");
        recorded.Values.ShouldContain("ACME-1");
    }

    /// <summary>
    /// Only the patched column is set. The setter list is projected from the patch alone, so a column the
    /// caller did not name must not appear — an update that also wrote <c>status</c> would be a lost update
    /// no caller asked for.
    /// </summary>
    [Fact]
    public async Task A_patch_sets_no_column_it_did_not_name()
    {
        var recorded = await Update(new Dictionary<string, object?>(StringComparer.Ordinal) { ["plate"] = "ACME-1" });

        recorded.Sql.ShouldNotContain("\"status\"");
        recorded.Sql.ShouldNotContain("\"mileage\"");
    }

    /// <summary>
    /// Every patched column gets its own setter, so a multi-field patch is one statement with as many
    /// assignments as the caller named.
    /// </summary>
    [Fact]
    public async Task Every_patched_column_gets_its_own_setter()
    {
        var recorded = await Update(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["plate"] = "ACME-1",
            ["status"] = "open",
        });

        recorded.Sql.ShouldContain("\"plate\" = @");
        recorded.Sql.ShouldContain("\"status\" = @");
    }

    /// <summary>
    /// The value is bound as the <b>column</b> holds it, not as the caller spelled it: a patch is funnelled
    /// through the same <c>ColumnValue</c> conversion a filter operand is, so an agent emitting JSON can
    /// write the value it can also compare against.
    /// </summary>
    [Fact]
    public async Task A_patch_value_is_bound_as_the_columns_own_type()
    {
        var recorded = await Update(new Dictionary<string, object?>(StringComparer.Ordinal) { ["mileage"] = "42" });

        recorded.Values.ShouldContain(42L);
    }

    /// <summary>
    /// A nullable column cleared to <see langword="null"/> is a real <c>SET col = NULL</c> rather than a
    /// failure to box — the reason the setter's generic argument is the read model's <em>nullable</em> type,
    /// and the one case an insert cannot express, where an absent key is the column's own default instead.
    /// </summary>
    [Fact]
    public async Task A_nulled_column_is_set_to_null_rather_than_skipped()
    {
        var recorded = await Update(new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = null });

        recorded.Sql.ShouldContain("\"status\" = NULL");
    }

    /// <summary>
    /// The entity is the authority for every field's type, so it is refused as <see langword="null"/> at the
    /// door. Asserted on an <b>empty</b> patch deliberately: a non-empty one reaches the field lookup, which
    /// raises an indistinguishable refusal of its own, and the guard would then be unfalsifiable.
    /// </summary>
    [Fact]
    public void A_null_entity_is_refused_even_for_an_empty_patch() =>
        Should.Throw<ArgumentNullException>(
            () => UpdateSetterFactory.For(null!, new Dictionary<string, object?>(StringComparer.Ordinal)))
            .ParamName.ShouldBe("entity");

    [Fact]
    public void A_null_patch_is_refused_and_named() =>
        Should.Throw<ArgumentNullException>(() => UpdateSetterFactory.For(AlvoDataFixtures.Vehicle, null!))
            .ParamName.ShouldBe("values");

    /// <summary>A field the entity does not declare earns the same refusal every unwritable field gets.</summary>
    [Fact]
    public void An_undeclared_field_is_refused_rather_than_reaching_reflection() =>
        Should.Throw<AlvoAuthorizationException>(() => UpdateSetterFactory.For(
            AlvoDataFixtures.Vehicle,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["not_a_field"] = "x" }));

    /// <summary>Runs one patch through EF and hands back the statement it composed.</summary>
    /// <param name="values">The patch.</param>
    private static async Task<RecordedStatement> Update(IReadOnlyDictionary<string, object?> values)
    {
        var recorder = new StatementRecorder();
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder();
        options.UseSqlite(connection, static sqlite => sqlite.UseRelationalNulls()).AddInterceptors(recorder);
        using var context = new AlvoDataContext(
            options.Options, new SchemaModel([AlvoDataFixtures.Vehicle]), Guid.NewGuid());

        await context.Rows(AlvoDataFixtures.Vehicle.Name)
            .ExecuteUpdateAsync(UpdateSetterFactory.For(AlvoDataFixtures.Vehicle, values));

        return recorder.Recorded
            ?? throw new InvalidOperationException("No statement reached the interceptor.");
    }

    /// <summary>One command EF was about to run: its text and the values it bound.</summary>
    /// <param name="Sql">The command text.</param>
    /// <param name="Values">Every bound parameter value, in the order EF added them.</param>
    private sealed record RecordedStatement(string Sql, IReadOnlyList<object?> Values);

    /// <summary>
    /// Records the one statement under test and suppresses it, so the assertion is about what EF composed
    /// rather than about a table this suite would otherwise have to create and keep in step with the schema.
    /// </summary>
    private sealed class StatementRecorder : DbCommandInterceptor
    {
        internal RecordedStatement? Recorded { get; private set; }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return InterceptionResult<int>.SuppressWithResult(0);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
        }

        private void Record(DbCommand command) => Recorded = new RecordedStatement(
            command.CommandText,
            [.. command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value)]);
    }
}
