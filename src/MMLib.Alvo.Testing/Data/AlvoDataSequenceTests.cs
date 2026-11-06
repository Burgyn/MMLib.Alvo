using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// A field's <c>sequence</c> is a gapless number allocated inside the create's own transaction (HX-12), asked of both
/// shipped drivers: concurrent creates take one number each, a rolled-back create returns its number, each tenant,
/// <c>per</c> value and period counts from its start, and a declared rename keeps counting.
/// </summary>
public abstract class AlvoDataSequenceTests
{
    private const string Orders = "orders";
    private const string SalesOrders = "sales_orders";

    private static readonly DateTimeOffset _lastSecondOf2026 = new(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);
    private static readonly DateTimeOffset _firstSecondOf2027 = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[][] _oppositeOrders = [["north", "south"], ["south", "north"]];
    private static readonly AlvoContext _acme = InTenant(TenantId.New());
    private static readonly AlvoContext _globex = InTenant(TenantId.New());

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));

    /// <summary>Stands up an engine over <paramref name="schema"/>, its policy primed from <paramref name="descriptor"/>.</summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    /// <param name="time">The clock every write is stamped by.</param>
    protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor, TimeProvider time);

    /// <summary>The transaction-unit port over the engine <see cref="CreateAsync"/> stood up.</summary>
    protected abstract IAlvoTransactions Transactions { get; }

    /// <summary>
    /// Plans and applies a migration through this engine's migrator, then re-primes the policy from
    /// <paramref name="descriptor"/>.
    /// </summary>
    /// <param name="current">The schema the store currently holds.</param>
    /// <param name="desired">The schema to migrate it to.</param>
    /// <param name="descriptor">The descriptor <paramref name="desired"/> maps from.</param>
    protected abstract Task ApplyAsync(SchemaModel current, SchemaModel desired, AlvoDescriptor descriptor);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Acceptance (1): a hundred concurrent creates are numbered one to a hundred, without a gap or a duplicate.</summary>
    [Fact]
    public async Task A_hundred_concurrent_creates_number_one_to_a_hundred_without_gap_or_duplicate()
    {
        var data = await CreateAsync(Schema, Descriptor, _clock);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var creates = Enumerable.Range(0, 100).Select(async _ =>
        {
            await start.Task;
            return await data.CreateAsync(Orders, Order(_acme, "north"), _acme, cancellationToken: Ct);
        }).ToList();
        start.SetResult();
        var rows = await Task.WhenAll(creates);

        rows.Select(row => Convert.ToInt64(row["number"], System.Globalization.CultureInfo.InvariantCulture)).Order()
            .ShouldBe(Enumerable.Range(1, 100).Select(number => (long)number));
        rows.Select(row => (string)row["code"]!).Distinct().Count().ShouldBe(100);
    }

    /// <summary>Acceptance (2): a create the engine refuses rolls back, and its number with it.</summary>
    [Fact]
    public async Task A_rolled_back_create_consumes_no_number()
    {
        var data = await CreateAsync(Schema, Descriptor, _clock);
        (await NumberOfAsync(data, _acme, "north")).ShouldBe(1);

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => data.CreateAsync(Orders, Order(_acme, "north", qty: 0), _acme, cancellationToken: Ct));
        refused.Kind.ShouldBe(AlvoConstraintKind.Check);

        (await NumberOfAsync(data, _acme, "north")).ShouldBe(2);
    }

    /// <summary>Acceptance (2): a create inside a unit the host abandons returns its number when the unit rolls back.</summary>
    [Fact]
    public async Task A_create_rolled_back_with_its_unit_consumes_no_number()
    {
        var data = await CreateAsync(Schema, Descriptor, _clock);
        (await NumberOfAsync(data, _acme, "north")).ShouldBe(1);

        await Should.ThrowAsync<InvalidOperationException>(() => Transactions.TransactAsync(_acme, async (unit, ct) =>
        {
            (await unit.CreateAsync(Orders, Order(_acme, "north"), ct))["number"].ShouldBe(2L);
            throw new InvalidOperationException("the host changed its mind");
        }, cancellationToken: Ct));

        (await NumberOfAsync(data, _acme, "north")).ShouldBe(2);
    }

    /// <summary>
    /// A create the engine refuses inside a unit that carries on returns its number with its savepoint — on SQLite too,
    /// where a failed statement does not abort the transaction — so the unit's next create takes the same number.
    /// </summary>
    [Fact]
    public async Task A_create_refused_inside_a_unit_that_carries_on_consumes_no_number()
    {
        var data = await CreateAsync(Schema, Descriptor, _clock);
        (await NumberOfAsync(data, _acme, "north")).ShouldBe(1);

        var kept = await Transactions.TransactAsync(_acme, async (unit, ct) =>
        {
            await Should.ThrowAsync<AlvoConstraintViolationException>(
                () => unit.CreateAsync(Orders, Order(_acme, "north", qty: 0), ct));
            return (await unit.CreateAsync(Orders, Order(_acme, "north"), ct))["number"];
        }, cancellationToken: Ct);

        kept.ShouldBe(2L);
        (await NumberOfAsync(data, _acme, "north")).ShouldBe(3);
    }

    /// <summary>Review focus 5: each tenant, <c>per</c> value and period counts from its start, and none restarts.</summary>
    [Fact]
    public async Task Each_tenant_per_value_and_period_counts_from_start()
    {
        _clock.Now = _lastSecondOf2026;
        var data = await CreateAsync(Schema, Descriptor, _clock);

        (await CreatedAsync(data, _acme, "north")).ShouldBe((1L, "SO-2026-0001"));
        (await CreatedAsync(data, _acme, "south")).ShouldBe((1L, "SO-2026-0002"));
        (await CreatedAsync(data, _globex, "north")).ShouldBe((1L, "SO-2026-0001"));
        (await CreatedAsync(data, _acme, "north")).ShouldBe((2L, "SO-2026-0003"));

        _clock.Now = _firstSecondOf2027;
        (await CreatedAsync(data, _acme, "north")).ShouldBe((3L, "SO-2027-0001"));
    }

    /// <summary>A batch create numbers its rows consecutively, in the order the request lists them.</summary>
    [Fact]
    public async Task A_batch_create_numbers_its_rows_in_order()
    {
        var data = await CreateAsync(Schema, Descriptor, _clock);
        (await NumberOfAsync(data, _acme, "north")).ShouldBe(1);

        var batch = await data.CreateManyAsync(
            Orders, [Order(_acme, "north", qty: 1), Order(_acme, "north", qty: 2), Order(_acme, "north", qty: 3)], _acme, cancellationToken: Ct);

        batch.Rows.Select(row => (row["qty"], row["number"])).ShouldBe([(1L, 2L), (2L, 3L), (3L, 4L)]);
    }

    /// <summary>
    /// Two batches over the same counts in opposite orders, released together round after round, both succeed: the counter
    /// rows are taken in one canonical order, so neither waits on the other in a cycle.
    /// </summary>
    [Fact]
    public async Task Concurrent_batches_over_the_same_counts_in_opposite_orders_both_succeed()
    {
        const int Rounds = 10;
        var data = await CreateAsync(Schema, Descriptor, _clock);

        for (var round = 0; round < Rounds; round++)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var batches = _oppositeOrders.Select(async branches =>
            {
                await start.Task;
                return await data.CreateManyAsync(
                    Orders, [.. branches.Select(branch => Order(_acme, branch))], _acme, cancellationToken: Ct);
            }).ToList();
            start.SetResult();
            (await Task.WhenAll(batches)).ShouldAllBe(batch => batch.Rows.Count == 2);
        }

        var rows = await data.QueryAsync(new AlvoQuery { Entity = Orders, Limit = 100 }, _acme, Ct);
        foreach (var branch in _oppositeOrders[0])
        {
            rows.Items.Where(row => (string)row["branch"]! == branch).Select(row => (long)row["number"]!).Order()
                .ShouldBe(Enumerable.Range(1, 2 * Rounds).Select(number => (long)number), branch);
        }
    }

    /// <summary>A replace that creates its row takes the next number; one that replaces a stored row keeps its number.</summary>
    [Fact]
    public async Task A_replace_that_creates_assigns_a_number_and_one_that_updates_keeps_it()
    {
        var data = await CreateAsync(Schema, Descriptor, _clock);
        (await NumberOfAsync(data, _acme, "north")).ShouldBe(1);
        var id = Guid.NewGuid();

        var created = await data.ReplaceAsync(Orders, id, Replacement(qty: 1), _acme, cancellationToken: Ct);
        var replaced = await data.ReplaceAsync(Orders, id, Replacement(qty: 9), _acme, cancellationToken: Ct);

        created.Created.ShouldBeTrue();
        created.Row["number"].ShouldBe(2L);
        replaced.Created.ShouldBeFalse();
        (replaced.Row["number"], replaced.Row["code"], replaced.Row["qty"]).ShouldBe((2L, created.Row["code"], 9L));
    }

    /// <summary>Review focus 4: a declared rename of the entity and the field keeps counting — a restart would re-issue numbers.</summary>
    [Fact]
    public async Task Renaming_a_sequenced_entity_and_field_keeps_counting()
    {
        var data = await CreateAsync(Schema, Descriptor, _clock);
        (await NumberOfAsync(data, _acme, "north")).ShouldBe(1);
        (await NumberOfAsync(data, _acme, "north")).ShouldBe(2);

        await ApplyAsync(Schema, RenamedSchema, RenamedDescriptor);

        var row = await data.CreateAsync(SalesOrders, Order(_acme, "north"), _acme, cancellationToken: Ct);
        (row["seq"], row["code"]).ShouldBe((3L, "SO-2026-0003"));
    }

    /// <summary>
    /// Counter rows are never deleted, so a rename onto a name an earlier sequence counted under is refused by name when it
    /// is planned — rewriting the renamed count onto those rows would merge two counts, and hit the counter table's key.
    /// </summary>
    [Fact]
    public async Task A_rename_onto_a_name_an_earlier_sequence_counted_under_is_refused()
    {
        var data = await CreateAsync(WithLegacy(Schema, "legacy", renamedFrom: null, sequenced: true), Descriptor, _clock);
        (await NumberOfAsync(data, _acme, "north")).ShouldBe(1);
        var retired = WithLegacy(Schema, "old_legacy", renamedFrom: "legacy", sequenced: false);
        await ApplyAsync(WithLegacy(Schema, "legacy", renamedFrom: null, sequenced: true), retired, Descriptor);

        var onto = WithLegacy(new SchemaModel([Entity(Orders, renamedFrom: null, numberField: "legacy", numberRenamedFrom: "number")]),
            "old_legacy", renamedFrom: null, sequenced: false);
        var refused = await Should.ThrowAsync<DescriptorValidationException>(
            () => ApplyAsync(WithLegacy(Schema, "old_legacy", renamedFrom: null, sequenced: false), onto, DescriptorFor(Orders, "legacy")));

        var error = refused.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/orders/fields/legacy/renamedFrom");
        error.Message.ShouldStartWith("'orders.legacy' still holds the sequence counts of an earlier declaration of that name");
    }

    private static SchemaModel WithLegacy(SchemaModel schema, string name, string? renamedFrom, bool sequenced) => new([
        schema.Entities[0] with
        {
            Fields =
            [
                .. schema.Entities[0].Fields,
                new FieldSchema
                {
                    Name = name,
                    RenamedFrom = renamedFrom,
                    Type = SchemaField.Integer,
                    Nullable = true,
                    Sequence = sequenced ? new SequenceSchema { Per = "branch" } : null,
                },
            ],
        },
    ]);

    private static async Task<(long Number, string Code)> CreatedAsync(IAlvoData data, AlvoContext caller, string branch)
    {
        var row = await data.CreateAsync(Orders, Order(caller, branch), caller, cancellationToken: Ct);
        return ((long)row["number"]!, (string)row["code"]!);
    }

    private static async Task<long> NumberOfAsync(IAlvoData data, AlvoContext caller, string branch) =>
        (long)(await data.CreateAsync(Orders, Order(caller, branch), caller, cancellationToken: Ct))["number"]!;

    private static Dictionary<string, object?> Order(AlvoContext caller, string branch, long qty = 1) => new(StringComparer.Ordinal)
    {
        ["tenant_id"] = caller.Tenant!.Value.Value,
        ["branch"] = branch,
        ["qty"] = qty,
    };

    private static Dictionary<string, object?> Replacement(long qty) =>
        new(StringComparer.Ordinal) { ["branch"] = "north", ["qty"] = qty };

    private static AlvoContext InTenant(TenantId tenant) => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = tenant,
    };

    private static SchemaModel Schema => new([Entity(Orders, renamedFrom: null, numberField: "number", numberRenamedFrom: null)]);

    private static SchemaModel RenamedSchema => new([Entity(SalesOrders, renamedFrom: Orders, numberField: "seq", numberRenamedFrom: "number")]);

    private static EntitySchema Entity(string name, string? renamedFrom, string numberField, string? numberRenamedFrom) => new()
    {
        Name = name,
        RenamedFrom = renamedFrom,
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
            new FieldSchema { Name = "branch", Type = SchemaField.String, Required = true, MaxLength = 20 },
            new FieldSchema { Name = "qty", Type = SchemaField.Integer, Required = true, CheckExpression = "qty > 0" },
            new FieldSchema
            {
                Name = numberField,
                RenamedFrom = numberRenamedFrom,
                Type = SchemaField.Integer,
                Required = true,
                Sequence = new SequenceSchema { Per = "branch" },
            },
            new FieldSchema
            {
                Name = "code",
                Type = SchemaField.String,
                Required = true,
                Sequence = new SequenceSchema { Period = SequenceResetPeriod.Year, Format = "SO-{yyyy}-{n:4}" },
            },
            new FieldSchema { Name = "tenant_id", Type = SchemaField.Uuid, Required = true, Indexed = true },
        ],
    };

    private static AlvoDescriptor Descriptor => DescriptorFor(Orders, "number");

    private static AlvoDescriptor RenamedDescriptor => DescriptorFor(SalesOrders, "seq");

    private static AlvoDescriptor DescriptorFor(string entity, string numberField) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "sequences",
        Tenancy = new Tenancy { Enabled = true },
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [entity] = new()
            {
                Tenancy = EntityTenancy.Scoped,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["branch"] = new() { Type = DescField.String, Required = true, MaxLength = 20 },
                    ["qty"] = new() { Type = DescField.Integer, Required = true, Validation = "value > 0" },
                    [numberField] = new() { Type = DescField.Integer, Required = true, Sequence = new FieldSequence { Per = "branch" } },
                    ["code"] = new()
                    {
                        Type = DescField.String,
                        Required = true,
                        Sequence = new FieldSequence { Period = SequencePeriod.Year, Format = "SO-{yyyy}-{n:4}" },
                    },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
            },
        },
    };

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
