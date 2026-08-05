using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of the computed/rollup suite — the engine that adds a stored generated column in place and
/// backfills, and the one that requires the parent's row lock before a rollup recompute.
/// </summary>
/// <remarks>
/// The race itself is <em>not</em> inherited from the shared suite: it lives in
/// <c>PostgreSqlRollupRaceTests</c>, on this engine only, because SQLite cannot fail it.
/// </remarks>
public sealed class PostgreSqlAlvoDataComputedRollupTests : AlvoDataComputedRollupTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();
    private PostgreSqlAlvoDataHost? _host;

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor)
    {
        _host = await _fixture.StartAsync(schema, descriptor);
        return _host.Data;
    }

    /// <inheritdoc/>
    protected override Task<Exception?> ExecuteOutOfBandAsync(string sql) =>
        OutOfBandStatement.ExecuteAsync(
            _host!.Services.GetRequiredService<AlvoDataContextFactory>(), sql, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
