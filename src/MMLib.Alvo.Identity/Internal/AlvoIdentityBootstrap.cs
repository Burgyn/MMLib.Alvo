using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data.Common;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// Brings the identity tables up and seeds the configured bootstrap administrator — both idempotent,
/// because a container restarts on every deploy.
/// </summary>
/// <remarks>
/// <b>In <see cref="IHostedLifecycleService.StartingAsync"/>, not <c>StartAsync</c>.</b> A web host's server is a
/// hosted service too, and a request it accepts first reads the users table for its cookie check. Hosted services
/// start in registration order only while <c>HostOptions.ServicesStartConcurrently</c> is false, and that option
/// binds from configuration; every service's <c>StartingAsync</c> finishes before any <c>StartAsync</c> begins,
/// either way. Alvo's own boot runs there for the same reason.
/// </remarks>
/// <param name="scopes">Creates the scope the scoped store and managers are resolved from.</param>
/// <param name="options">The configured bootstrap administrator, if there is one.</param>
/// <param name="admin">The port the seeded administrator is published through.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class AlvoIdentityBootstrap(
    IServiceScopeFactory scopes,
    IOptions<AlvoIdentityOptions> options,
    AlvoBootstrapAdmin admin,
    ILogger<AlvoIdentityBootstrap> logger) : IHostedLifecycleService
{
    /// <inheritdoc/>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        await EnsureTablesAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        await SeedAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Creates the identity tables when they are absent.</summary>
    /// <remarks>
    /// <para>
    /// <b><c>EnsureCreated</c> is not usable and migrations are not either.</b> <c>EnsureCreated</c>
    /// refuses a database that already has tables, and Alvo's own are already there; EF migrations are
    /// generated per provider, so shipping them would mean one migration assembly per engine and would
    /// put the provider choice inside this package. So the probe is a query of the users table that reads no row, and
    /// the creation is EF's own per-provider DDL, which is exactly the pair those two mechanisms would have wrapped.
    /// </para>
    /// <para>
    /// <b>A probe can fail for more than a missing table</b>: a permission refused, a lock, a view where the table
    /// should be. The creation then fails too, with "already exists" or its engine's words for it, which says nothing
    /// about why. So a creation that fails after a failed probe fails the start with the probe's own error, the one
    /// that says what is wrong, and the creation's as the detail (batch-B re-review N5). The probe's failure is logged
    /// at Debug either way: on a first boot it is the expected "no such table".
    /// </para>
    /// </remarks>
    /// <param name="scope">The scope the store is resolved from.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>A task that completes when the tables are present.</returns>
    private async Task EnsureTablesAsync(IServiceProvider scope, CancellationToken cancellationToken)
    {
        var store = scope.GetRequiredService<AlvoIdentityDbContext>();
        if (await ProbeAsync(store, cancellationToken).ConfigureAwait(false) is not { } probed)
        {
            await ReconcileColumnsAsync(store, cancellationToken).ConfigureAwait(false);
            return;
        }

        ProbeFoundNoTables(logger, probed.Message);
        await CreateTablesAsync(store, probed, cancellationToken).ConfigureAwait(false);
        CreatedIdentityTables(logger);
    }

    /// <summary>Creates the tables, or fails the start with the probe's error when that fails too.</summary>
    /// <param name="store">The identity store.</param>
    /// <param name="probed">Why the probe found no tables.</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <returns>A task that completes when the tables are created.</returns>
    /// <exception cref="InvalidOperationException">Neither the read nor the creation worked.</exception>
    private static async Task CreateTablesAsync(
        AlvoIdentityDbContext store, DbException probed, CancellationToken cancellationToken)
    {
        try
        {
            await store.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException created)
        {
            throw new InvalidOperationException(
                $"Alvo's identity tables could not be read, and could not be created. Reading them said: {probed.Message} "
                + $"(creating them said: {created.Message})", probed);
        }
    }

    /// <summary>Adds columns an older build's database does not have yet.</summary>
    /// <remarks>
    /// <b>Only on the branch where the tables already exist, and that is the whole point.</b>
    /// Tables this start created are current by construction; tables an earlier build created are
    /// current only until the model gains a column, and then every read of that table fails with a
    /// missing-column error. <c>{prefix}_identity_users.TenantId</c> was the first such column —
    /// <see cref="AlvoIdentitySchema"/> carries the reasoning and the limits.
    /// </remarks>
    /// <param name="store">The identity store.</param>
    /// <param name="cancellationToken">Cancels the reconciliation.</param>
    /// <returns>A task that completes when the model's columns are all present.</returns>
    private async Task ReconcileColumnsAsync(
        AlvoIdentityDbContext store, CancellationToken cancellationToken)
    {
        var added = await AlvoIdentitySchema
            .EnsureColumnsAsync(store, cancellationToken)
            .ConfigureAwait(false);

        if (added.Count > 0)
        {
            AddedIdentityColumns(logger, added);
        }
    }

    /// <summary>Probes for the identity tables with a query that reads no row.</summary>
    /// <remarks>
    /// <b>Sent on the store's own connection, not through a query of the model.</b> EF logs a command that fails at
    /// error level before any catch sees it, so the model's <c>Users.AnyAsync()</c> printed "no such table" as a
    /// <c>fail:</c> line on every first boot, the line an operator greps for when something is broken. The table's
    /// name is the model's, delimited by the provider's own helper, and <c>SELECT 1 FROM t WHERE 1 = 0</c> is read the
    /// same way by every engine Alvo ships.
    /// </remarks>
    /// <param name="store">The identity store.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns><see langword="null"/> when the tables are there; the probe's failure when they are not, or cannot be read.</returns>
    private static async Task<DbException?> ProbeAsync(AlvoIdentityDbContext store, CancellationToken cancellationToken)
    {
        await store.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var probe = store.Database.GetDbConnection().CreateCommand();
            probe.CommandText = $"SELECT 1 FROM {UsersTable(store)} WHERE 1 = 0";
            await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DbException failure)
        {
            return failure;
        }
        finally
        {
            await store.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The users table, as the provider writes its name into SQL.</summary>
    /// <param name="store">The identity store.</param>
    /// <returns>The delimited, schema-qualified name.</returns>
    private static string UsersTable(AlvoIdentityDbContext store)
    {
        var users = store.Model.FindEntityType(typeof(AlvoIdentityUser))
            ?? throw new InvalidOperationException("The identity model has no users table.");

        return store.GetService<ISqlGenerationHelper>()
            .DelimitIdentifier(users.GetTableName()!, users.GetSchema());
    }

    /// <summary>Creates the configured administrator, or finds the one an earlier start created.</summary>
    /// <remarks>
    /// <b>An existing account is published, never rewritten.</b> Re-seeding the password from the file
    /// would make rotating a mounted secret a silent reset of a live administrator's credential — and
    /// would undo a rotation the administrator performed in the dashboard on the next restart.
    /// </remarks>
    /// <param name="scope">The scope the managers are resolved from.</param>
    /// <param name="cancellationToken">Cancels the seeding.</param>
    /// <returns>A task that completes when the administrator is published, if there is one.</returns>
    private async Task SeedAsync(IServiceProvider scope, CancellationToken cancellationToken)
    {
        if (options.Value.BootstrapEmail is not { Length: > 0 } email)
        {
            return;
        }

        var users = scope.GetRequiredService<UserManager<AlvoIdentityUser>>();
        if (await users.FindByEmailAsync(email).ConfigureAwait(false) is { } existing)
        {
            admin.Publish(new UserId(existing.Id));
            return;
        }

        admin.Publish(await CreateAsync(scope, users, email, cancellationToken).ConfigureAwait(false));
        SeededBootstrapAdmin(logger, email);
    }

    /// <summary>Creates the administrator account and grants it the built-in administrative role.</summary>
    /// <param name="scope">The scope the role manager is resolved from.</param>
    /// <param name="users">ASP.NET Core Identity's user manager.</param>
    /// <param name="email">The configured address.</param>
    /// <param name="cancellationToken">Cancels the read of the secret file.</param>
    /// <returns>The created administrator's identifier.</returns>
    private async Task<UserId> CreateAsync(
        IServiceProvider scope, UserManager<AlvoIdentityUser> users, string email, CancellationToken cancellationToken)
    {
        var password = await ReadPasswordAsync(cancellationToken).ConfigureAwait(false);
        var created = new AlvoIdentityUser { Id = Guid.NewGuid(), UserName = email, Email = email };

        Require(await users.CreateAsync(created, password).ConfigureAwait(false), email);
        await EnsureAdminRoleAsync(scope).ConfigureAwait(false);
        Require(await users.AddToRoleAsync(created, Role.Admin.Name).ConfigureAwait(false), email);

        return new UserId(created.Id);
    }

    /// <summary>Creates the built-in administrative role row when it is not there yet.</summary>
    /// <param name="scope">The scope the role manager is resolved from.</param>
    /// <returns>A task that completes when the role exists.</returns>
    private static async Task EnsureAdminRoleAsync(IServiceProvider scope)
    {
        var roles = scope.GetRequiredService<RoleManager<AlvoIdentityRole>>();
        if (!await roles.RoleExistsAsync(Role.Admin.Name).ConfigureAwait(false))
        {
            await roles.CreateAsync(new AlvoIdentityRole { Id = Guid.NewGuid(), Name = Role.Admin.Name })
                .ConfigureAwait(false);
        }
    }

    /// <summary>Reads the administrator's password from the mounted secret file.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The password, without the trailing newline a secret file usually carries.</returns>
    /// <exception cref="InvalidOperationException">No secret file is configured.</exception>
    private async Task<string> ReadPasswordAsync(CancellationToken cancellationToken)
    {
        var path = options.Value.BootstrapPasswordFile
            ?? throw new InvalidOperationException(
                $"'{AlvoIdentity.ConfigurationSection}:BootstrapEmail' is configured without "
                + $"'{AlvoIdentity.ConfigurationSection}:BootstrapPasswordFile'.");

        return (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
    }

    /// <summary>Turns a refused seeding into a failed start rather than a host with no administrator.</summary>
    /// <param name="result">What Identity said.</param>
    /// <param name="email">The configured address, for the message.</param>
    /// <exception cref="InvalidOperationException">The seeding was refused.</exception>
    private static void Require(IdentityResult result, string email)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"The bootstrap administrator '{email}' could not be created: "
                + string.Join("; ", result.Errors.Select(error => error.Description)));
        }
    }

    /// <summary>Logs why the probe found no identity tables: on a first boot, the expected missing table.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="probeFailure">What the probe's read said.</param>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Alvo's identity tables were not found: {ProbeFailure}")]
    private static partial void ProbeFoundNoTables(ILogger logger, string probeFailure);

    /// <summary>Logs that the identity tables were created.</summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Created Alvo's identity tables.")]
    private static partial void CreatedIdentityTables(ILogger logger);

    /// <summary>Logs the columns an older build's identity database was missing.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="addedColumns">The columns that were added; they render themselves.</param>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Added missing Alvo identity columns: {AddedColumns}.")]
    private static partial void AddedIdentityColumns(
        ILogger logger, AlvoIdentitySchema.AddedColumns addedColumns);

    /// <summary>Logs that the bootstrap administrator was seeded.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="bootstrapEmail">The seeded address.</param>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Seeded the bootstrap administrator {BootstrapEmail}.")]
    private static partial void SeededBootstrapAdmin(ILogger logger, string bootstrapEmail);
}
