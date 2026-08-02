using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// SQLite's leg of the port-level concurrency suite. It is the engine on which two writers actually
/// contend for one file, so it is where a lost update, a duplicated row, or a raw provider exception
/// escaping the port's failure contract would show up first.
/// </summary>
public sealed class SqliteAlvoDataConcurrencyTests : AlvoDataConcurrencyTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

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
