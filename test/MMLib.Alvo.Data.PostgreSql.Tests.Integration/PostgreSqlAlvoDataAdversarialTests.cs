using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The milestone's central security suite over a real PostgreSQL engine — the criterion "green on SQLite
/// <em>and</em> PostgreSQL", proved rather than assumed. Inherits every fact unchanged.
/// </summary>
public sealed class PostgreSqlAlvoDataAdversarialTests : AlvoDataAdversarialTests, IAsyncLifetime
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

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
