using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using MMLib.Alvo.Scheduling;

using System.Net;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// A descriptor action an apply no longer declares has its unfinished work cancelled, once, with one operations-log row
/// (issue 480) — and a rollback that declares it again does not bring that work back.
/// </summary>
public abstract partial class AutomationFacts
{
    private const string Superseded = "cancel-superseded";

    [Fact]
    public async Task Editing_a_hook_cancels_its_retrying_delivery_at_the_apply_with_one_operations_log_row()
    {
        var database = await DatabaseAsync();
        var receiver = new ScriptedReceiver(HttpStatusCode.ServiceUnavailable);
        await using var alvo = await StartAsync(receiver, database: database);
        await CreateOrderAsync(alvo, TenantA, "A-1", total: 1m);
        await alvo.DrainEventsAsync(Ct);
        var stale = (await DeliveriesAsync(database, Webhook)).Single(delivery => delivery.Status == "retrying");

        var revision = await ApplyAsync(alvo, WithEditedWebhook());
        Time.Advance(TimeSpan.FromMinutes(5));
        await alvo.DrainEventsAsync(Ct);

        var row = (await DeliveriesAsync(database, Webhook)).Single(delivery => delivery.Subscriber == stale.Subscriber);
        (row.Status, row.LastErrorType).ShouldBe(("cancelled", "superseded"));
        receiver.Bodies.Count.ShouldBe(1, "neither the old nor the edited action runs the cancelled delivery");
        var logged = (await DeliveryRows.OperationRowsAsync(database.Connect, Ct)).ShouldHaveSingleItem();
        (logged.Operation, logged.Actor, logged.Target, logged.Affected).ShouldBe((Superseded, "system", stale.Subscriber, (int?)1));
        logged.Reason.ShouldNotBeNull().ShouldContain($"Revision {revision}");
    }

    [Fact]
    public async Task A_second_apply_cancels_nothing_more_and_logs_nothing_more()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(new ScriptedReceiver(HttpStatusCode.ServiceUnavailable), database: database);
        await CreateOrderAsync(alvo, TenantA, "A-1", total: 1m);
        await alvo.DrainEventsAsync(Ct);

        await ApplyAsync(alvo, WithEditedWebhook());
        await ApplyAsync(alvo, WithEditedWebhook(condition: "new.number != 'x'"));

        (await DeliveryRows.OperationRowsAsync(database.Connect, Ct)).Count(row => row.Operation == Superseded).ShouldBe(1);
    }

    [Fact]
    public async Task A_rollback_that_restores_the_old_hook_leaves_its_cancelled_delivery_cancelled()
    {
        var database = await DatabaseAsync();
        var receiver = new ScriptedReceiver(HttpStatusCode.ServiceUnavailable);
        await using var alvo = await StartAsync(receiver, database: database);
        await CreateOrderAsync(alvo, TenantA, "A-1", total: 1m);
        await alvo.DrainEventsAsync(Ct);
        var stale = (await DeliveriesAsync(database, Webhook)).Single(delivery => delivery.Status == "retrying");
        await ApplyAsync(alvo, WithEditedWebhook());

        await RollbackAsync(alvo, toRevision: 1);
        await CreateOrderAsync(alvo, TenantA, "A-2", total: 2m);
        await alvo.DrainEventsAsync(Ct);
        Time.Advance(TimeSpan.FromMinutes(5));
        await alvo.DrainEventsAsync(Ct);

        var restored = (await DeliveriesAsync(database, Webhook)).Where(delivery => delivery.Subscriber == stale.Subscriber).ToList();
        restored.Count.ShouldBe(2, "the restored action receives the new order's event");
        restored.Single(delivery => delivery.EventId == stale.EventId).Status.ShouldBe("cancelled");
        restored.Single(delivery => delivery.EventId != stale.EventId).Status.ShouldBe("succeeded");
        receiver.Bodies.Count.ShouldBe(2, "the first order's post, then the second's — never the cancelled one again");
    }

    [Fact]
    public async Task Editing_a_cron_rule_s_trigger_removes_its_old_stored_schedule()
    {
        await using var alvo = await StartAsync();
        await alvo.DrainEventsAsync(Ct);
        var before = (await SchedulesAsync(alvo)).ShouldHaveSingleItem();

        await ApplyAsync(alvo, WithNightlyAt("0 3 * * *"));
        await alvo.DrainEventsAsync(Ct);

        var after = (await SchedulesAsync(alvo)).ShouldHaveSingleItem();
        after.ShouldNotBe(before);
        after.ShouldStartWith(NightlySchedule);
    }

    [Fact]
    public async Task Removing_a_cron_rule_removes_its_stored_schedule()
    {
        await using var alvo = await StartAsync();
        await alvo.DrainEventsAsync(Ct);
        (await SchedulesAsync(alvo)).ShouldHaveSingleItem();

        await ApplyAsync(alvo, WithoutNightly());

        (await SchedulesAsync(alvo)).ShouldBeEmpty();
    }

    private static async Task<List<string>> SchedulesAsync(AlvoTestHost alvo) =>
        [.. (await alvo.Services.GetRequiredService<ITimerStore>().SchedulesAsync(Ct))
            .Select(schedule => schedule.Id)
            .Where(id => id.StartsWith(NightlySchedule, StringComparison.Ordinal))];

    private static async Task RollbackAsync(AlvoTestHost alvo, int toRevision)
    {
        using var scope = alvo.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAlvoContextAccessor>().Principal =
            new AlvoPrincipal { Context = AlvoTestHost.Caller("ops"), Scopes = new HashSet<ApiKeyScope>(), KeyId = OrderDesk };
        var management = scope.ServiceProvider.GetRequiredService<IAlvoManagement>();
        var current = await management.GetDescriptorAsync(OrderDesk, Ct);

        (await management.RollbackAsync(OrderDesk, toRevision, new ManagementRollbackRequest(current.Revision), Ct)).Applied.ShouldBeTrue();
    }

    private static string WithNightlyAt(string cron)
    {
        var desk = Desk();
        desk["automation"]!["nightly-close"]!["trigger"]!["schedule"] = cron;
        return desk.ToJsonString();
    }
}
