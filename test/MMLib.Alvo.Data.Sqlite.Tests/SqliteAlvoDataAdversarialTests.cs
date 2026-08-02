using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The milestone's central security suite, run over a real SQLite database — the second implementation held
/// to it, after PR1's in-memory reference. Every fact is inherited unchanged: this class supplies a store and
/// nothing else, so a fact cannot be weakened to make a provider pass.
/// </summary>
public sealed class SqliteAlvoDataAdversarialTests : AlvoDataAdversarialTests, IAsyncDisposable
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
