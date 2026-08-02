using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The one thing an <see cref="AlvoApiWorld"/> does not know about itself: which database engine it runs
/// on. An engine provisions a fresh, empty database per world, registers the provider the way a host
/// would, and hands out a connection for the out-of-band reads a fact needs.
/// </summary>
/// <remarks>
/// <para>
/// It exists because #19's DoD is "tests green on SQLite + Postgres" and the API-level suite ran on SQLite
/// alone, while the port-level suites already had both legs. The shape mirrors those:
/// <c>MMLib.Alvo.Testing.Data.AlvoDataPagingTests</c> and friends declare the facts once and take the store
/// through an abstract seam, and each engine's test project supplies the seam. Here the seam is the
/// database rather than the port, because everything above it — routing, the query parse, the ETag, the
/// idempotency ledger — has to be the production code path on both engines or the fact proves nothing about
/// the second one.
/// </para>
/// <para>
/// <see langword="public"/> rather than <see langword="internal"/> only because
/// <see cref="DataApiEngineTests.Engine"/> is a <see langword="protected"/> member of a public class in
/// another assembly. Nothing here is shipped: this file is linked into each API test project, exactly as
/// <c>test/_shared/ef/SqlCapture.cs</c> is linked into each engine test project, and for the same reason —
/// a second copy is how two suites come to disagree about what they are testing.
/// </para>
/// </remarks>
public abstract class AlvoApiEngine
{
    /// <summary>
    /// Provisions a fresh, empty database for one world, or skips the calling test when the engine cannot
    /// be reached at all.
    /// </summary>
    /// <remarks>
    /// The skip belongs here rather than in each fact: a Testcontainers-backed engine is unavailable on a
    /// host with no Docker daemon, and a fact that has already been handed a world has no way to tell that
    /// from a defect. Every fact reaches the engine through this method, so one <c>Assert.Skip</c> covers
    /// the whole suite.
    /// </remarks>
    public abstract Task<AlvoApiDatabase> CreateDatabaseAsync();
}

/// <summary>
/// One empty database, live for the lifetime of the world that asked for it.
/// </summary>
/// <remarks>
/// A database rather than a connection, because both engines need something kept alive around the world —
/// SQLite a keep-alive connection holding the shared cache, PostgreSQL the created database itself — and
/// both need to hand out <em>fresh</em> connections, since Alvo's relational driver opens one per unit of
/// work by design.
/// </remarks>
public abstract class AlvoApiDatabase : IAsyncDisposable
{
    /// <summary>
    /// A substring unique to this database's connection string — <c>SqlCapture</c>'s marker, so a world
    /// reports the statements of its own database and worlds stay safe to run in parallel.
    /// </summary>
    public abstract string Marker { get; }

    /// <summary>Registers this database as Alvo's provider, through the same extension a host calls.</summary>
    /// <param name="builder">The Alvo builder being configured.</param>
    public abstract void Use(IAlvoBuilder builder);

    /// <summary>
    /// A new, unopened connection to this database, for the reads and bulk seeds that must go
    /// <em>around</em> the API — "no row was written" cannot be asked of an endpoint whose policy already
    /// hides rows.
    /// </summary>
    public abstract DbConnection Connect();

    /// <inheritdoc/>
    public abstract ValueTask DisposeAsync();
}

/// <summary>
/// SQLite, in memory and shared-cache — the engine every fast API fact runs on, because it needs no
/// container and therefore keeps ring0 Docker-free.
/// </summary>
public sealed class SqliteApiEngine : AlvoApiEngine
{
    /// <summary>The one instance; it holds no state, since every database it makes owns its own.</summary>
    public static SqliteApiEngine Instance { get; } = new();

    /// <inheritdoc/>
    public override async Task<AlvoApiDatabase> CreateDatabaseAsync()
    {
        var database = new SqliteApiDatabase($"alvo-api-{Guid.NewGuid():N}");
        await database.OpenAsync();
        return database;
    }
}

/// <summary>
/// One uniquely named <c>Mode=Memory;Cache=Shared</c> database, kept alive by one open connection.
/// </summary>
/// <remarks>
/// A bare <c>Data Source=:memory:</c> gives every connection its own private, empty database, and Alvo's
/// relational driver opens a fresh connection per unit of work (<c>RelationalConnectionFactory</c>) — so the
/// migration would create tables one connection could see and no request ever could. The keep-alive
/// connection is what makes a named shared cache behave like one database while still needing no file.
/// </remarks>
public sealed class SqliteApiDatabase(string name) : AlvoApiDatabase
{
    private readonly SqliteConnection _keepAlive = new($"Data Source={name};Mode=Memory;Cache=Shared");

    /// <inheritdoc/>
    /// <remarks>The generated database name, which appears in every connection string to it.</remarks>
    public override string Marker { get; } = name;

    /// <summary>Opens the keep-alive connection, which is what brings the shared cache into existence.</summary>
    internal Task OpenAsync() => _keepAlive.OpenAsync(TestContext.Current.CancellationToken);

    /// <inheritdoc/>
    public override void Use(IAlvoBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSqlite(_keepAlive.ConnectionString);
    }

    /// <inheritdoc/>
    public override DbConnection Connect() => new SqliteConnection(_keepAlive.ConnectionString);

    /// <inheritdoc/>
    public override ValueTask DisposeAsync() => _keepAlive.DisposeAsync();
}
