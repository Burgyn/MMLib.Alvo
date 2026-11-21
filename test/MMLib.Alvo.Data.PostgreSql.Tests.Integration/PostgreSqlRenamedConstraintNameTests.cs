using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Tests.Ef;
using Npgsql;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// A declared rename's index and primary-key names on PostgreSQL (#523), where an index or a key constraint is a relation
/// of the schema — so a name left behind by a rename collides with the next object that takes it.
/// </summary>
/// <remarks>One container for the class and a database of its own per fact, for the reason the tenant-guard leg gives.</remarks>
public sealed class PostgreSqlRenamedConstraintNameTests(PostgresFixture fixture)
    : RenamedConstraintNameFacts, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    protected override IServiceProvider Services => _services;

    protected override bool Runnable => !OperatingSystem.IsWindows();

    public async ValueTask InitializeAsync()
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (!Runnable)
        {
            return;
        }

        var name = $"alvo_rename_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        await _services.DisposeAsync();
        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UsePostgreSql(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ToString());
        builder.Services.AddAlvo();
        _services = builder.Services.BuildServiceProvider();
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
