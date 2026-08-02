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
        var matched = await CountAsync(context, CountByOwner, BindWithoutColumn(context, PolicyParameterPrefix.Using + "0", ownerId));

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

        BindThroughColumn(context, field, PolicyParameterPrefix.Filter + "0", SampleFor(field))
            .DbType.ShouldBe(expected);
    }

    /// <summary>
    /// The same guarantee on the one path that has no column: a rendered <c>SqlPredicate</c>'s bag carries
    /// names and values only, so it binds by the <em>value's</em> type — and must still reach ADO.NET with
    /// EF's own <see cref="DbType"/> rather than the <c>String</c> a provider infers for an unmapped value.
    /// </summary>
    [Theory]
    [InlineData("owner_id", DbType.Guid)]
    [InlineData("plate", DbType.String)]
    [InlineData("mileage", DbType.Int64)]
    [InlineData("price", DbType.Decimal)]
    [InlineData("is_public", DbType.Boolean)]
    [InlineData("due_on", DbType.Date)]
    [InlineData("created_at", DbType.DateTimeOffset)]
    public async Task Every_policy_predicate_bind_carries_the_db_type_efs_own_mapping_chose(string field, DbType expected)
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        BindWithoutColumn(context, PolicyParameterPrefix.Using + "0", SampleFor(field))
            .DbType.ShouldBe(expected);
    }

    /// <summary>
    /// The column path's real advantage: a <see langword="null"/> still gets the <em>column's</em>
    /// <see cref="DbType"/>, where a path with no column has no type to take it from. That is
    /// what keeps a <c>NULL</c> comparison from reaching PostgreSQL as an untyped parameter
    /// (<c>42P08 could not determine data type of parameter</c>).
    /// </summary>
    [Fact]
    public async Task A_null_bound_against_a_column_still_carries_the_columns_db_type()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        BindThroughColumn(context, "created_at", PolicyParameterPrefix.Filter + "0", null)
            .DbType.ShouldBe(DbType.DateTimeOffset);
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
        var bound = BindThroughColumn(context, "owner_id", PolicyParameterPrefix.Using + "0", ownerId.ToString("D", CultureInfo.InvariantCulture));

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
        var bound = BindThroughColumn(context, "due_on", PolicyParameterPrefix.Using + "0",
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

        Should.Throw<ArgumentException>(
            () => BindThroughColumn(context, "owner_id", PolicyParameterPrefix.Filter + "0", "not-a-uuid"));
    }

    /// <summary>
    /// A fractional value against an integral column is refused, not rounded. <c>Convert.ChangeType</c>
    /// rounds (midpoint-to-even), so <c>mileage=gt.12.7</c> would have bound <c>13</c> and answered
    /// <c>mileage &gt; 13</c> — excluding the row with <c>mileage = 13</c> from a request whose stated
    /// predicate included it, silently. <c>lte.12.7</c> is the mirror, admitting a row the caller excluded.
    /// The binder's own contract says a value the column cannot hold is refused rather than coerced; this is
    /// that contract being true.
    /// </summary>
    [Theory]
    [InlineData(12.7)]
    [InlineData(12.5)]
    [InlineData(-0.5)]
    public async Task A_fractional_value_against_an_integral_column_is_refused_rather_than_rounded(double fraction)
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();
        var binder = new PredicateParameterBinder(context);

        Should.Throw<ArgumentException>(() => BindThroughColumn(context, "mileage", PolicyParameterPrefix.Filter + "0", (decimal)fraction));
    }

    [Fact]
    public async Task A_whole_number_of_another_numeric_type_still_binds_against_an_integral_column()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        BindThroughColumn(context, "mileage", PolicyParameterPrefix.Filter + "0", 13m)
            .Value.ShouldBe(13L);
    }

    /// <summary>
    /// The refusal is about losing information, not about the type: a fractional value against a
    /// <c>decimal</c> column is exactly what that column holds.
    /// </summary>
    [Fact]
    public async Task A_fractional_value_against_a_decimal_column_is_untouched()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        BindThroughColumn(context, "price", PolicyParameterPrefix.Filter + "0", 12.7)
            .Value.ShouldBe(12.7m);
    }

    /// <summary>
    /// An offset-less timestamp binds the same instant whatever time zone the host runs in. Parsing without
    /// explicit styles reads it in the <em>process's local</em> zone, so two replicas of one service in two
    /// regions answer <c>created_at=gte.2026-07-26T00:00:00</c> over different row sets — and CI, which runs
    /// UTC, never sees it. The zone is set here rather than trusted, for exactly that reason.
    /// </summary>
    [Theory]
    [InlineData("UTC")]
    [InlineData("Pacific/Kiritimati")]
    [InlineData("Pacific/Niue")]
    public async Task An_offset_less_timestamp_binds_the_same_instant_in_every_host_time_zone(string timeZone)
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();
        using var zone = new LocalTimeZone(timeZone);

        BindThroughColumn(context, "created_at", PolicyParameterPrefix.Filter + "0", "2026-07-26T10:00:00")
            .Value.ShouldBe(new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// An input that <em>does</em> carry an offset is read at that offset and normalised to the instant it
    /// denotes — the caller said which instant they meant, and the offset is a spelling of it, not part of it.
    /// </summary>
    [Fact]
    public async Task A_timestamp_carrying_its_own_offset_binds_as_the_instant_it_denotes()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();
        using var zone = new LocalTimeZone("Pacific/Kiritimati");

        BindThroughColumn(context, "created_at", PolicyParameterPrefix.Filter + "0", "2026-07-26T10:00:00+02:00")
            .Value.ShouldBe(new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// The same rule for a value that arrives already typed as a <see cref="DateTimeOffset"/> — the route that
    /// went unnormalised, because such a value <em>is</em> an instance of the column's own CLR type and so
    /// short-circuited the conversion entirely.
    /// </summary>
    /// <remarks>
    /// What that cost: on SQLite the caller's <c>+02:00</c> text was compared lexically against stored
    /// <c>+00:00</c> text and matched rows the caller had excluded; on PostgreSQL Npgsql refused the parameter
    /// outright, throwing out of a <em>read</em>. One payload, two wrong answers.
    /// </remarks>
    /// <remarks>
    /// The <b>offset</b> is asserted alongside the instant, and that is not belt-and-braces:
    /// <see cref="DateTimeOffset"/> equality compares instants, so <c>10:00+02:00</c> and <c>08:00Z</c> are
    /// equal and an instant-only assertion would pass with no normalisation at all. What reaches SQLite is the
    /// rendered <em>text</em>, so the offset is the whole point.
    /// </remarks>
    [Fact]
    public async Task A_datetimeoffset_at_a_non_utc_offset_binds_as_the_instant_it_denotes()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        var bound = BindThroughColumn(context, "created_at", PolicyParameterPrefix.Filter + "0",
            new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.FromHours(2)));

        bound.Value.ShouldBe(new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero));
        ((DateTimeOffset)bound.Value!).Offset.ShouldBe(TimeSpan.Zero);
    }

    /// <summary>
    /// A <see cref="DateTime"/> the caller explicitly marked <see cref="DateTimeKind.Local"/> denotes the right
    /// instant but carries the host's offset, so unnormalised the bound value differed per region. The zone is
    /// forced rather than trusted, because CI runs UTC and would never show it.
    /// </summary>
    /// <param name="timeZone">The host zone to answer the binding in.</param>
    [Theory]
    [InlineData("UTC")]
    [InlineData("Pacific/Kiritimati")]
    [InlineData("Pacific/Niue")]
    public async Task A_local_datetime_binds_as_the_same_utc_instant_in_every_host_time_zone(string timeZone)
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();
        using var zone = new LocalTimeZone(timeZone);
        var local = new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero).ToLocalTime().LocalDateTime;

        var bound = BindThroughColumn(context, "created_at", PolicyParameterPrefix.Filter + "0", local);

        bound.Value.ShouldBe(new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero));
        ((DateTimeOffset)bound.Value!).Offset.ShouldBe(TimeSpan.Zero);
    }

    /// <summary>
    /// The same rule for a <see cref="DateTime"/> that arrived with no <see cref="DateTimeKind"/> — which is
    /// what <c>System.Text.Json</c> produces for an offset-less JSON timestamp.
    /// </summary>
    [Fact]
    public async Task A_kindless_datetime_binds_as_utc_rather_than_as_the_hosts_local_time()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();
        using var zone = new LocalTimeZone("Pacific/Kiritimati");

        BindThroughColumn(context, "created_at", PolicyParameterPrefix.Filter + "0",
                new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Unspecified))
            .Value.ShouldBe(new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_null_binds_against_a_column_as_the_ado_net_null_sentinel()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        BindThroughColumn(context, "owner_id", PolicyParameterPrefix.Filter + "0", null)
            .Value.ShouldBe(DBNull.Value);
    }

    [Fact]
    public async Task A_policy_predicate_bind_still_carries_the_null_sentinel()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        BindWithoutColumn(context, PolicyParameterPrefix.Filter + "0", null)
            .Value.ShouldBe(DBNull.Value);
    }

    [Fact]
    public async Task A_bound_parameters_name_carries_the_providers_marker()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        BindThroughColumn(context, "id", PolicyParameterPrefix.RowId, Guid.NewGuid())
            .ParameterName.ShouldBe("@" + PolicyParameterPrefix.RowId);
    }

    /// <summary>
    /// A statement's values are bound in one call, dispatched on where each came from, and the names survive
    /// unchanged — the whole point of disjoint prefixes is that several fragments can be merged without one
    /// value overwriting another. Two fragments claiming one name is refused where they are <em>merged</em>
    /// (<c>ReadStatementComposer.Collect</c>), not here: a statement carries one dictionary, so by the time it
    /// reaches this class a duplicate is already unrepresentable.
    /// </summary>
    [Fact]
    public async Task A_statements_values_bind_in_one_call_without_losing_a_name()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();

        var bound = new PredicateParameterBinder(context).Bind(
            context.Model.FindEntityType("vehicle")!,
            new Dictionary<string, BoundValue>(StringComparer.Ordinal)
            {
                ["alvo_u0"] = BoundValue.FromPolicyPredicate(Guid.NewGuid()),
                ["alvo_f0"] = BoundValue.ForColumn("plate", "ACME-001"),
                ["alvo_limit"] = BoundValue.FromFramework(5),
            });

        bound.Select(parameter => parameter.ParameterName)
            .ShouldBe(["@alvo_u0", "@alvo_f0", "@alvo_limit"], ignoreOrder: true);
    }

    /// <summary>
    /// A value naming a field this read model does not map has no column to bind through, so it is refused
    /// rather than falling back to the value's own type — the fallback that would silently reintroduce the
    /// defect the origin-tagged shape exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_column_this_read_model_does_not_map_is_refused_rather_than_bound_by_value_type()
    {
        var factory = await FactoryAsync();
        using var context = factory.Create();
        var binder = new PredicateParameterBinder(context);

        Should.Throw<InvalidOperationException>(() => binder.Bind(
            context.Model.FindEntityType("vehicle")!,
            new Dictionary<string, BoundValue>(StringComparer.Ordinal)
            {
                ["alvo_f0"] = BoundValue.ForColumn("no_such_field", "x"),
            }));
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

        Should.Throw<InvalidOperationException>(
            () => BindWithoutColumn(context, PolicyParameterPrefix.Filter + "0", new object()));
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

    /// <summary>
    /// Binds one value against a column through the <b>production</b> entry point.
    /// </summary>
    /// <remarks>
    /// It goes through <c>Bind(IEntityType, …)</c> rather than a narrower member of its own, because a
    /// narrower member is what this class used to drive: <c>BindColumnValue</c> and
    /// <c>BindPolicyPredicate</c> had <b>zero</b> production call sites while 15 facts here exercised them —
    /// the fourth appearance of the defect <c>PredicateParameterBinder</c>'s own remarks describe as having
    /// already happened once. The members are gone; these two helpers say what origin the value has, which is
    /// the only thing they ever varied.
    /// </remarks>
    private static DbParameter BindThroughColumn(
        AlvoDataContext context, string field, string name, object? value) =>
        Bound(context, name, BoundValue.ForColumn(field, value));

    /// <summary>Binds one value with no column behind it — a rendered policy predicate's bag.</summary>
    private static DbParameter BindWithoutColumn(AlvoDataContext context, string name, object? value) =>
        Bound(context, name, BoundValue.FromPolicyPredicate(value));

    private static DbParameter Bound(AlvoDataContext context, string name, BoundValue bound) =>
        new PredicateParameterBinder(context).Bind(
            Rows(context), new Dictionary<string, BoundValue>(StringComparer.Ordinal) { [name] = bound })[0];

    private static IEntityType Rows(AlvoDataContext context) => context.Model.FindEntityType("vehicle")!;

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
