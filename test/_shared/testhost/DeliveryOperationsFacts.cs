using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;

using System.Collections.Concurrent;
using System.Data.Common;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// Delivery operations over the real pipeline (spec 2026-10-14 §7 HX-06) on whichever engine the deriving class supplies —
/// SQLite in <c>MMLib.Alvo.TestHost.Tests</c>, PostgreSQL in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
public abstract class DeliveryOperationsFacts
{
    private const string Project = "ops-desk";
    private const string Tickets = "tickets";
    private const string Handler = "send-confirmation";

    private static readonly DateTimeOffset _start = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AlvoContext Agent { get; } = AlvoTestHost.Caller("agent");

    private static Func<AlvoCreated<AlvoRecord>, Task> Down { get; } = _ => throw new InvalidOperationException("down");

    /// <summary>(1) A replay dry run returns the exact count and changes nothing.</summary>
    [Fact]
    public async Task A_replay_dry_run_returns_the_exact_count_and_changes_nothing()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, Down);
        foreach (var title in new[] { "one", "two", "three" })
        {
            await alvo.Data.CreateAsync(Tickets, Values(("title", title)), Agent, cancellationToken: Ct);
        }

        await alvo.DrainEventsAsync(Ct);
        var before = await DeliveryRows.ReadAsync(database.Connect, Ct);
        var attempts = await DeliveryRows.AttemptCountAsync(database.Connect, Ct);

        var plan = await AsOperator(alvo).ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, DryRun = true }, Ct);

        plan.Count.ShouldBe(3);
        plan.Ids.Count.ShouldBe(3);
        before.ShouldAllBe(row => row.Status == "dead");
        (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldBe(before);
        (await DeliveryRows.AttemptCountAsync(database.Connect, Ct)).ShouldBe(attempts);
        (await DeliveryRows.OperationsAsync(database.Connect, Ct)).ShouldBeEmpty();
    }

    /// <summary>(2) A paused handler accumulates its deliveries and drains them in order after resume.</summary>
    [Fact]
    public async Task A_paused_handler_accumulates_and_drains_in_order_after_resume()
    {
        var database = await DatabaseAsync();
        var seen = new ConcurrentQueue<string>();
        await using var alvo = await StartSavedAsync(database, (AlvoSaved<AlvoRecord> saved) => seen.Enqueue((string)saved.New["title"]!));
        var operations = AsOperator(alvo);
        await operations.SetPausedAsync(Project, Handler, paused: true, "mail provider down", Ct);

        var ticket = await alvo.Data.CreateAsync(Tickets, Values(("title", "first")), Agent, cancellationToken: Ct);
        foreach (var title in new[] { "second", "third" })
        {
            await alvo.Data.UpdateAsync(Tickets, (Guid)ticket[AlvoManagedColumns.Id]!, Values(("title", title)), Agent, cancellationToken: Ct);
        }

        await alvo.DrainEventsAsync(Ct);
        seen.ShouldBeEmpty("a paused handler runs nothing");
        (await DeliveryRows.ReadAsync(database.Connect, Ct)).Select(row => row.Status).ShouldBe(["pending", "pending", "pending"]);

        await operations.SetPausedAsync(Project, Handler, paused: false, reason: null, Ct);
        await alvo.DrainEventsAsync(Ct);

        seen.ShouldBe(["first", "second", "third"]);
    }

    /// <summary>(3) Redelivering a dead delivery unblocks its record's later deliveries.</summary>
    [Fact]
    public async Task Redelivering_a_dead_delivery_unblocks_its_lane()
    {
        var database = await DatabaseAsync();
        var failing = true;
        var seen = new ConcurrentQueue<string>();
        await using var alvo = await StartSavedAsync(database, (AlvoSaved<AlvoRecord> saved) =>
        {
            if (Volatile.Read(ref failing))
            {
                throw new InvalidOperationException("template missing");
            }

            seen.Enqueue((string)saved.New["title"]!);
        });
        var ticket = await alvo.Data.CreateAsync(Tickets, Values(("title", "first")), Agent, cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);
        Volatile.Write(ref failing, false);
        await alvo.Data.UpdateAsync(Tickets, (Guid)ticket[AlvoManagedColumns.Id]!, Values(("title", "second")), Agent, cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);
        seen.ShouldBeEmpty("the dead first delivery holds the record's lane");
        var operations = AsOperator(alvo);
        var dead = (await operations.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Status = "dead" }, Ct)).Items.ShouldHaveSingleItem();

        await operations.RedeliverAsync(Project, dead.Id, "template restored", Ct);
        await alvo.DrainEventsAsync(Ct);

        seen.ShouldBe(["first", "second"]);
        var history = (await operations.GetDeliveryAsync(Project, dead.Id, Ct)).History;
        history.Select(attempt => (attempt.Attempt, attempt.Outcome)).ShouldBe([(1, "dead"), (2, "succeeded")]);
    }

    /// <summary>(7) Pause, redeliver and replay each write one operations-log row and one <c>ManagementAudit</c> line.</summary>
    [Fact]
    public async Task Pause_redeliver_and_replay_each_write_one_log_row_and_one_audit_line()
    {
        var database = await DatabaseAsync();
        var audit = new AuditCapture();
        await using var alvo = await StartAsync(
            database,
            Down,
            services => services.AddSingleton<ILoggerProvider>(audit));
        await alvo.Data.CreateAsync(Tickets, Values(("title", "one")), Agent, cancellationToken: Ct);
        await alvo.Data.CreateAsync(Tickets, Values(("title", "two")), Agent, cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);
        var operations = AsOperator(alvo);
        var dead = (await operations.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Status = "dead", Limit = 1 }, Ct)).Items.Single();

        await operations.SetPausedAsync(Project, Handler, paused: true, "maintenance", Ct);
        await operations.SetPausedAsync(Project, Handler, paused: true, "maintenance", Ct);
        await operations.RedeliverAsync(Project, dead.Id, "retry one", Ct);
        await operations.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, Reason = "retry the rest" }, Ct);

        (await DeliveryRows.OperationsAsync(database.Connect, Ct)).ShouldBe(["pause", "redeliver", "replay"]);
        audit.Lines.Count.ShouldBe(3);
        audit.Lines.ShouldContain(line => line.Contains("made 'replay' on 'send-confirmation' (1 changed)", StringComparison.Ordinal));
    }

    /// <summary>Registers this leg's database; each fact gets a database of its own.</summary>
    protected abstract Task<OperationsDatabase> DatabaseAsync();

    private static Task<AlvoTestHost> StartAsync(OperationsDatabase database, Delegate handler, Action<IServiceCollection>? services = null) =>
        AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "ops-desk.alvo.json"));
                alvo.Entity(Tickets, t => t.OnCreated(Handler, handler, configure: options => options.Retry = AlvoRetryPolicy.None));
                services?.Invoke(alvo.Services);
            },
            new FakeTimeProvider(_start),
            webhooks: null,
            Ct);

    private static Task<AlvoTestHost> StartSavedAsync(OperationsDatabase database, Delegate handler) =>
        AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "ops-desk.alvo.json"));
                alvo.Entity(Tickets, t => t.OnSaved(Handler, handler, configure: options => options.Retry = AlvoRetryPolicy.None));
            },
            new FakeTimeProvider(_start),
            webhooks: null,
            Ct);

    private static IAlvoOperations AsOperator(AlvoTestHost alvo)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("ops"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "ops-key",
        };

        return alvo.Services.GetRequiredService<IAlvoOperations>();
    }

    private static Dictionary<string, object?> Values(params (string Field, object? Value)[] values) =>
        values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal);

    /// <summary>One leg's database: how to point a host at it, and how to read it directly.</summary>
    protected sealed record OperationsDatabase(Action<IAlvoBuilder> Use, Func<DbConnection> Connect);
}
