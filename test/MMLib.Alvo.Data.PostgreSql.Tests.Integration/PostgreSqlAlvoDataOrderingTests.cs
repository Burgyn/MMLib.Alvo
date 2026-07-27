using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of the value-ordering suite: <c>numeric</c> and <c>timestamptz</c> both order the way
/// their type does, so these facts are the reference the SQLite leg has to reproduce.
/// </summary>
public sealed class PostgreSqlAlvoDataOrderingTests : AlvoDataOrderingTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override async Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed)
    {
        var host = await _fixture.StartAsync(schema, descriptor);
        await AlvoDataSeed.SeedAsync(
            host.Services.GetRequiredService<AlvoDataContextFactory>(), seed, TestContext.Current.CancellationToken);
        return host.Data;
    }

    protected override string CursorFor(AlvoRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return KeysetCursor.Encode((Guid)row["id"]!);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
