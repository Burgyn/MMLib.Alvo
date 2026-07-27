using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using System.Globalization;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// SQLite's leg of the shared decimal-comparison table. The engine this suite exists for: it has no decimal
/// storage class, so every fact here inverts without the driver's value repair.
/// </summary>
public sealed class SqliteAlvoDataComparisonTests : AlvoDataComparisonTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    protected override async Task<int> MatchesAsync(string rule, decimal? price, long? mileage)
    {
        var host = await _fixture.StartAsync(new SchemaModel([AlvoDataFixtures.Vehicle]));
        var factory = host.Services.GetRequiredService<AlvoDataContextFactory>();
        await AlvoDataSeed.SeedAsync(factory, Seed(price, mileage), TestContext.Current.CancellationToken);

        using var context = factory.Create();
        var predicate = Render(host, rule);

        return (int)await CountAsync(context, predicate.Sql, new PredicateParameterBinder(context).BindPolicyPredicate(predicate.Parameters));
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
