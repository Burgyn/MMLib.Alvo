using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The lost update a rollup is otherwise: N writers insert one child each against one parent, concurrently, and
/// the parent's <c>sum</c> must equal N.
/// </summary>
/// <remarks>
/// <para>
/// <b>PostgreSQL only, and that inverts this repository's usual assumption.</b> The concurrent-boot facts treat
/// SQLite as the harder leg, because one writer at a time exposes lock contention. Here that same property makes
/// the lost update <em>structurally impossible</em>: SQLite serialises write transactions, so a SQLite leg would
/// be green whatever the implementation does, and a shared fact would report a guarantee it never tested. This
/// fact has to run on PostgreSQL to mean anything.
/// </para>
/// <para>
/// <b>And the window has to be widened, or the fact asserts nothing.</b> Measured: the naive atomic
/// <c>UPDATE parent SET total = (SELECT SUM …)</c> wrote 40 of 40 and looked perfectly correct until a 50 ms
/// delay was added, at which point it wrote 31. The mechanism is EvalPlanQual: under READ COMMITTED the
/// <c>SET</c> expression is evaluated from the snapshot taken at statement start; when the row lock is finally
/// granted, only the outer <c>WHERE</c> (<c>id = @p</c>, still true) is re-checked, so the stale value is
/// written. The wider the interval between "snapshot taken" and "lock granted", the more updates are lost — so
/// without a widening this file is an illusion.
/// </para>
/// <para>
/// <b>How it is widened, and why through this seam.</b> The interval that matters is the recompute statement's
/// own duration, because that is how long the winner holds the parent's row lock while every rival sits behind
/// it with an ageing snapshot. So the driver's <see cref="IFieldSqlRenderer"/> is substituted with one that
/// makes the <em>aggregated column</em> sleep — <see cref="SleepingFieldSqlRenderer"/>, registered after
/// <c>AddAlvo</c> exactly as a host would substitute a driver service. Nothing in the product is modified for
/// the test, and the repair seam it borrows is the one <c>RollupRecompute</c> genuinely routes the aggregated
/// column through.
/// </para>
/// <para>
/// <b>Every writer must also commit.</b> Asserted separately, because an implementation that "fixed" the race by
/// failing half its writers would satisfy the total alone — and SQLite's own measured failure mode
/// (<c>SQLITE_BUSY_SNAPSHOT</c> for a read-then-write transaction) is exactly that shape.
/// </para>
/// <para>
/// <b>The non-vacuity control has been RUN, and this is its number.</b> The widening here happens at a different
/// seam from the spike's (a delay inside the recompute's aggregate, rather than a <c>pg_sleep</c> before the
/// <c>UPDATE</c>), so the equivalence was not something to assume. Measured on 2026-08-05 by mutating
/// <c>RollupRecompute.LockStatement</c> so the dialect is never asked for a lock — which makes it answer
/// <see langword="null"/> on PostgreSQL and skip the locking read, i.e. exactly the lock step removed: this fact
/// <b>failed</b>, with <c>line_count</c> reading <b>2</b> of 20 while all 20 writers committed, reproduced twice.
/// So 18 of 20 recomputes were lost updates, the widening is doing its job, and the fact is measuring the lock
/// rather than passing for an unrelated reason. Restored immediately afterwards, and the run is recorded in
/// <c>docs/superpowers/specs/evidence/2026-08-04-f3-pr6-computed-rollup/spike.txt</c> (Q13). Re-run it if the
/// widening seam, the writer count or the recompute's statement order ever changes.
/// </para>
/// </remarks>
public sealed class PostgreSqlRollupRaceTests : IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    /// <summary>
    /// How many writers race, and how long the recompute is stretched to. Pinned here rather than inlined
    /// because they are the fact's criterion: with either of them at zero the fact is vacuous, and the numbers
    /// are what a later reader has to be able to check.
    /// </summary>
    private const int Writers = 20;

    /// <summary>
    /// The per-row delay inside the aggregate. Per <em>row</em>, so the window widens as children accumulate and
    /// the writers most likely to collide are stretched the most; small enough that the whole serialised run is
    /// a couple of seconds.
    /// </summary>
    private const double RowDelaySeconds = 0.01;

    [Fact]
    public async Task Concurrent_child_writes_all_land_in_the_parents_rollup()
    {
        var host = await _fixture.StartAsync(Schema, Descriptor, configure: Sleeping);
        var data = host.Data;
        var order = await CreateOrderAsync(data);

        var writes = Enumerable.Range(0, Writers).Select(_ => CreateLineAsync(data, order)).ToArray();

        // Not `await Task.WhenAll(writes)`: that rethrows the first fault, so a run that "fixed" the race by
        // failing writers would abort here with the driver's raw exception and the assertion below — the one
        // that tells that outcome from a real fix — would never run. The faults are observed instead, and
        // then asserted on, so the failure names the property this file exists to pin.
        await Task.WhenAll(writes.Select(write => write.ContinueWith(
            _ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)));

        writes.ShouldAllBe(write => write.IsCompletedSuccessfully, "a rollup that serialises by failing writers is not a fix");
        var stored = (await data.GetAsync(Orders, order, Caller, Ct))!;
        stored[LineCount].ShouldBe((long)Writers, "every child committed");
        stored[Total].ShouldBe(Writers * 1m, "and every one of them is in the parent's sum");
    }

    /// <summary>
    /// A filtered rollup under the same race (HX-11 acceptance 3): children land on both sides of
    /// <c>kind == 'sale'</c>, and the parent's <c>sum</c> counts exactly the sale lines — the filter rides the
    /// lock-then-recompute, so no admitted child is lost and no excluded one counted.
    /// </summary>
    /// <remarks>
    /// Its control was run (2026-11-06): with <c>RollupRecompute</c>'s parent lock skipped, <c>total</c> read <b>1</b> of 13
    /// while every writer committed.
    /// </remarks>
    [Fact]
    public async Task Concurrent_child_writes_on_both_sides_of_the_filter_all_land_correctly()
    {
        var host = await _fixture.StartAsync(SchemaFor(Sale), DescriptorFor(Sale), configure: Sleeping);
        var order = await CreateOrderAsync(host.Data);

        var writes = StartWriters(host.Data, order);
        await ObserveAsync(writes);

        writes.ShouldAllBe(write => write.IsCompletedSuccessfully, "a rollup that serialises by failing writers is not a fix");
        (await host.Data.GetAsync(Orders, order, Caller, Ct))![Total].ShouldBe(Sales * 1m, "every sale line, and no refund line");
    }

    /// <summary>The filter the race facts start from.</summary>
    private const string Sale = "kind == 'sale'";

    /// <summary>How many of the <see cref="Writers"/> lines are sales: every third line is a refund.</summary>
    private const int Sales = Writers - Refunds;

    /// <summary>How many of the <see cref="Writers"/> lines are refunds.</summary>
    private const int Refunds = (Writers + 2) / 3;

    /// <summary>One writer per line — every third a <c>refund</c>, the rest <c>sale</c> — all started at once.</summary>
    private static Task<AlvoRecord>[] StartWriters(IAlvoData data, Guid order) =>
        [.. Enumerable.Range(0, Writers).Select(index => CreateLineAsync(data, order, index % 3 == 0 ? "refund" : "sale"))];

    /// <summary>Waits for every writer, observing a fault rather than rethrowing it — see the first fact.</summary>
    private static Task ObserveAsync(Task<AlvoRecord>[] writes) =>
        Task.WhenAll(writes.Select(write => write.ContinueWith(
            _ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)));

    /// <summary>
    /// Registers the widening renderer <em>after</em> <c>AddAlvo</c>, which is the same seam a host uses to
    /// substitute one of a driver's services: the driver registers its own with <c>TryAdd</c>, so the last
    /// explicit registration is the one resolved.
    /// </summary>
    private static void Sleeping(IServiceCollection services) =>
        services.AddSingleton<IFieldSqlRenderer>(new SleepingFieldSqlRenderer(new PostgreSqlFieldSqlRenderer(), RowDelaySeconds));

    /// <summary>
    /// <see cref="PostgreSqlFieldSqlRenderer"/> with one difference: a <b>decimal</b> operand comes back wrapped
    /// so evaluating it sleeps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>pg_sleep</c> returns <c>void</c>, which cannot be compared or added — so it is reached through
    /// <c>length(pg_sleep(…)::text)</c>, which is always <c>0</c> and therefore leaves the operand's
    /// <em>value</em> exactly as it was. That matters: a widening that changed the number would make the fact
    /// assert something else entirely.
    /// </para>
    /// <para>
    /// Restricted to <see cref="CelValueType.Decimal"/> so it touches the aggregate and nothing else. The read
    /// path routes its keyset key (a <c>uuid</c>) through the same member, and a sleeping page read would make
    /// the assertion's own <c>GET</c> slow for no reason.
    /// </para>
    /// </remarks>
    /// <param name="inner">The driver's real renderer, which decides everything else.</param>
    /// <param name="seconds">The per-evaluation delay.</param>
    private sealed class SleepingFieldSqlRenderer(IFieldSqlRenderer inner, double seconds) : IFieldSqlRenderer
    {
        public string TrueLiteral => inner.TrueLiteral;

        public string FalseLiteral => inner.FalseLiteral;

        public string RenderField(EntitySchema entity, string fieldName) => inner.RenderField(entity, fieldName);

        public string RenderParameter(string parameterName) => inner.RenderParameter(parameterName);

        public string RenderCaseInsensitiveLike(string left, string right) => inner.RenderCaseInsensitiveLike(left, right);

        /// <summary>Forwarded, so a filter's text constant renders as the driver's own literal.</summary>
        /// <param name="value">The constant.</param>
        public string? RenderStringLiteral(string value) => inner.RenderStringLiteral(value);

        public (string Left, string Right) RenderComparableOperands(string left, string right, CelValueType type) =>
            type == CelValueType.Decimal ? (Slow(left), Slow(right)) : inner.RenderComparableOperands(left, right, type);

        private string Slow(string operand) =>
            $"({operand} + length(pg_sleep({seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)})::text))";
    }

    private const string Orders = "orders";

    private const string Lines = "order_lines";

    private const string Order = "order_id";

    private const string Total = "total";

    private const string LineCount = "line_count";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AlvoContext _caller = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    private static AlvoContext Caller => _caller;

    private static async Task<Guid> CreateOrderAsync(IAlvoData data) =>
        (Guid)(await data.CreateAsync(
            Orders, new Dictionary<string, object?>(StringComparer.Ordinal), Caller, cancellationToken: Ct))["id"]!;

    /// <summary>One child worth exactly <c>1</c>, so the expected total is the writer count and nothing else.</summary>
    private static Task<AlvoRecord> CreateLineAsync(IAlvoData data, Guid order, string kind = "sale") =>
        data.CreateAsync(
            Lines,
            new Dictionary<string, object?>(StringComparer.Ordinal) { [Order] = order, ["amount"] = 1m, ["kind"] = kind },
            Caller,
            cancellationToken: Ct);

    private static AlvoDescriptor Descriptor => DescriptorFor(where: null);

    /// <summary>The pair with <c>orders.total</c> filtered by <paramref name="where"/>, or unfiltered.</summary>
    private static AlvoDescriptor DescriptorFor(string? where) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "rollup-race",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Orders] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    [Total] = new() { Type = DescField.Decimal, Rollup = new() { From = Lines, Op = RollupOp.Sum, Field = "amount", Where = where } },
                    [LineCount] = new() { Type = DescField.Integer, Rollup = new() { From = Lines, Op = RollupOp.Count } },
                },
                Rules = AllowAll,
            },
            [Lines] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    [Order] = new() { Type = DescField.Ref, Entity = Orders, Required = true },
                    ["amount"] = new() { Type = DescField.Decimal, Required = true },
                    ["kind"] = new() { Type = DescField.String },
                },
                Rules = AllowAll,
            },
        },
    };

    private static AccessRules AllowAll =>
        new() { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" };

    private static SchemaModel Schema => SchemaFor(where: null);

    /// <summary>The schema <see cref="DescriptorFor"/> maps to.</summary>
    private static SchemaModel SchemaFor(string? where) => new([
        new EntitySchema
        {
            Name = Orders,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema
                {
                    Name = Total,
                    Type = SchemaField.Decimal,
                    Precision = 18,
                    Scale = 2,
                    Nullable = true,
                    Rollup = new RollupSchema { From = Lines, Op = RollupOperation.Sum, Field = "amount", Via = Order, Where = where },
                },
                new FieldSchema
                {
                    Name = LineCount,
                    Type = SchemaField.Integer,
                    Nullable = true,
                    Rollup = new RollupSchema { From = Lines, Op = RollupOperation.Count, Via = Order },
                },
            ],
        },
        new EntitySchema
        {
            Name = Lines,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema
                {
                    Name = Order,
                    Type = SchemaField.Ref,
                    Required = true,
                    Reference = new RefSchema(Orders, MMLib.Alvo.Schema.OnDelete.Cascade),
                },
                new FieldSchema { Name = "amount", Type = SchemaField.Decimal, Precision = 18, Scale = 2, Required = true },
                new FieldSchema { Name = "kind", Type = SchemaField.String, Nullable = true },
            ],
        },
    ]);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
