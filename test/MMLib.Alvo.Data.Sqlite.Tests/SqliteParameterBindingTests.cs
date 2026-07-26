using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.Sqlite.Tests;

public sealed class SqliteParameterBindingTests : IAsyncDisposable
{
    private const string CountByOwner = "SELECT COUNT(*) FROM \"vehicle\" WHERE \"owner_id\" = @alvo_u0";

    private readonly SqliteAlvoDataFixture _fixture = new();

    [Fact]
    public async Task A_guid_bound_through_efs_mapping_finds_the_row_it_wrote()
    {
        var ownerId = Guid.NewGuid();
        var factory = await SeededFactoryAsync(ownerId);

        using var context = factory.Create();
        var binder = new PredicateParameterBinder(context);
        var matched = await CountAsync(context, CountByOwner, binder.Bind(PolicyParameterPrefix.Using + "0", ownerId));

        matched.ShouldBe(1);
    }

    /// <summary>
    /// The spike's own first false negative, kept as a regression: EF's SQLite <c>Guid</c> mapping stores
    /// an upper-case <c>TEXT</c>, so the same value hand-formatted lower-case matches nothing — and
    /// matches nothing <em>silently</em>, which under a negated predicate would fail open.
    /// </summary>
    [Fact]
    public async Task The_same_guid_hand_formatted_as_lower_case_text_matches_nothing()
    {
        var ownerId = Guid.NewGuid();
        var factory = await SeededFactoryAsync(ownerId);

        using var context = factory.Create();
        var handFormatted = new SqliteParameter(
            "@alvo_u0", ownerId.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant());
        var matched = await CountAsync(context, CountByOwner, handFormatted);

        matched.ShouldBe(0);
    }

    /// <summary>
    /// Every value the data path can bind reaches ADO.NET with a real provider type. <c>decimal</c>,
    /// <c>bool</c>, <see cref="DateTimeOffset"/> and <see cref="DateOnly"/> are all stored as
    /// <c>TEXT</c>/<c>INTEGER</c> on SQLite by mappings only EF knows, so the <see cref="Guid"/> case
    /// above is the family, not the exception.
    /// </summary>
    [Fact]
    public async Task Every_awkward_clr_type_binds_with_a_real_db_type()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();
        var binder = new PredicateParameterBinder(context);

        object?[] values = [Guid.NewGuid(), "text", 42L, 12.34m, true, DateTimeOffset.UnixEpoch, new DateOnly(2026, 7, 26)];

        foreach (var value in values)
        {
            binder.Bind(PolicyParameterPrefix.Filter + "0", value).Value.ShouldNotBeNull();
        }

        binder.Bind(PolicyParameterPrefix.Filter + "0", null).Value.ShouldBe(DBNull.Value);
    }

    [Fact]
    public async Task A_bound_parameters_name_carries_the_providers_marker()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        new PredicateParameterBinder(context).Bind(PolicyParameterPrefix.RowId, Guid.NewGuid())
            .ParameterName.ShouldBe("@" + PolicyParameterPrefix.RowId);
    }

    /// <summary>
    /// One call binds every predicate's bag at once, and the names survive unchanged — the whole point of
    /// three disjoint prefixes is that two bags can be merged without one value overwriting another.
    /// </summary>
    [Fact]
    public async Task Several_predicate_bags_bind_in_one_call_without_losing_a_name()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        var bound = new PredicateParameterBinder(context).Bind(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["alvo_u0"] = Guid.NewGuid() },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["alvo_t0"] = Guid.NewGuid() },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["alvo_f0"] = "text" });

        bound.Select(parameter => parameter.ParameterName)
            .ShouldBe(["@alvo_u0", "@alvo_t0", "@alvo_f0"], ignoreOrder: true);
    }

    /// <summary>
    /// A value the provider has no mapping for is refused rather than handed to ADO.NET with an inferred
    /// type — an inferred type is exactly the silent misrepresentation this binder exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_value_with_no_relational_mapping_is_refused_rather_than_inferred()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();
        var binder = new PredicateParameterBinder(context);

        Should.Throw<InvalidOperationException>(() => binder.Bind(PolicyParameterPrefix.Filter + "0", new object()));
    }

    private Task<AlvoDataContextFactory> FactoryAsync() => FactoryAsync(new SchemaModel([AlvoDataSqlSnapshotTests.SnapshotEntity]));

    private async Task<AlvoDataContextFactory> FactoryAsync(SchemaModel schema)
    {
        var host = await _fixture.StartAsync(schema);
        return host.Services.GetRequiredService<AlvoDataContextFactory>();
    }

    private async Task<AlvoDataContextFactory> SeededFactoryAsync(Guid ownerId)
    {
        var factory = await FactoryAsync();
        await AlvoDataSeed.SeedAsync(factory, Seed(ownerId), TestContext.Current.CancellationToken);
        return factory;
    }

    private static Dictionary<string, IReadOnlyList<Data.AlvoRecord>> Seed(Guid ownerId) =>
        new(StringComparer.Ordinal)
        {
            ["vehicle"] =
            [
                new Data.AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = Guid.NewGuid(),
                    ["tenant_id"] = Guid.NewGuid(),
                    ["owner_id"] = ownerId,
                    ["plate"] = "ACME-001",
                }),
            ],
        };

    private static async Task<long> CountAsync(DbContext context, string sql, params DbParameter[] parameters)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
