using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Scheduling;

using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Every run a data migration fact's migration saw, in the order they ran.</summary>
public sealed class MigrationLog
{
    public ConcurrentQueue<AlvoJobContext> Runs { get; } = new();
}

/// <summary>Records each run.</summary>
public sealed class RecordingMigration(MigrationLog log) : IAlvoDataMigration
{
    public Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken)
    {
        log.Runs.Enqueue(context);
        return Task.CompletedTask;
    }
}

/// <summary>Records the schedule pump's <c>TenantCatalogMissing</c> lines.</summary>
internal sealed class MigrationLogCapture : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _missing = new();

    internal IReadOnlyList<string> TenantCatalogMissing => [.. _missing];

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (eventId.Name == "TenantCatalogMissing")
        {
            _missing.Enqueue(formatter(state, exception));
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Data migrations over the real pipeline: one run per migration, at the first applied revision at or after the one it waits
/// for, across restarts, rollbacks and re-applies — one per tenant on a scoped schema.
/// </summary>
public abstract partial class TimeFacts
{
    private const string Backfill = "backfill-notes";
    private const string MigrationDesk = "migration-desk";
    private const string DeskFile = "migration-desk.alvo.json";
    private const string DeskWithNotes = "migration-desk-notes.alvo.json";
    private const string DeskWithBookings = "migration-desk-scoped.alvo.json";

    [Fact]
    public async Task A_data_migration_runs_once_per_revision_across_restarts_rollback_and_reapply()
    {
        var database = await DatabaseAsync();
        var log = new MigrationLog();
        await using (var first = await StartMigratingAsync(database, DeskFile, log, afterRevision: 2))
        {
            await first.DrainEventsAsync(Ct);
            log.Runs.ShouldBeEmpty();
            await ApplyAsync(first, DeskWithNotes);
            await first.DrainEventsAsync(Ct);
        }

        await using var second = await StartMigratingAsync(database, DeskWithNotes, log, afterRevision: 2);
        await second.DrainEventsAsync(Ct);
        await RollbackAsync(second, toRevision: 1);
        await second.DrainEventsAsync(Ct);
        await ApplyAsync(second, DeskWithNotes);
        await second.DrainEventsAsync(Ct);

        log.Runs.Select(run => run.Revision).ShouldBe([2]);
        (await Deliveries(second, Backfill)).ShouldHaveSingleItem().Kind.ShouldBe("migration");
    }

    [Fact]
    public async Task A_scoped_schema_runs_the_migration_once_per_tenant()
    {
        var log = new MigrationLog();
        await using var alvo = await StartMigratingAsync(
            await DatabaseAsync(), BookingDesk, log, afterRevision: 1, new FixedTenants(TenantA, TenantB));

        await alvo.DrainEventsAsync(Ct);

        log.Runs.Select(run => run.Tenant).ShouldBe([TenantA, TenantB], ignoreOrder: true);
        log.Runs.ShouldAllBe(run => run.Revision == 1 && run.Id == Backfill);
    }

    [Fact]
    public async Task A_migration_on_a_scoped_schema_without_a_tenant_catalog_refuses_boot()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(
            () => StartMigratingAsync(null, BookingDesk, new MigrationLog(), afterRevision: 1));

        refusal.Message.ShouldContain($"extensions/{Backfill}/tenants");
        refusal.FixSuggestion.ShouldContain("IAlvoTenantCatalog");
    }

    [Fact]
    public async Task A_schema_scoped_by_a_later_apply_holds_the_migration_until_a_catalog_exists()
    {
        var database = await DatabaseAsync();
        var log = new MigrationLog();
        var logs = new MigrationLogCapture();
        await using (var first = await StartMigratingAsync(database, DeskFile, log, afterRevision: 2, logs: logs))
        {
            await ApplyAsync(first, DeskWithBookings);
            await first.DrainEventsAsync(Ct);

            log.Runs.ShouldBeEmpty();
            (await TimerStore(first).StartedMigrationsAsync(Ct)).ShouldBeEmpty();
            logs.TenantCatalogMissing.ShouldHaveSingleItem().ShouldContain(Backfill);
        }

        await using var second = await StartMigratingAsync(database, DeskWithBookings, log, afterRevision: 2, new FixedTenants(TenantA, TenantB));
        await second.DrainEventsAsync(Ct);

        log.Runs.Select(run => run.Tenant).ShouldBe([TenantA, TenantB], ignoreOrder: true);
        log.Runs.ShouldAllBe(run => run.Revision == 2);
    }

    private async Task<AlvoTestHost> StartMigratingAsync(
        TimeDatabase? database, string descriptor, MigrationLog log, int afterRevision, IAlvoTenantCatalog? tenants = null, MigrationLogCapture? logs = null)
    {
        var used = database ?? await DatabaseAsync();

        return await AlvoTestHost.StartAsync(
            alvo =>
            {
                used.Use(alvo);
                alvo.FromDescriptor(DescriptorPath(descriptor));
                alvo.AddDataMigration<RecordingMigration>(Backfill, afterRevision).Services.AddSingleton(log);
                if (tenants is not null)
                {
                    alvo.Services.AddSingleton(tenants);
                }

                if (logs is not null)
                {
                    alvo.Services.AddSingleton<ILoggerProvider>(logs);
                }
            },
            Time,
            cancellationToken: Ct);
    }

    private static async Task ApplyAsync(AlvoTestHost alvo, string descriptor)
    {
        var management = AsDeveloper(alvo);
        var current = await management.GetDescriptorAsync(MigrationDesk, Ct);
        var json = await File.ReadAllTextAsync(DescriptorPath(descriptor), Ct);

        (await management.ApplyDescriptorAsync(MigrationDesk, new ManagementApplyRequest(json, current.Revision), Ct)).Applied.ShouldBeTrue();
    }

    private static async Task RollbackAsync(AlvoTestHost alvo, int toRevision)
    {
        var management = AsDeveloper(alvo);
        var current = await management.GetDescriptorAsync(MigrationDesk, Ct);
        var request = new ManagementRollbackRequest(current.Revision, AllowDestructive: true);

        (await management.RollbackAsync(MigrationDesk, toRevision, request, Ct)).Applied.ShouldBeTrue();
    }

    private static IAlvoManagement AsDeveloper(AlvoTestHost alvo)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("ops"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "ops-key",
        };

        return alvo.Services.GetRequiredService<IAlvoManagement>();
    }

    private static string DescriptorPath(string file) => Path.Combine(AppContext.BaseDirectory, "descriptors", file);
}
