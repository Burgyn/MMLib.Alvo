using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// SQLite's leg of the value-ordering suite — the engine that has neither a decimal storage class nor an
/// instant-ordered timestamp one, so every fact here is a real question rather than a formality.
/// </summary>
public sealed class SqliteAlvoDataOrderingTests : AlvoDataOrderingTests, IAsyncDisposable
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

    protected override string CursorFor(AlvoRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return KeysetCursor.Encode((Guid)row["id"]!);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
