using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

using System.Globalization;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Fails the first unit it commits — after the commit — the way a crash between the write and its answer would.</summary>
internal sealed class FailingAfterCommit(IAlvoTransactions inner) : IAlvoTransactions
{
    private int _failures = 1;

    public async Task<T> TransactAsync<T>(
        AlvoContext context, Func<IAlvoTransaction, CancellationToken, Task<T>> work, AlvoTransactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await inner.TransactAsync(context, work, options, cancellationToken);
        return Interlocked.Decrement(ref _failures) >= 0 ? throw new IOException("the answer was lost after the commit") : result;
    }
}

/// <summary><c>entity.update</c>: exactly once per event, in the event's tenant, through the command ledger.</summary>
public abstract partial class AutomationFacts
{
    private const string MarkRule = "/automation/mark-order/actions#";
    private const int DefaultChainDepth = 8;

    [Fact]
    public async Task An_entity_update_fires_once_per_event_under_redelivery()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database: database, descriptor: WithRule("mark-order", "entity.orders.created", Update("{{new.id}}", new JsonObject { ["status"] = "closed" })), failAfterCommit: true);
        var order = await CreateOrderAsync(alvo, TenantA, "A-1", total: 1m);

        await alvo.DrainEventsAsync(Ct);
        Time.Advance(TimeSpan.FromMinutes(5));
        await alvo.DrainEventsAsync(Ct);

        var delivery = (await DeliveriesAsync(alvo, MarkRule)).ShouldHaveSingleItem();
        (delivery.Status, delivery.Attempts).ShouldBe(("succeeded", 2));
        (await UpdatedEventsAsync(database)).ShouldBe(1, "the retry found its key committed and wrote nothing");
        (await OrderAsync(alvo, TenantA, order))["status"].ShouldBe("closed");

        await RedeliverAsync(alvo, delivery.Id);
        await alvo.DrainEventsAsync(Ct);

        (await UpdatedEventsAsync(database)).ShouldBe(1, "an operator redeliver of the succeeded delivery changes nothing either");
    }

    [Fact]
    public async Task An_entity_update_creates_a_record_when_record_id_is_absent()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database: database);
        await alvo.DrainEventsAsync(Ct);

        Time.Advance(TimeSpan.FromHours(1));
        await alvo.DrainEventsAsync(Ct);
        await alvo.DrainEventsAsync(Ct);

        (await ClosingsAsync(database)).ShouldBe([(TenantA.Value, "nightly"), (TenantB.Value, "nightly")], ignoreOrder: true);
        (await DeliveriesAsync(alvo, NightlyRule)).ShouldAllBe(delivery => delivery.Status == "succeeded");
    }

    [Fact]
    public async Task An_entity_update_payload_null_entry_writes_null_to_an_optional_field()
    {
        await using var alvo = await StartAsync(descriptor: WithRule("mark-order", "entity.orders.created", Update("{{new.id}}", new JsonObject { ["note"] = null })));
        var order = await CreateOrderAsync(alvo, TenantA, "A-1", total: 1m, note: "to clear");

        await alvo.DrainEventsAsync(Ct);

        (await OrderAsync(alvo, TenantA, order))["note"].ShouldBeNull();
    }

    [Fact]
    public async Task An_entity_update_never_writes_across_tenants()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database: database);
        var foreign = await CreateOrderAsync(alvo, TenantB, "B-1", total: 1m, note: "tenant b");
        await alvo.DrainEventsAsync(Ct);

        await ApplyAsync(alvo, WithRule("mark-order", "entity.orders.created", Update(foreign.ToString(), new JsonObject { ["note"] = "written by a" })));
        await CreateOrderAsync(alvo, TenantA, "A-1", total: 1m);
        await alvo.DrainEventsAsync(Ct);

        var delivery = (await DeliveriesAsync(alvo, MarkRule)).ShouldHaveSingleItem();
        (delivery.Status, delivery.LastErrorType).ShouldBe(("dead", "record-not-found"));
        (await OrderAsync(alvo, TenantB, foreign))["note"].ShouldBe("tenant b");
    }

    [Fact]
    public async Task An_entity_update_that_retriggers_its_rule_stops_at_the_depth_limit()
    {
        await using var alvo = await StartAsync(descriptor: WithRule("touch-order", "entity.orders.updated", Update("{{new.id}}", new JsonObject { ["note"] = "touched" })));
        var order = await CreateOrderAsync(alvo, TenantA, "A-1", total: 1m);
        await alvo.Data.UpdateAsync("orders", order, new Dictionary<string, object?> { ["note"] = "edited" }, Staff(TenantA), cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct);

        var touches = await DeliveriesAsync(alvo, "/automation/touch-order/actions#");
        touches.ShouldContain(delivery => delivery.Status == "dead" && delivery.LastErrorType == "chain-depth-exceeded");
        touches.Count.ShouldBeLessThanOrEqualTo(DefaultChainDepth + 1, "the loop stops at the depth limit, never runs away");
    }

    [Fact]
    public async Task An_entity_update_breaking_a_validation_is_dead_write_refused()
    {
        var desk = JsonNode.Parse(WithRule("mark-order", "entity.orders.created", Update("{{new.id}}", new JsonObject { ["number"] = "TAKEN" })))!;
        desk["entities"]!["orders"]!["fields"]!["number"]!["unique"] = true;
        await using var alvo = await StartAsync(descriptor: desk.ToJsonString());
        await CreateOrderAsync(alvo, TenantA, "TAKEN", total: 1m);
        await alvo.DrainEventsAsync(Ct);
        await CreateOrderAsync(alvo, TenantA, "A-2", total: 1m);

        await alvo.DrainEventsAsync(Ct);

        var delivery = (await DeliveriesAsync(alvo, MarkRule)).Single(candidate => candidate.Status != "succeeded");
        (delivery.Status, delivery.Attempts, delivery.LastErrorType).ShouldBe(("dead", 1, "write-refused"));
    }

    private static JsonObject Update(string recordId, JsonObject payload) => new()
    {
        ["type"] = "entity.update",
        ["entity"] = "orders",
        ["recordId"] = recordId,
        ["payload"] = payload,
    };

    private static string WithRule(string name, string trigger, JsonObject action)
    {
        var desk = Desk();
        desk["automation"]![name] = new JsonObject
        {
            ["trigger"] = new JsonObject { ["event"] = trigger },
            ["actions"] = new JsonArray(action),
        };
        return desk.ToJsonString();
    }

    private static async Task<AlvoRecord> OrderAsync(AlvoTestHost alvo, TenantId tenant, Guid id) =>
        (await alvo.Data.GetAsync("orders", id, Staff(tenant), Ct)).ShouldNotBeNull();

    private async Task RedeliverAsync(AlvoTestHost alvo, Guid delivery) =>
        (await alvo.Services.GetRequiredService<IDeliveryStore>().RedeliverAsync(delivery, new DeliveryOperationEntry
        {
            Id = Guid.CreateVersion7(),
            At = Time.GetUtcNow(),
            Actor = "operator",
            Operation = "redeliver",
            Target = delivery.ToString(),
            Reason = "checking it is exactly once",
        }, Ct)).ShouldBeTrue();

    private static async Task<long> UpdatedEventsAsync(AutomationDatabase database)
    {
        await using var connection = database.Connect();
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM alvo_outbox WHERE event_type = 'entity.orders.updated'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private static async Task<List<(Guid Tenant, string Note)>> ClosingsAsync(AutomationDatabase database)
    {
        await using var connection = database.Connect();
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tenant_id, note FROM closings";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(Guid, string)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((Guid.Parse(reader.GetValue(0).ToString()!, CultureInfo.InvariantCulture), reader.GetString(1)));
        }

        return rows;
    }
}
