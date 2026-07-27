using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using Npgsql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's half of the caller-filter binding contract: a value this engine cannot represent is refused
/// by <b>Alvo</b>, on its own channel, rather than escaping as a raw provider exception.
/// </summary>
/// <remarks>
/// The SQLite twin of these facts records the other side of the same divergence — SQLite answered where this
/// engine threw. Two small classes rather than one inherited suite because the in-memory reference
/// implementation genuinely <em>can</em> hold the values in question, so this is a relational-storage rule and
/// not a port-wide one.
/// </remarks>
public sealed class PostgreSqlAlvoDataFilterBindingTests : IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    /// <summary>
    /// Before the guard, this reached the server and came back as
    /// <c>Npgsql.PostgresException 22021: invalid byte sequence for encoding "UTF8": 0x00</c> — thrown out of
    /// <see cref="IAlvoData.QueryAsync"/>, which declares no such exception.
    /// </summary>
    /// <param name="op">The operator the value is reached through.</param>
    [Theory]
    [InlineData(AlvoFilterOperator.Eq)]
    [InlineData(AlvoFilterOperator.Like)]
    public async Task A_text_filter_value_containing_a_nul_is_refused_before_it_reaches_the_server(
        AlvoFilterOperator op)
    {
        var data = await DataAsync();

        var refused = await Should.ThrowAsync<InvalidOperationException>(
            () => Query(data, new AlvoComparison("plate", op, "ACME\0001")));

        refused.ShouldNotBeOfType<PostgresException>();
        refused.Message.ShouldContain("NUL");
    }

    /// <summary>The same value reached through an <c>in</c> list, which binds each element separately.</summary>
    [Fact]
    public async Task A_nul_inside_an_in_list_is_refused_too()
    {
        var data = await DataAsync();

        await Should.ThrowAsync<InvalidOperationException>(
            () => Query(data, new AlvoComparison("plate", AlvoFilterOperator.In, new object?[] { "A", "B\0C" })));
    }

    /// <summary>
    /// The counterweight: a value this engine <em>can</em> hold still answers, so the guard above cannot be
    /// passing because every text filter is refused.
    /// </summary>
    [Fact]
    public async Task A_text_filter_value_without_a_nul_still_answers()
    {
        var data = await DataAsync();

        var rows = await Query(data, new AlvoComparison("plate", AlvoFilterOperator.Eq, "ACME-001"));

        rows.ShouldBeEmpty();
    }

    private async Task<IAlvoData> DataAsync() =>
        (await _fixture.StartAsync(new SchemaModel([AlvoDataFixtures.Vehicle]))).Data;

    private static Task<IReadOnlyList<AlvoRecord>> Query(IAlvoData data, AlvoFilter filter) => data.QueryAsync(
        new AlvoQuery { Entity = AlvoDataFixtures.Vehicle.Name, Filter = filter },
        AlvoDataFixtures.Caller,
        TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
