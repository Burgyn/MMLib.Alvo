using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using System.Globalization;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of the shared decimal-comparison table — the engine whose <c>numeric</c> answers
/// correctly without any repair, and therefore the reference the SQLite leg is measured against.
/// </summary>
/// <remarks>
/// The point of running it here is not that it might fail; it is that a per-engine copy of this table is how
/// the two engines drift, and a table with only one implementation records one engine's behaviour rather than
/// their agreement. If a fact here does fail, the repair is being applied where it must not be — an
/// engine-agnostic-core violation in the other direction.
/// </remarks>
public sealed class PostgreSqlAlvoDataComparisonTests : AlvoDataComparisonTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override async Task<int> MatchesAsync(string rule, decimal? price, long? mileage)
    {
        var host = await _fixture.StartAsync(new SchemaModel([AlvoDataFixtures.Vehicle]));
        var factory = host.Services.GetRequiredService<AlvoDataContextFactory>();
        await AlvoDataSeed.SeedAsync(factory, Seed(price, mileage), TestContext.Current.CancellationToken);

        using var context = factory.Create();
        var predicate = Render(host, rule);

        return (int)await CountAsync(context, predicate.Sql, MMLib.Alvo.Tests.Data.PolicyPredicateParameters.Bind(context, context.Rows("vehicle").EntityType, predicate.Parameters));
    }

    private static SqlPredicate Render(PostgreSqlAlvoDataHost host, string rule)
    {
        var compiled = host.Services.GetRequiredService<ICelCompiler>()
            .Compile(rule, CelProfile.Rule, AlvoDataFixtures.Vehicle);
        if (!compiled.IsSuccess)
        {
            throw new InvalidOperationException(
                $"'{rule}' did not compile: {string.Join("; ", compiled.Errors.Select(error => error.Message))}");
        }

        return host.Services.GetRequiredService<IPredicateRenderer>().Render(
            compiled.Expression!, AlvoDataFixtures.Caller, new PostgreSqlFieldSqlRenderer(), PolicyParameterPrefix.Using);
    }

    private static Dictionary<string, IReadOnlyList<Alvo.Data.AlvoRecord>> Seed(decimal? price, long? mileage) =>
        new(StringComparer.Ordinal)
        {
            ["vehicle"] =
            [
                new Alvo.Data.AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
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
