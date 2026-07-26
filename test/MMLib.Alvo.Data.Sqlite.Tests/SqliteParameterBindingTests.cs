using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using System.Data;
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
    /// Pins the <em>engine</em> behaviour the binder exists to avoid, not the binder itself: EF's SQLite
    /// <c>Guid</c> mapping stores an upper-case <c>TEXT</c>, so the same value hand-formatted lower-case
    /// matches nothing — and matches nothing <em>silently</em>, which under a negated predicate fails open.
    /// It constructs its own parameter deliberately; what pins the binder's own mechanism is
    /// <see cref="Every_column_binds_with_the_db_type_efs_own_mapping_chose"/>, because
    /// <c>Microsoft.Data.Sqlite</c> re-serialises a <see cref="Guid"/> to the same text with or without a
    /// mapping, so no round-trip alone could tell the two apart.
    /// </summary>
    [Fact]
    public async Task A_guid_hand_formatted_as_lower_case_text_matches_nothing_on_this_engine()
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
    /// Every value the data path can bind reaches ADO.NET with the <see cref="DbType"/> EF's own mapping
    /// chose — not with the <c>String</c> the provider infers for an unmapped value. That is the whole
    /// premise of this class, and asserting the <see cref="DbType"/> rather than merely non-nullness is
    /// what makes it testable: <c>Microsoft.Data.Sqlite</c> re-serialises a <see cref="Guid"/> to the same
    /// text either way, so a positive round-trip alone cannot tell a mapped parameter from a naive one.
    /// </summary>
    [Theory]
    [InlineData("owner_id", DbType.Guid)]
    [InlineData("plate", DbType.String)]
    [InlineData("mileage", DbType.Int64)]
    [InlineData("price", DbType.Decimal)]
    [InlineData("is_public", DbType.Boolean)]
    [InlineData("due_on", DbType.Date)]
    [InlineData("created_at", DbType.DateTimeOffset)]
    public async Task Every_column_binds_with_the_db_type_efs_own_mapping_chose(string field, DbType expected)
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        new PredicateParameterBinder(context)
            .Bind(Column(context, field), PolicyParameterPrefix.Filter + "0", SampleFor(field))
            .DbType.ShouldBe(expected);
    }

    /// <summary>
    /// The shape C1 was: a <c>uuid</c> column compared against a value that arrived as a
    /// <see cref="string"/> — a caller filter's JSON value, say. Binding it by the <em>value's</em> type
    /// picks the <c>string</c> mapping and the comparison silently matches nothing; binding it by the
    /// <em>column's</em> type is the guarantee this class claims to make.
    /// </summary>
    [Fact]
    public async Task A_uuid_column_matches_a_string_typed_value_bound_through_the_column()
    {
        var ownerId = Guid.NewGuid();
        var factory = await SeededFactoryAsync(ownerId);

        using var context = factory.Create();
        var bound = new PredicateParameterBinder(context)
            .Bind(Column(context, "owner_id"), PolicyParameterPrefix.Using + "0", ownerId.ToString("D", CultureInfo.InvariantCulture));

        (await CountAsync(context, CountByOwner, bound)).ShouldBe(1);
    }

    /// <summary>
    /// The second shape, and the one no layer above coerces: <c>CelTypeChecker</c> collapses
    /// <c>FieldType.Date</c> and <c>FieldType.DateTime</c> into one <c>Timestamp</c>, while
    /// <c>FieldClrTypeMap</c> keeps them as <see cref="DateOnly"/> and <see cref="DateTimeOffset"/>. A
    /// timestamp-typed value against a <c>date</c> column must still find the row.
    /// </summary>
    [Fact]
    public async Task A_date_column_matches_a_timestamp_typed_value_bound_through_the_column()
    {
        var dueOn = new DateOnly(2026, 7, 26);
        var factory = await FactoryAsync();
        await AlvoDataSeed.SeedAsync(factory, Seed(Guid.NewGuid(), dueOn), TestContext.Current.CancellationToken);

        using var context = factory.Create();
        var bound = new PredicateParameterBinder(context).Bind(
            Column(context, "due_on"),
            PolicyParameterPrefix.Using + "0",
            new DateTimeOffset(dueOn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

        var matched = await CountAsync(
            context, "SELECT COUNT(*) FROM \"vehicle\" WHERE \"due_on\" = @alvo_u0", bound);

        matched.ShouldBe(1);
    }

    /// <summary>
    /// A value the column's type cannot accept is refused loudly rather than coerced to something
    /// arbitrary or bound with the wrong mapping — a silent mismatch is what C1 was.
    /// </summary>
    [Fact]
    public async Task A_value_the_column_cannot_hold_is_refused_loudly()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();
        var binder = new PredicateParameterBinder(context);

        Should.Throw<InvalidOperationException>(
            () => binder.Bind(Column(context, "owner_id"), PolicyParameterPrefix.Filter + "0", "not-a-uuid"));
    }

    [Fact]
    public async Task A_null_binds_against_a_column_as_the_ado_net_null_sentinel()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        new PredicateParameterBinder(context)
            .Bind(Column(context, "owner_id"), PolicyParameterPrefix.Filter + "0", null)
            .Value.ShouldBe(DBNull.Value);
    }

    [Fact]
    public async Task A_value_typed_bind_still_carries_the_null_sentinel()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        new PredicateParameterBinder(context)
            .Bind(PolicyParameterPrefix.Filter + "0", null)
            .Value.ShouldBe(DBNull.Value);
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
    /// The last place a forgotten explicit parameter prefix can be caught. A <c>PolicyDecision</c> carries
    /// three predicates, each numbering its parameters from zero; if two of them render with one prefix,
    /// both bags carry <c>alvo_p0</c> and the engine binds whichever it sees last — no exception, and one
    /// predicate's value substituted into another's comparison.
    /// </summary>
    [Fact]
    public async Task Two_bags_claiming_one_name_are_refused_rather_than_bound_twice()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();
        var binder = new PredicateParameterBinder(context);

        Should.Throw<InvalidOperationException>(() => binder.Bind(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["alvo_p0"] = Guid.NewGuid() },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["alvo_p0"] = Guid.NewGuid() }));
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

    private Task<AlvoDataContextFactory> FactoryAsync() => FactoryAsync(new SchemaModel([AlvoDataFixtures.Vehicle]));

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

    private static IProperty Column(DbContext context, string field) =>
        context.Model.FindEntityType("vehicle")!.FindProperty(field)!;

    private static object SampleFor(string field) => field switch
    {
        "owner_id" => Guid.NewGuid(),
        "plate" => "ACME-001",
        "mileage" => 42L,
        "price" => 12.34m,
        "is_public" => true,
        "due_on" => new DateOnly(2026, 7, 26),
        _ => DateTimeOffset.UnixEpoch,
    };

    private static Dictionary<string, IReadOnlyList<Data.AlvoRecord>> Seed(Guid ownerId, DateOnly? dueOn = null) =>
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
                    ["due_on"] = dueOn,
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
