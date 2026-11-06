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

/// <summary>Adds <c>tenant:note</c> for every booking its run can read, so a run that read another tenant's row shows.</summary>
public sealed class NoteReadingMigration(ConcurrentBag<string> seen) : IAlvoDataMigration
{
    public async Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken)
    {
        var bookings = await context.Data.QueryAsync(new AlvoQuery { Entity = "bookings" }, cancellationToken);
        foreach (var booking in bookings.Items)
        {
            seen.Add($"{context.Tenant}:{booking["note"]}");
        }
    }
}

/// <summary>Lists two tenants, after failing the first <see cref="FailuresLeft"/> times it is asked.</summary>
public sealed class FlakyTenants(int failures, params TenantId[] tenants) : IAlvoTenantCatalog
{
    public int FailuresLeft { get; private set; } = failures;

    public int Calls { get; private set; }

    public Task<IReadOnlyList<TenantId>> ListTenantsAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        if (FailuresLeft > 0)
        {
            FailuresLeft--;
            throw new InvalidOperationException("the tenant directory is down");
        }

        return Task.FromResult<IReadOnlyList<TenantId>>(tenants);
    }
}

/// <summary>Records the schedule pump's <c>TenantCatalogMissing</c> and <c>MigrationStartFailed</c> lines.</summary>
internal sealed class MigrationLogCapture : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _missing = new();
    private readonly ConcurrentQueue<string> _failed = new();

    internal IReadOnlyList<string> TenantCatalogMissing => [.. _missing];

    internal IReadOnlyList<string> MigrationStartFailed => [.. _failed];

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

        if (eventId.Name == "MigrationStartFailed")
        {
            _failed.Enqueue(formatter(state, exception));
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
    public async Task A_scoped_schema_runs_the_migration_once_per_tenant_and_each_run_reads_only_its_own_rows()
    {
        var log = new MigrationLog();
        var seen = new ConcurrentBag<string>();
        await using var alvo = await StartMigratingAsync(
            await DatabaseAsync(), BookingDesk, log, afterRevision: 1, new FixedTenants(TenantA, TenantB),
            also: builder => builder.AddDataMigration<NoteReadingMigration>("read-notes", afterRevision: 1).Services.AddSingleton(seen));
        await SeedBookingAsync(alvo, TenantA, "a-note");
        await SeedBookingAsync(alvo, TenantB, "b-note");

        await alvo.DrainEventsAsync(Ct);

        log.Runs.Select(run => run.Tenant).ShouldBe([TenantA, TenantB], ignoreOrder: true);
        log.Runs.ShouldAllBe(run => run.Revision == 1 && run.Id == Backfill);
        seen.ShouldBe([$"{TenantA}:a-note", $"{TenantB}:b-note"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_replica_still_on_the_unscoped_catalog_starts_per_tenant_runs_at_the_first_scoped_revision()
    {
        var database = await DatabaseAsync();
        var applier = new MigrationLog();
        var stale = new MigrationLog();
        await using var first = await StartMigratingAsync(database, DeskFile, applier, afterRevision: 2, new FixedTenants(TenantA, TenantB));
        await using var second = await StartMigratingAsync(database, DeskFile, stale, afterRevision: 2, new FixedTenants(TenantA, TenantB));

        await ApplyAsync(first, DeskWithBookings);
        await second.DrainEventsAsync(Ct);

        stale.Runs.Select(run => run.Tenant).ShouldBe([TenantA, TenantB], ignoreOrder: true);
        stale.Runs.ShouldAllBe(run => run.Revision == 2);
    }

    [Fact]
    public async Task A_failed_start_is_retried_after_its_backoff_not_every_round_and_then_starts()
    {
        var log = new MigrationLog();
        var logs = new MigrationLogCapture();
        var tenants = new FlakyTenants(1, TenantA, TenantB);
        await using var alvo = await StartMigratingAsync(await DatabaseAsync(), BookingDesk, log, afterRevision: 1, tenants, logs);

        await alvo.DrainEventsAsync(Ct);
        await alvo.DrainEventsAsync(Ct);
        (tenants.Calls, log.Runs.Count, logs.MigrationStartFailed.Count).ShouldBe((1, 0, 1));
        (await TimerStore(alvo).StartedMigrationsAsync(Ct)).ShouldBeEmpty();

        Time.Advance(TimeSpan.FromMinutes(1));
        await alvo.DrainEventsAsync(Ct);

        log.Runs.Select(run => run.Tenant).ShouldBe([TenantA, TenantB], ignoreOrder: true);
        (await TimerStore(alvo).StartedMigrationsAsync(Ct)).ShouldBe([Backfill]);
    }

    [Fact]
    public async Task A_migration_that_has_run_does_not_refuse_a_boot_on_a_schema_scoped_since()
    {
        var database = await DatabaseAsync();
        var log = new MigrationLog();
        await using (var first = await StartMigratingAsync(database, DeskFile, log, afterRevision: 1))
        {
            await first.DrainEventsAsync(Ct);
            await ApplyAsync(first, DeskWithBookings);
        }

        await using var second = await StartMigratingAsync(database, DeskWithBookings, log, afterRevision: 1);
        await second.DrainEventsAsync(Ct);

        log.Runs.ShouldHaveSingleItem().Tenant.ShouldBeNull();
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
        TimeDatabase? database,
        string descriptor,
        MigrationLog log,
        int afterRevision,
        IAlvoTenantCatalog? tenants = null,
        MigrationLogCapture? logs = null,
        Action<IAlvoBuilder>? also = null)
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

                also?.Invoke(alvo);
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
