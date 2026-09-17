using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The three create paths that are <b>not</b> the ordinary create, asked the one question the ordinary one is
/// already asked elsewhere: does the parent's rollup move when the child lands?
/// </summary>
/// <remarks>
/// <para>
/// A rollup is a parent column the framework maintains from its children, and the recompute rides the child
/// write's own transaction. Skipped on one of these paths, the child row is stored and answered for while the
/// parent keeps a number that is now wrong — and nothing reports it, because no statement failed. That is the
/// defect this class is about: a total an operator reads, or an agent writes a rule over, that is quietly
/// stale until some later child write on the ordinary path happens to repair it.
/// </para>
/// <para>
/// The engine suite (<c>AlvoDataComputedRollupTests</c>) covers the ordinary create, the update and the
/// delete over both shipped drivers. It never reaches these three, all of which are a different method with
/// its own copy of the call: the idempotent create, the create branch of a replace, and the batch create.
/// </para>
/// <para>
/// Integer amounts throughout, deliberately: SQLite stores a <c>decimal</c> as text, and a sum over it is a
/// question about the driver's storage mapping rather than about whether the recompute ran at all.
/// </para>
/// </remarks>
public class EfAlvoDataRollupWritePathTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// An idempotent create — a child written with a retry token — moves the parent's aggregates, exactly as
    /// the untokened one does. The token changes how the write is recorded, not what it means.
    /// </summary>
    [Fact]
    public async Task An_idempotent_create_moves_the_parents_rollups()
    {
        await using var world = await StartAsync();
        var invoice = await InvoiceAsync(world);

        await world.Data.CreateAsync(Items, Item(invoice, 7), world.Caller, Token(), Ct);

        var parent = await ReadInvoiceAsync(world, invoice);
        parent[ItemCount].ShouldBe(1L, "one child now belongs to this invoice");
        parent[TotalAmount].ShouldBe(7L, "and its amount is the invoice's total");
    }

    /// <summary>
    /// A replace at an id no row holds takes the create branch, and that branch maintains the parent too —
    /// the caller-keyed create a <c>PUT</c> of a new id performs.
    /// </summary>
    [Fact]
    public async Task A_replace_that_creates_the_child_moves_the_parents_rollups()
    {
        await using var world = await StartAsync();
        var invoice = await InvoiceAsync(world);

        var result = await world.Data.ReplaceAsync(
            Items, Guid.NewGuid(), Item(invoice, 9), world.Caller, cancellationToken: Ct);

        result.Created.ShouldBeTrue("the id named no row");
        var parent = await ReadInvoiceAsync(world, invoice);
        parent[ItemCount].ShouldBe(1L);
        parent[TotalAmount].ShouldBe(9L);
    }

    /// <summary>
    /// A batch create maintains the parent over the whole batch — and the count is the assertion that a
    /// recompute running once per <em>row</em> would also satisfy, which is why the sum of unequal amounts is
    /// asserted beside it.
    /// </summary>
    [Fact]
    public async Task A_batch_create_moves_the_parents_rollups_over_every_row()
    {
        await using var world = await StartAsync();
        var invoice = await InvoiceAsync(world);

        await world.Data.CreateManyAsync(
            Items, [Item(invoice, 3), Item(invoice, 4), Item(invoice, 5)], world.Caller, cancellationToken: Ct);

        var parent = await ReadInvoiceAsync(world, invoice);
        parent[ItemCount].ShouldBe(3L, "every row of the batch is aggregated, not only the last");
        parent[TotalAmount].ShouldBe(12L);
    }

    private const string Invoices = "invoice";

    private const string Items = "invoice_item";

    private const string Invoice = "invoice_id";

    private const string Amount = "amount";

    private const string ItemCount = "item_count";

    private const string TotalAmount = "total_amount";

    private static Task<WritePathWorld> StartAsync() => WritePathWorld.StartAsync(Descriptor, Schema);

    private static async Task<Guid> InvoiceAsync(WritePathWorld world) =>
        (Guid)(await world.Data.CreateAsync(
            Invoices,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["reference"] = "INV-1" },
            world.Caller,
            cancellationToken: Ct))["id"]!;

    private static async Task<AlvoRecord> ReadInvoiceAsync(WritePathWorld world, Guid id) =>
        (await world.Data.GetAsync(Invoices, id, world.Caller, Ct))!;

    private static Dictionary<string, object?> Item(Guid invoice, int amount) =>
        new(StringComparer.Ordinal) { [Invoice] = invoice, [Amount] = amount };

    private static AlvoIdempotency Token() => new(Guid.NewGuid().ToString(), "fingerprint");

    /// <summary>
    /// One parent with two rollups over one child, and nothing else: a <c>count</c> and a <c>sum</c>, so a
    /// recompute that ran for the wrong number of rows and one that did not run at all are told apart.
    /// </summary>
    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "rollup-write-path-fixture",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Invoices] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["reference"] = new() { Type = DescField.String },
                    [ItemCount] = new() { Type = DescField.Integer, Rollup = new() { From = Items, Op = RollupOp.Count } },
                    [TotalAmount] = new()
                    {
                        Type = DescField.Integer,
                        Rollup = new() { From = Items, Op = RollupOp.Sum, Field = Amount },
                    },
                },
                Rules = AllowAll,
            },
            [Items] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    [Invoice] = new() { Type = DescField.Ref, Entity = Invoices, Required = true },
                    [Amount] = new() { Type = DescField.Integer, Required = true },
                },
                Rules = AllowAll,
            },
        },
    };

    private static AccessRules AllowAll =>
        new() { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" };

    /// <summary>
    /// The applied schema the descriptor above maps to, paired by hand for the reason the engine suite gives:
    /// the core's mapper is <see langword="internal"/> and unreachable from this project.
    /// </summary>
    private static SchemaModel Schema => new([Parent, Child]);

    private static EntitySchema Parent => new()
    {
        Name = Invoices,
        Tenancy = TenancyMode.Global,
        Fields =
        [
            new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
            new FieldSchema { Name = "reference", Type = SchemaField.String, Nullable = true, MaxLength = 32 },
            new FieldSchema
            {
                Name = ItemCount,
                Type = SchemaField.Integer,
                Nullable = true,
                Rollup = new RollupSchema { From = Items, Via = Invoice, Op = RollupOperation.Count },
            },
            new FieldSchema
            {
                Name = TotalAmount,
                Type = SchemaField.Integer,
                Nullable = true,
                Rollup = new RollupSchema
                {
                    From = Items,
                    Via = Invoice,
                    Op = RollupOperation.Sum,
                    Field = Amount,
                },
            },
        ],
    };

    private static EntitySchema Child => new()
    {
        Name = Items,
        Tenancy = TenancyMode.Global,
        Fields =
        [
            new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
            new FieldSchema
            {
                Name = Invoice,
                Type = SchemaField.Ref,
                Required = true,
                Reference = new RefSchema(Invoices, MMLib.Alvo.Schema.OnDelete.Cascade),
            },
            new FieldSchema { Name = Amount, Type = SchemaField.Integer, Required = true },
        ],
    };
}
