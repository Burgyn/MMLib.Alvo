using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Scheduling;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Scheduling;

/// <summary>
/// <see cref="DataMigrationStarter"/> on its own: per tenant decided by the revision it starts at, a failed start that waits its
/// own backoff without holding the others back, and a scoped schema with no tenants marked started and said so.
/// </summary>
public sealed class DataMigrationStarterTests : IDisposable
{
    private const string First = "aaa-first";
    private const string Second = "bbb-second";
    private static readonly DateTimeOffset _now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private static readonly SchemaModel _scoped = new([
        new EntitySchema
        {
            Name = "bookings",
            Tenancy = TenancyMode.Scoped,
            Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid }],
        },
    ]);

    private readonly FakeTimeProvider _time = new(_now);
    private readonly ITimerStore _store = Substitute.For<ITimerStore>();
    private readonly IAppliedSchemaStore _applied = Substitute.For<IAppliedSchemaStore>();
    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;

    public DataMigrationStarterTests()
    {
        _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));
        _store.StartedMigrationsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _store.StartMigrationAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<AlvoFrameworkEnvelope>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        Applied(MMLib.Alvo.Tests.Extensions.HandlerCatalog.Schema);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task A_replica_whose_catalog_is_unscoped_starts_per_tenant_runs_at_a_scoped_revision()
    {
        Applied(_scoped);
        var tenants = new[] { new TenantId(Guid.NewGuid()), new TenantId(Guid.NewGuid()) };

        (await Starter(tenants).StartDueAsync(_store, Bindings(First), "desk", Ct)).ShouldBe(new MigrationRound(1, 0));

        await _store.Received(1).StartMigrationAsync(
            First, 2, Arg.Is<IReadOnlyList<AlvoFrameworkEnvelope>>(events => events.Count == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_scoped_revision_without_a_tenant_catalog_holds_the_migration_and_says_so_once()
    {
        Applied(_scoped);
        var starter = Starter(tenants: null);

        await starter.StartDueAsync(_store, Bindings(First), "desk", Ct);
        await starter.StartDueAsync(_store, Bindings(First), "desk", Ct);

        await _store.DidNotReceiveWithAnyArgs().StartMigrationAsync(default!, default, default!, Ct);
        _logs.Entries.Count(entry => entry.EventName == "TenantCatalogMissing").ShouldBe(1);
    }

    [Fact]
    public async Task One_failing_start_does_not_stop_the_others()
    {
        FailStarting(First);

        var round = await Starter([]).StartDueAsync(_store, Bindings(First, Second), "desk", Ct);

        round.ShouldBe(new MigrationRound(Started: 1, Failed: 1));
        await _store.Received(1).StartMigrationAsync(Second, 2, Arg.Any<IReadOnlyList<AlvoFrameworkEnvelope>>(), Arg.Any<CancellationToken>());
        _logs.Entries.ShouldContain(entry => entry.EventName == "MigrationStartFailed" && entry.Message.Contains(First));
    }

    [Fact]
    public async Task A_failed_start_waits_a_minute_then_twice_as_long_rather_than_retrying_every_round()
    {
        FailStarting(First);
        var starter = Starter([]);
        var bindings = Bindings(First);

        await starter.StartDueAsync(_store, bindings, "desk", Ct);
        await starter.StartDueAsync(_store, bindings, "desk", Ct);
        _time.Advance(TimeSpan.FromSeconds(59));
        await starter.StartDueAsync(_store, bindings, "desk", Ct);
        Attempts().ShouldBe(1);

        _time.Advance(TimeSpan.FromSeconds(1));
        await starter.StartDueAsync(_store, bindings, "desk", Ct);
        _time.Advance(TimeSpan.FromSeconds(119));
        await starter.StartDueAsync(_store, bindings, "desk", Ct);
        Attempts().ShouldBe(2);

        _time.Advance(TimeSpan.FromSeconds(1));
        await starter.StartDueAsync(_store, bindings, "desk", Ct);
        Attempts().ShouldBe(3);
    }

    [Fact]
    public async Task A_scoped_schema_with_no_tenants_marks_the_migration_started_and_says_so()
    {
        Applied(_scoped);

        (await Starter([]).StartDueAsync(_store, Bindings(First), "desk", Ct)).ShouldBe(new MigrationRound(1, 0));

        await _store.Received(1).StartMigrationAsync(
            First, 2, Arg.Is<IReadOnlyList<AlvoFrameworkEnvelope>>(events => events.Count == 0), Arg.Any<CancellationToken>());
        _logs.Entries.ShouldContain(entry => entry.EventName == "MigrationStartedWithNoTenants" && entry.Level == LogLevel.Information);
    }

    private DataMigrationStarter Starter(TenantId[]? tenants) => new(
        new ScheduleRuns(tenants is null ? null : new Tenants(tenants), _time), _applied, _time, _loggers.CreateLogger<DataMigrationStarter>());

    private void Applied(SchemaModel schema) =>
        _applied.GetCurrentAsync("desk", Arg.Any<CancellationToken>()).Returns(new AppliedSchema(schema, "{}", 2, _now));

    private void FailStarting(string id) =>
        _store.StartMigrationAsync(id, Arg.Any<int>(), Arg.Any<IReadOnlyList<AlvoFrameworkEnvelope>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the append was rejected"));

    private int Attempts() =>
        _store.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ITimerStore.StartMigrationAsync) && (string)call.GetArguments()[0]! == First);

    private static ExtensionBindings Bindings(params string[] ids)
    {
        var registry = Registry(alvo =>
        {
            foreach (var id in ids)
            {
                alvo.AddDataMigration<Backfill>(id, afterRevision: 1);
            }
        });

        return ExtensionBindingCompiler.Compile(registry, Catalog(), CelFixtures.Compiler, EveryService()).Bindings;
    }

    private sealed class Backfill : IAlvoDataMigration
    {
        public Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Tenants(TenantId[] tenants) : IAlvoTenantCatalog
    {
        public Task<IReadOnlyList<TenantId>> ListTenantsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantId>>(tenants);
    }
}
