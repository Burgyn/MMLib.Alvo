using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using System.Data.Common;
using System.Globalization;
using Xunit;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// The one <see cref="IDifferentialProbe"/> both engines answer through: it stores the candidate row via the
/// production seeding seam (so every value carries EF's own type mapping), then counts rows under the rendered
/// predicate with every parameter bound through the production binder.
/// </summary>
/// <remarks>
/// <para>
/// Linked into both engine test projects rather than copied. A per-engine copy is precisely how a differential
/// comparison stops being differential: two probes that seed or bind slightly differently no longer compare
/// like with like, and the disagreement they exist to catch hides in the difference between them. Everything
/// engine-specific arrives as the <see cref="IAlvoSqlDialect"/> argument.
/// </para>
/// <para>
/// <c>DifferentialRuleCases</c> builds rows with <see cref="DateTime"/> values while a <c>datetime</c> field
/// maps to <see cref="DateTimeOffset"/>. The probe does <b>not</b> convert them itself: it seeds through
/// <c>AlvoDataSeed</c>, whose property-bag preparation routes every value through the production
/// <c>StoredInstant</c>. An earlier revision converted here and claimed in this remark to honour
/// <see cref="DateTimeKind"/> while not actually inspecting it — harmless for the matrix as it stands, and
/// exactly the kind of near-miss a differential test cannot see, because a wrong conversion here is applied to
/// both sides at once.
/// </para>
/// </remarks>
internal sealed class DifferentialProbe(
    AlvoDataContextFactory contexts, EntitySchema entity, IAlvoSqlDialect dialect) : IDifferentialProbe
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async Task<bool> MatchesAsync(AlvoRecord row, SqlPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(predicate);

        await ClearAsync();
        await SeedAsync(row);

        return await CountAsync(predicate) == 1;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task ClearAsync()
    {
        using var context = contexts.Create();
        await using var command = await CommandAsync(context, $"DELETE FROM {dialect.RenderTable(entity)}");

        await command.ExecuteNonQueryAsync(Token);
    }

    private Task SeedAsync(AlvoRecord row) => AlvoDataSeed.SeedAsync(
        contexts,
        new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal) { [entity.Name] = [Storable(row)] },
        Token);

    private async Task<long> CountAsync(SqlPredicate predicate)
    {
        using var context = contexts.Create();
        await using var command = await CommandAsync(
            context, $"SELECT COUNT(*) FROM {dialect.RenderTable(entity)} WHERE {predicate.Sql}");
        command.Parameters.AddRange(new PredicateParameterBinder(context).BindPolicyPredicate(predicate.Parameters));

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A command on the context's own connection. Raw ADO.NET rather than <c>ExecuteSqlRaw</c> because the
    /// predicate must be the whole <c>WHERE</c> clause verbatim — the thing under test is exactly the SQL the
    /// renderer produced, with its parameters bound the way the data path binds them.
    /// </summary>
    private static async Task<DbCommand> CommandAsync(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(Token);
        var command = connection.CreateCommand();
        command.CommandText = sql;

        return command;
    }

    /// <summary>
    /// The row with the id its table requires. Nothing else is touched — value preparation belongs to the
    /// seeding seam, which shares it with the production create path.
    /// </summary>
    private static AlvoRecord Storable(AlvoRecord row)
    {
        var values = row.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        values["id"] = Guid.NewGuid();

        return new AlvoRecord(values);
    }
}
