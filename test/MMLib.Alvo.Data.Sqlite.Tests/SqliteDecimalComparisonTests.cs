using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using System.Globalization;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// EF stores a <c>decimal</c> in a <c>TEXT</c> column on SQLite, and SQLite compares <c>TEXT</c>
/// lexicographically — so an unguarded <c>price &gt; 100</c> matches a row whose price is <c>12.34</c>,
/// while PostgreSQL's <c>numeric</c> answers correctly. That is not imprecision, it is an inverted answer,
/// and on a <c>USING</c> rule that gates access on an amount it is a fail-open authorization outcome on one
/// engine — §0 principle 3's exact prohibition. These facts run the whole path a rule really takes:
/// compile the CEL, render it through the driver's own <see cref="IFieldSqlRenderer"/>, bind through EF's
/// type mapping, and execute against a real SQLite file.
/// </summary>
public sealed class SqliteDecimalComparisonTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    [Theory]
    [InlineData("price > 100", 0)]
    [InlineData("price > 2", 1)]
    [InlineData("price >= 12.34", 1)]
    [InlineData("price < 100", 1)]
    [InlineData("price <= 2", 0)]
    [InlineData("price > 12.34", 0)]
    public async Task A_decimal_comparison_answers_numerically_not_lexicographically(string rule, int expected)
        => (await MatchesAsync(rule, 12.34m)).ShouldBe(expected);

    /// <summary>
    /// The same defect reached through a whole-number literal, which the type checker accepts against a
    /// decimal field (<c>IsNumeric(left) &amp;&amp; IsNumeric(right)</c>). Here the bound parameter is an
    /// <c>INTEGER</c> and the column is <c>TEXT</c>, so SQLite's type ordering alone — every <c>TEXT</c>
    /// value sorts above every <c>INTEGER</c> — produces the wrong answer even before collation.
    /// </summary>
    [Theory]
    [InlineData("price > 3", 0)]
    [InlineData("price < 3", 1)]
    public async Task A_whole_number_literal_against_a_decimal_column_answers_numerically(string rule, int expected)
        => (await MatchesAsync(rule, 2.50m)).ShouldBe(expected);

    /// <summary>
    /// Equality is the shape that fails <em>open</em>: with the column stored as <c>'100.00'</c> and the
    /// literal bound as an <c>INTEGER</c> <c>100</c>, a textual comparison makes <c>price == 100</c> miss
    /// and therefore <c>price != 100</c> match — a rule meant to exclude the row admits it.
    /// </summary>
    [Theory]
    [InlineData("price == 100", 1)]
    [InlineData("price != 100", 0)]
    public async Task Equality_against_a_decimal_column_answers_numerically(string rule, int expected)
        => (await MatchesAsync(rule, 100m)).ShouldBe(expected);

    /// <summary>
    /// A <see langword="null"/> decimal satisfies neither a comparison nor its negation — the cast must not
    /// disturb the three-valued fold every predicate goes through.
    /// </summary>
    [Theory]
    [InlineData("price > 1", 0)]
    [InlineData("!(price > 1)", 1)]
    [InlineData("has(price)", 0)]
    public async Task A_null_decimal_keeps_its_three_valued_answer(string rule, int expected)
        => (await MatchesAsync(rule, null)).ShouldBe(expected);

    /// <summary>An integer column is already ordered by SQLite, so its comparison must stay untouched.</summary>
    [Theory]
    [InlineData("mileage > 100", 1)]
    [InlineData("mileage > 100000", 0)]
    public async Task An_integer_comparison_is_unaffected(string rule, int expected)
        => (await MatchesAsync(rule, price: null, mileage: 5000L)).ShouldBe(expected);

    private async Task<int> MatchesAsync(string rule, decimal? price, long? mileage = null)
    {
        var host = await _fixture.StartAsync(new SchemaModel([AlvoDataFixtures.Vehicle]));
        var factory = host.Services.GetRequiredService<AlvoDataContextFactory>();
        await AlvoDataSeed.SeedAsync(factory, Seed(price, mileage), TestContext.Current.CancellationToken);

        using var context = factory.Create();
        var predicate = Render(host, rule);
        var binder = new PredicateParameterBinder(context);

        return (int)await CountAsync(context, predicate.Sql, binder.Bind(predicate.Parameters));
    }

    private static SqlPredicate Render(AlvoDataHost host, string rule)
    {
        var compiled = host.Services.GetRequiredService<ICelCompiler>()
            .Compile(rule, CelProfile.Rule, AlvoDataFixtures.Vehicle);
        if (!compiled.IsSuccess)
        {
            throw new InvalidOperationException(
                $"'{rule}' did not compile: {string.Join("; ", compiled.Errors.Select(error => error.Message))}");
        }

        return host.Services.GetRequiredService<IPredicateRenderer>().Render(
            compiled.Expression!, AlvoDataFixtures.Caller, new SqliteFieldSqlRenderer(), PolicyParameterPrefix.Using);
    }

    private static Dictionary<string, IReadOnlyList<Data.AlvoRecord>> Seed(decimal? price, long? mileage) =>
        new(StringComparer.Ordinal)
        {
            ["vehicle"] =
            [
                new Data.AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = Guid.NewGuid(),
                    ["tenant_id"] = Guid.NewGuid(),
                    ["plate"] = "ACME-001",
                    ["price"] = price,
                    ["mileage"] = mileage,
                }),
            ],
        };

    private static async Task<long> CountAsync(
        DbContext context, string predicate, params System.Data.Common.DbParameter[] parameters)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"vehicle\" WHERE {predicate}";
        command.Parameters.AddRange(parameters);

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
