using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// What a batch update and a batch delete owe the rest of the system once their rows are judged: the
/// parent's rollups move, one event per row is queued carrying the <b>unmasked</b> images, and the answer
/// comes back in the order the caller sent the rows — not the order the rows were locked in.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these fails <em>silently</em> if it stops happening. A batch whose rollup recompute is gone
/// leaves the parent holding a total that no longer matches its children; a batch whose emit is gone writes
/// the rows and tells no subscriber; a masked image reaches a subscriber as a <c>null</c> that looks like a
/// cleared field; and a result in lock order is still a well-formed result, just about different rows than
/// the caller asked about. None of them throws.
/// </para>
/// <para>
/// Asserted per engine rather than on the inherited suite, because the rollup recompute is a statement this
/// port composes and the events are read out of this engine's own outbox table.
/// </para>
/// </remarks>
public sealed class SqliteAlvoDataBatchWriteTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    /// <summary>
    /// A batch delete lowers the parent's aggregates, exactly as N single deletes would — the recompute is
    /// one statement for the whole batch, and its absence leaves a total that outlives its children.
    /// </summary>
    [Fact]
    public async Task A_batch_delete_lowers_the_parents_rollup()
    {
        var world = await StartAsync();
        var invoice = await CreateInvoiceAsync(world);
        var first = await CreateItemAsync(world, invoice, 10m);
        var second = await CreateItemAsync(world, invoice, 20m);

        var removed = await world.Data.DeleteManyAsync(Items, [first, second], Caller, cancellationToken: Ct);

        removed.Succeeded.ShouldBeTrue();
        var parent = await InvoiceAsync(world, invoice);
        parent[NetTotal].ShouldBeNull("a sum over no children is the engine's own empty answer");
        parent[ItemCount].ShouldBe(0L);
    }

    /// <summary>
    /// A batch update to an aggregated field moves the parent's rollup with it, over <b>both</b> images — the
    /// pre-image is what says which parent to lower and the post-image what says which to raise.
    /// </summary>
    [Fact]
    public async Task A_batch_update_moves_the_parents_rollup()
    {
        var world = await StartAsync();
        var invoice = await CreateInvoiceAsync(world);
        var first = await CreateItemAsync(world, invoice, 10m);
        var second = await CreateItemAsync(world, invoice, 20m);

        var wrote = await world.Data.UpdateManyAsync(
            Items, [Patch(first, 1m), Patch(second, 2m)], Caller, cancellationToken: Ct);

        wrote.Succeeded.ShouldBeTrue();
        (await InvoiceAsync(world, invoice))[NetTotal].ShouldBe(3m);
    }

    /// <summary>A batch delete queues one <c>deleted</c> event per row, and nothing else.</summary>
    [Fact]
    public async Task A_batch_delete_emits_one_deleted_event_per_row()
    {
        var world = await StartAsync();
        var invoice = await CreateInvoiceAsync(world);
        var first = await CreateItemAsync(world, invoice, 10m);
        var second = await CreateItemAsync(world, invoice, 20m);
        var before = (await world.EventsAsync()).Count;

        await world.Data.DeleteManyAsync(Items, [first, second], Caller, cancellationToken: Ct);

        var queued = (await world.EventsAsync()).Skip(before).ToList();
        queued.Select(queue => queue.Type).ShouldBe([ItemDeleted, ItemDeleted]);
    }

    /// <summary>A batch update queues one <c>updated</c> event per row, and nothing else.</summary>
    [Fact]
    public async Task A_batch_update_emits_one_updated_event_per_row()
    {
        var world = await StartAsync();
        var invoice = await CreateInvoiceAsync(world);
        var first = await CreateItemAsync(world, invoice, 10m);
        var second = await CreateItemAsync(world, invoice, 20m);
        var before = (await world.EventsAsync()).Count;

        await world.Data.UpdateManyAsync(
            Items, [Patch(first, 1m), Patch(second, 2m)], Caller, cancellationToken: Ct);

        var queued = (await world.EventsAsync()).Skip(before).ToList();
        queued.Select(queue => queue.Type).ShouldBe([ItemUpdated, ItemUpdated]);
    }

    /// <summary>
    /// A batch delete's event carries the row's <c>hidden</c> field at its stored value — the documented
    /// disclosure, and the only thing that still knows what the row held.
    /// </summary>
    /// <remarks>
    /// The mask is applied to what the <em>caller</em> is answered with, never to the image the judging pass
    /// and the event are built from. A masked pre-image would reach a subscriber as a <c>null</c
    /// >indistinguishable from a field the row genuinely did not hold.
    /// </remarks>
    [Fact]
    public async Task A_batch_delete_event_carries_the_hidden_field_unmasked()
    {
        var world = await StartAsync();
        var invoice = await CreateInvoiceAsync(world);
        var item = await CreateItemAsync(world, invoice, 10m);
        var before = (await world.EventsAsync()).Count;

        await world.Data.DeleteManyAsync(Items, [item], Caller, cancellationToken: Ct);

        var deleted = (await world.EventsAsync()).Skip(before).ShouldHaveSingleItem();
        deleted.Data.OldRecord!["secret"].ShouldBe(Secret);
    }

    /// <summary>The same disclosure on the single-row delete, whose pre-image the event is likewise built from.</summary>
    [Fact]
    public async Task A_delete_event_carries_the_hidden_field_unmasked()
    {
        var world = await StartAsync();
        var invoice = await CreateInvoiceAsync(world);
        var item = await CreateItemAsync(world, invoice, 10m);
        var before = (await world.EventsAsync()).Count;

        await world.Data.DeleteAsync(Items, item, Caller, cancellationToken: Ct);

        var deleted = (await world.EventsAsync()).Skip(before).ShouldHaveSingleItem();
        deleted.Data.OldRecord!["secret"].ShouldBe(Secret);
    }

    /// <summary>
    /// A batch update's event carries the <b>post</b>-image unmasked too, so a hidden field the update did not
    /// touch is not reported as having moved.
    /// </summary>
    [Fact]
    public async Task A_batch_update_event_carries_the_hidden_post_image_unmasked()
    {
        var world = await StartAsync();
        var invoice = await CreateInvoiceAsync(world);
        var item = await CreateItemAsync(world, invoice, 10m);
        var before = (await world.EventsAsync()).Count;

        await world.Data.UpdateManyAsync(Items, [Patch(item, 1m)], Caller, cancellationToken: Ct);

        var updated = (await world.EventsAsync()).Skip(before).ShouldHaveSingleItem();
        updated.Data.Record!["secret"].ShouldBe(Secret);
        updated.Data.Changed.ShouldNotContain(
            "secret", "a masked post-image would report every hidden field as moved on every update");
    }

    /// <summary>
    /// A batch update answers in the order the caller sent the rows, not the order they were locked in. The
    /// rows are deliberately sent in descending id order, which is the reverse of the lock order, so a result
    /// that merely came back "sorted" cannot pass.
    /// </summary>
    /// <remarks>
    /// Reporting the lock order would be worse than reporting nothing: it is a well-formed answer about
    /// different rows than the ones the caller's own indices name, which is the same confusion
    /// <c>BatchWrite</c>'s request index exists to remove on the refusal face.
    /// </remarks>
    [Fact]
    public async Task A_batch_update_answers_its_rows_in_request_order()
    {
        var world = await StartAsync();
        var invoice = await CreateInvoiceAsync(world);
        var ascending = new List<Guid>
        {
            await CreateItemAsync(world, invoice, 10m),
            await CreateItemAsync(world, invoice, 20m),
            await CreateItemAsync(world, invoice, 30m),
        };
        ascending.Sort();
        var requested = Enumerable.Reverse(ascending).ToList();

        var wrote = await world.Data.UpdateManyAsync(
            Items, [.. requested.Select(id => Patch(id, 1m))], Caller, cancellationToken: Ct);

        wrote.Rows.Select(row => (Guid)row["id"]!).ShouldBe(requested, ignoreOrder: false);
    }

    private async Task<AlvoDataOutboxWorld> StartAsync()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor);
        return new AlvoDataOutboxWorld(host.Data, host.Services);
    }

    private static async Task<Guid> CreateInvoiceAsync(AlvoDataOutboxWorld world) =>
        (Guid)(await world.Data.CreateAsync(
            Invoices, new Dictionary<string, object?>(StringComparer.Ordinal), Caller, cancellationToken: Ct))["id"]!;

    private static async Task<Guid> CreateItemAsync(AlvoDataOutboxWorld world, Guid invoice, decimal amount) =>
        (Guid)(await world.Data.CreateAsync(
            Items,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [Invoice] = invoice,
                ["amount"] = amount,
                ["secret"] = Secret,
            },
            Caller,
            cancellationToken: Ct))["id"]!;

    private static async Task<AlvoRecord> InvoiceAsync(AlvoDataOutboxWorld world, Guid id) =>
        (await world.Data.GetAsync(Invoices, id, Caller, Ct))!;

    private static AlvoRowPatch Patch(Guid id, decimal amount) =>
        new(id, new Dictionary<string, object?>(StringComparer.Ordinal) { ["amount"] = amount });

    private const string Invoices = "invoices";

    private const string Items = "items";

    private const string Invoice = "invoice";

    private const string NetTotal = "net_total";

    private const string ItemCount = "item_count";

    private const string ItemDeleted = $"entity.{Items}.deleted";

    private const string ItemUpdated = $"entity.{Items}.updated";

    /// <summary>The value of the child's <c>hidden</c> field, which no caller-facing answer may carry.</summary>
    private const string Secret = "shh";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AlvoContext Caller { get; } = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = null,
    };

    private static AccessRules AllowAll =>
        new() { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" };

    /// <summary>
    /// One parent with two rollups over one child, and a <c>hidden</c> field on the child so every image the
    /// batch builds can be told from a masked one.
    /// </summary>
    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "batch-write-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Invoices] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    [NetTotal] = new() { Type = DescField.Decimal, Rollup = new() { From = Items, Op = RollupOp.Sum, Field = "amount" } },
                    [ItemCount] = new() { Type = DescField.Integer, Rollup = new() { From = Items, Op = RollupOp.Count } },
                },
                Rules = AllowAll,
            },
            [Items] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    [Invoice] = new() { Type = DescField.Ref, Entity = Invoices, Required = true },
                    ["amount"] = new() { Type = DescField.Decimal, Required = true },
                    ["secret"] = new() { Type = DescField.String, Hidden = BoolOrCel.FromBoolean(true) },
                },
                Rules = AllowAll,
            },
        },
    };

    /// <summary>
    /// The applied schema the descriptor above maps to, paired by hand for the reason every suite here gives:
    /// the core's mapper is <see langword="internal"/> and unreachable from a driver's test project.
    /// </summary>
    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Invoices,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                Money(NetTotal) with { Rollup = new RollupSchema { From = Items, Op = RollupOperation.Sum, Field = "amount", Via = Invoice } },
                new FieldSchema
                {
                    Name = ItemCount,
                    Type = SchemaField.Integer,
                    Nullable = true,
                    Rollup = new RollupSchema { From = Items, Op = RollupOperation.Count, Field = null, Via = Invoice },
                },
            ],
        },
        new EntitySchema
        {
            Name = Items,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema
                {
                    Name = Invoice,
                    Type = SchemaField.Ref,
                    Required = true,
                    Reference = new RefSchema(Invoices, MMLib.Alvo.Schema.OnDelete.Cascade),
                },
                Money("amount") with { Required = true, Nullable = false },
                new FieldSchema { Name = "secret", Type = SchemaField.String, Nullable = true, MaxLength = 40 },
            ],
        },
    ]);

    private static FieldSchema Money(string name) => new()
    {
        Name = name,
        Type = SchemaField.Decimal,
        Precision = 18,
        Scale = 2,
        Nullable = true,
    };

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
