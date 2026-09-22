using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// Bringing an identity database created by an older build up to the current model.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one path in the package that issues DDL against a database that predates the
/// deploy.</b> <c>AlvoIdentityBootstrap</c> creates the tables when they are absent and does
/// nothing when they are present, which is right exactly once — so every column the model gains
/// after the first release reaches an existing database through here, or not at all.
/// </para>
/// <para>
/// The facts are written against a <b>real SQLite file</b> rather than an in-memory model, because
/// the thing under test is what the database answers: a column probe that resolved against the EF
/// model instead of the connection would pass while the column was missing, which is precisely the
/// failure this code exists to prevent.
/// </para>
/// </remarks>
public class AlvoIdentitySchemaTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"alvo-identity-schema-{Guid.CreateVersion7():N}.db");

    /// <inheritdoc/>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_column_an_older_build_never_created_is_added()
    {
        await CreateTablesWithoutAsync("TenantId");

        using var provider = Container();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AlvoIdentityDbContext>();

        var added = await AlvoIdentitySchema.EnsureColumnsAsync(store, TestContext.Current.CancellationToken);

        added.ShouldContain(
            entry => entry.EndsWith(".TenantId", StringComparison.Ordinal),
            "TenantId on the identity users table is the first column the model gained after the tables "
            + "existed, and without this every database created by the previous build fails on the "
            + "first read rather than at a place anybody can act on");

        (await store.Users.AnyAsync(TestContext.Current.CancellationToken)).ShouldBeFalse(
            "and the table is readable afterwards, which is the whole point");
    }

    [Fact]
    public async Task A_database_that_is_already_current_is_left_alone()
    {
        using var provider = Container();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AlvoIdentityDbContext>();
        await store.GetService<IRelationalDatabaseCreator>()
            .CreateTablesAsync(TestContext.Current.CancellationToken);

        var added = await AlvoIdentitySchema.EnsureColumnsAsync(store, TestContext.Current.CancellationToken);

        added.ShouldBeEmpty(
            "this runs on every start, so a start that changes nothing must write nothing");
    }

    [Fact]
    public async Task A_missing_table_is_named_as_a_table_rather_than_as_its_columns()
    {
        await CreateTablesWithoutAsync(column: null, dropTable: RolesTable);

        using var provider = Container();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AlvoIdentityDbContext>();

        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => AlvoIdentitySchema.EnsureColumnsAsync(store, TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain(
            RolesTable,
            customMessage: "the bootstrap probes only the users table, so a half-created schema reaches "
                + "here — and diagnosing it as every column of that table being missing sends an "
                + "operator to look at a column when the problem is a table");
    }

    /// <summary>Creates the identity tables the way an older build would have.</summary>
    /// <remarks>
    /// EF's own DDL generator, with one column or one whole table taken out of the model's own
    /// script — which is a closer model of "a database an earlier build created" than hand-written
    /// SQL, and stays correct when the model changes again.
    /// </remarks>
    /// <param name="column">A column to leave out of every table that has it.</param>
    /// <param name="dropTable">A table to leave out entirely.</param>
    /// <returns>A task that completes when the database is there.</returns>
    private async Task CreateTablesWithoutAsync(string? column, string? dropTable = null)
    {
        using var provider = Container();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AlvoIdentityDbContext>();

        var script = store.Database.GenerateCreateScript();
        var statements = script
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(statement => statement.Length > 0)
            .Where(statement => dropTable is null || !Targets(statement, dropTable))
            .Select(statement => column is null ? statement : WithoutColumn(statement, column));

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Whether a statement creates the named table or an index over it.</summary>
    /// <remarks>
    /// Deliberately not a bare name match: a foreign key clause names its target, so dropping every
    /// statement that <em>mentions</em> the table would also drop the tables that reference it —
    /// and the fact would then measure a database missing four tables rather than one.
    /// </remarks>
    /// <param name="statement">One statement from the create script.</param>
    /// <param name="table">The table to leave out.</param>
    /// <returns><see langword="true"/> when the statement builds that table or an index on it.</returns>
    private static bool Targets(string statement, string table)
        => statement.StartsWith($"CREATE TABLE \"{table}\"", StringComparison.Ordinal)
            || statement.Contains($"ON \"{table}\"", StringComparison.Ordinal);

    /// <summary>Removes one column definition from a <c>CREATE TABLE</c> statement.</summary>
    /// <param name="statement">The statement.</param>
    /// <param name="column">The column to leave out.</param>
    /// <returns>The statement without that column.</returns>
    private static string WithoutColumn(string statement, string column)
    {
        var lines = statement
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith($"\"{column}\"", StringComparison.Ordinal));

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The identity roles table, as the model actually names it.
    /// </summary>
    /// <remarks>
    /// Not <c>AspNetRoles</c>: every identity table is named under <c>AlvoOptions.SchemaPrefix</c>,
    /// because one prefix names every table the framework owns and a table outside it lands in what
    /// Alvo reads as the <em>user's</em> schema. Spelled from the default prefix rather than from
    /// the constant, so a rename of either has to be looked at here.
    /// </remarks>
    private const string RolesTable = "alvo_identity_roles";

    private string ConnectionString => $"Data Source={_path}";

    private ServiceProvider Container()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvoIdentity(store => store.UseSqlite(ConnectionString));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
