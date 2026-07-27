using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using System.Globalization;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// End-to-end value ordering, through the port, over a real engine: the same rows, the same query, the same
/// expected answer on every engine — for the two column types whose <em>storage</em> does not order the way
/// their type does.
/// </summary>
/// <remarks>
/// <para>
/// <c>AlvoDataComparisonTests</c> proves a <b>rule</b> compares a decimal by value; this suite proves the
/// other two channels a comparison reaches SQL through — the caller's <c>filter</c>, and the <c>ORDER BY</c>
/// plus keyset boundary a page is made of — because they are rendered by different code and a repair applied
/// in one is not applied in the others. A page is the sharper of the two: the ordering and the boundary must
/// be the <em>same</em> total order, so a repair present in one and absent in the other does not mis-sort, it
/// silently drops rows.
/// </para>
/// <para>
/// Timestamps are here because nothing repairs them. A <c>datetime</c> field maps to
/// <see cref="DateTimeOffset"/>, PostgreSQL stores it as <c>timestamptz</c> (normalised to UTC, ordered as an
/// instant) and SQLite stores it as <c>TEXT</c> compared lexically — which agrees with instant order only
/// while every stored value carries the same offset. The mixed-offset facts are the ones that tell the two
/// stories apart.
/// </para>
/// </remarks>
public abstract class AlvoDataOrderingTests
{
    private const string Entity = "ledger";

    /// <summary>
    /// Builds a fresh <see cref="IAlvoData"/> over <paramref name="descriptor"/>/<paramref name="schema"/>,
    /// seeded out of band with <paramref name="seed"/>'s rows — the same seam
    /// <see cref="AlvoDataAdversarialTests.CreateAsync"/> defines, so an engine's subclass is the fixture it
    /// already has plus nothing.
    /// </summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    /// <param name="seed">The initial rows to insert, keyed by entity name.</param>
    protected abstract Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed);

    /// <summary>
    /// The cursor a caller would send back to continue after <paramref name="row"/>.
    /// </summary>
    /// <remarks>
    /// A seam rather than an encoding this suite performs: <see cref="AlvoQuery.After"/> is documented as
    /// <em>opaque</em>, so a provider is free to change how it spells a cursor, and a shared suite that
    /// hard-coded one spelling would be asserting on a provider's internals instead of on its behaviour.
    /// </remarks>
    /// <param name="row">The last row of the page just read.</param>
    protected abstract string CursorFor(AlvoRecord row);

    /// <summary>
    /// <c>amount &gt; 100</c> through the caller's filter channel. Lexically <c>'12.34' &gt; '100'</c> is
    /// true, so an unrepaired comparison returns the 12.34 row as well — and a filter is the channel a caller
    /// controls, so being wrong here is being wrong on request.
    /// </summary>
    [Fact]
    public async Task A_decimal_filter_answers_numerically_not_lexicographically()
    {
        var data = await LedgerAsync([9.5m, 12.34m, 100m, 250m]);

        var matched = await AmountsAsync(data, new AlvoComparison("amount", AlvoFilterOperator.Gt, 100m));

        matched.ShouldBe([250m]);
    }

    /// <summary>
    /// Equality is the shape that fails <em>open</em>: with the amount stored as text and the caller's value an
    /// <see cref="long"/> <c>100</c>, an unrepaired <c>eq</c> misses and its negation therefore matches.
    /// </summary>
    /// <remarks>
    /// The value is deliberately a <see cref="long"/> rather than a <see cref="decimal"/>, because what makes
    /// this exact is not the value repair but the fact that the caller's value is bound through the
    /// <b>column's</b> type mapping: a binder that bound by the <em>value's</em> CLR type would send an
    /// <c>INTEGER</c> <c>100</c> against a <c>TEXT</c> column and miss. So this fact guards the binding
    /// authority, and it is the end-to-end guard against that defect returning — it has already been fixed
    /// three times.
    /// </remarks>
    [Fact]
    public async Task A_decimal_filter_equality_answers_numerically()
    {
        var data = await LedgerAsync([9.5m, 100m]);

        var matched = await AmountsAsync(data, new AlvoComparison("amount", AlvoFilterOperator.Eq, 100L));

        matched.ShouldBe([100m]);
    }

    /// <summary>The ordering half: <c>ORDER BY</c> over a decimal key must order by value.</summary>
    [Fact]
    public async Task A_decimal_sort_orders_by_value_not_by_its_text_form()
    {
        var data = await LedgerAsync([100m, 12.34m, 250m, 9.5m]);

        var ordered = await AmountsAsync(data, sort: new AlvoSort("amount"));

        ordered.ShouldBe([9.5m, 12.34m, 100m, 250m]);
    }

    /// <summary>
    /// A page's ordering and its boundary must be one total order. Walking one row at a time is what exposes a
    /// disagreement between them: the walk loses exactly the rows the boundary orders differently from the
    /// <c>ORDER BY</c>.
    /// </summary>
    [Fact]
    public async Task Paging_over_a_decimal_key_neither_skips_nor_repeats_a_row()
    {
        var data = await LedgerAsync([100m, 12.34m, 250m, 9.5m]);

        var walked = await WalkAsync(data, new AlvoSort("amount"), row => Amount(row));

        walked.ShouldBe([9.5m, 12.34m, 100m, 250m]);
    }

    /// <summary>
    /// Timestamps written in UTC — the ordinary case, and the one that must hold everywhere: SQLite's
    /// <c>TEXT</c> form is zero-padded and carries one offset, so its lexical order <em>is</em> instant order.
    /// </summary>
    [Fact]
    public async Task A_timestamp_sort_over_utc_values_orders_by_instant()
    {
        var data = await LedgerAsync(Instants(0, 1, 2, 3));

        var ordered = await InstantsAsync(data, sort: new AlvoSort("occurred_at"));

        ordered.ShouldBe([Midnight, Midnight.AddHours(1), Midnight.AddHours(2), Midnight.AddHours(3)]);
    }

    /// <summary>The filter channel over the same UTC values.</summary>
    [Fact]
    public async Task A_timestamp_filter_over_utc_values_answers_by_instant()
    {
        var data = await LedgerAsync(Instants(0, 1, 2, 3));

        var matched = await InstantsAsync(
            data, new AlvoComparison("occurred_at", AlvoFilterOperator.Gt, Midnight.AddMinutes(90)));

        matched.ShouldBe([Midnight.AddHours(2), Midnight.AddHours(3)]);
    }

    /// <summary>
    /// The paging half. Timestamps are <b>not</b> value-repaired the way decimals are, so this is the fact that
    /// says they do not need to be: the <c>ORDER BY</c> and the keyset boundary compare the same stored text and
    /// therefore agree on one total order.
    /// </summary>
    [Fact]
    public async Task Paging_over_a_utc_timestamp_key_neither_skips_nor_repeats_a_row()
    {
        var data = await LedgerAsync(Instants(3, 0, 2, 1));

        var walked = await WalkAsync(data, new AlvoSort("occurred_at"), Occurred);

        walked.ShouldBe([Midnight, Midnight.AddHours(1), Midnight.AddHours(2), Midnight.AddHours(3)]);
    }

    /// <summary>
    /// The same instants written at <em>different</em> offsets, which the two engines do not handle alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Skipped, and the skip is the finding.</b> Measured in PR2 slice 5 on both engines:
    /// </para>
    /// <para>
    /// PostgreSQL <b>refuses the write</b> — Npgsql throws
    /// <c>Cannot write DateTimeOffset with Offset=-02:00:00 to PostgreSQL type 'timestamp with time zone',
    /// only offset 0 (UTC) is supported</c>, surfacing as a <c>DbUpdateException</c>. SQLite <b>accepts</b> it
    /// and then answers by the stored <c>TEXT</c>: <c>2025-12-31 23:00:00-02:00</c> is the later instant but the
    /// lexically smaller string, so an ascending page walks the rows in reverse-instant order and
    /// <c>occurred_at &gt; 00:30Z</c> matches nothing at all.
    /// </para>
    /// <para>
    /// So the same payload is rejected by one engine and silently mis-answered by the other — §0 principle 3,
    /// and not something a per-engine expectation may paper over. Closing it is a decision, not a bug fix:
    /// normalise every timestamp to UTC on the way in (preserves the instant, discards an offset
    /// <c>timestamptz</c> was never going to keep), or refuse a non-UTC offset on <em>both</em> engines
    /// (fail-closed parity, which is the contract Npgsql's message already states). PR2 does not pick; the fact
    /// stays here so the ruling has one line to enable.
    /// </para>
    /// </remarks>
    [Fact(Skip = "Ruling required: PostgreSQL refuses a non-UTC DateTimeOffset outright, SQLite stores it and "
        + "then orders and filters it by its text form (measured, PR2 slice 5 — see this fact's remarks and "
        + "docs/architecture/data-path.md).")]
    public async Task A_timestamp_written_at_a_non_utc_offset_behaves_the_same_on_every_engine()
    {
        var data = await LedgerAsync(MixedOffsetRows());

        var walked = await WalkAsync(data, new AlvoSort("occurred_at"), Occurred);

        walked.ShouldBe([Midnight, Midnight.AddHours(1), Midnight.AddHours(2)]);
    }

    /// <summary>The instant every timestamp fact is expressed relative to.</summary>
    private static DateTimeOffset Midnight => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Three rows one hour apart, the second and third written at offsets whose text form sorts the wrong way:
    /// <c>2025-12-31 23:00-02:00</c> and <c>2025-12-31 21:00-05:00</c> are both after <c>2026-01-01 00:00Z</c>
    /// as instants and both before it as strings.
    /// </summary>
    private static IReadOnlyList<(decimal Amount, DateTimeOffset Occurred)> MixedOffsetRows() =>
    [
        (1m, Midnight),
        (2m, Midnight.AddHours(1).ToOffset(TimeSpan.FromHours(-2))),
        (3m, Midnight.AddHours(2).ToOffset(TimeSpan.FromHours(-5))),
    ];

    /// <summary>Rows at the given whole-hour UTC offsets from <see cref="Midnight"/>, each with a distinct amount.</summary>
    private static IReadOnlyList<(decimal Amount, DateTimeOffset Occurred)> Instants(params int[] hours) =>
        [.. hours.Select(hour => ((decimal)hour + 1m, Midnight.AddHours(hour)))];

    private Task<IAlvoData> LedgerAsync(IReadOnlyList<decimal> amounts) =>
        LedgerAsync([.. amounts.Select((amount, index) => (amount, Midnight.AddHours(index)))]);

    private async Task<IAlvoData> LedgerAsync(IReadOnlyList<(decimal Amount, DateTimeOffset Occurred)> rows)
    {
        var (descriptor, schema) = Fixture();
        var seed = rows.Select(row => new AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Guid.NewGuid(),
            ["amount"] = row.Amount,
            ["occurred_at"] = row.Occurred,
        }));

        return await CreateAsync(
            schema,
            descriptor,
            new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal) { [Entity] = [.. seed] });
    }

    /// <summary>
    /// A global entity whose every field is <c>required</c>: a paged read needs a non-nullable sort key, and
    /// nothing here is about nullability.
    /// </summary>
    private static (AlvoDescriptor Descriptor, SchemaModel Schema) Fixture()
    {
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "ordering-fixture",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [Entity] = new EntityDescriptor
                {
                    Tenancy = EntityTenancy.Global,
                    Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                    {
                        ["amount"] = new() { Type = DescField.Decimal, Required = true },
                        ["occurred_at"] = new() { Type = DescField.DateTime, Required = true },
                    },
                    Rules = new AccessRules { List = "true", Get = "true" },
                },
            },
        };

        var schema = new SchemaModel([
            new EntitySchema
            {
                Name = Entity,
                Tenancy = TenancyMode.Global,
                Fields =
                [
                    new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                    new FieldSchema { Name = "amount", Type = SchemaField.Decimal, Required = true, Precision = 18, Scale = 2 },
                    new FieldSchema { Name = "occurred_at", Type = SchemaField.DateTime, Required = true },
                ],
            },
        ]);

        return (descriptor, schema);
    }

    private static AlvoContext Caller => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = null,
    };

    private static async Task<IReadOnlyList<decimal>> AmountsAsync(
        IAlvoData data, AlvoFilter? filter = null, AlvoSort? sort = null) =>
        [.. (await RowsAsync(data, filter, sort)).Select(Amount)];

    private static async Task<IReadOnlyList<DateTimeOffset>> InstantsAsync(
        IAlvoData data, AlvoFilter? filter = null, AlvoSort? sort = null) =>
        [.. (await RowsAsync(data, filter, sort)).Select(Occurred)];

    private static Task<IReadOnlyList<AlvoRecord>> RowsAsync(IAlvoData data, AlvoFilter? filter, AlvoSort? sort) =>
        data.QueryAsync(
            new AlvoQuery
            {
                Entity = Entity,
                Filter = filter,
                Sort = sort is null ? [new AlvoSort("amount")] : [sort],
            },
            Caller,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Walks the whole set one row per page, following the cursor the provider itself issued. A page size of
    /// one means every row crosses the boundary, so a boundary that orders differently from the
    /// <c>ORDER BY</c> loses rows rather than merely reordering them.
    /// </summary>
    private async Task<IReadOnlyList<T>> WalkAsync<T>(IAlvoData data, AlvoSort sort, Func<AlvoRecord, T> select)
    {
        var walked = new List<T>();
        string? cursor = null;

        for (var page = 0; page < PageCap; page++)
        {
            var rows = await data.QueryAsync(
                new AlvoQuery { Entity = Entity, Sort = [sort], Limit = 1, After = cursor },
                Caller,
                TestContext.Current.CancellationToken);
            if (rows.Count == 0)
            {
                return walked;
            }

            walked.Add(select(rows[0]));
            cursor = CursorFor(rows[0]);
        }

        throw new InvalidOperationException(
            $"The walk did not terminate after {PageCap} single-row pages — the cursor is not advancing.");
    }

    /// <summary>An upper bound well above any fixture here, so a non-advancing cursor fails rather than hangs.</summary>
    private const int PageCap = 32;

    private static decimal Amount(AlvoRecord row) => Convert.ToDecimal(row["amount"], CultureInfo.InvariantCulture);

    private static DateTimeOffset Occurred(AlvoRecord row) => (DateTimeOffset)row["occurred_at"]!;
}
