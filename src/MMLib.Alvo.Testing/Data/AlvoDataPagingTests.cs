using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// Paging honesty as a rule of the <b>port</b>, proved over every <see cref="IAlvoData"/> implementation
/// this suite runs against — the in-memory reference included: a full last page carries no
/// <see cref="AlvoPage.NextCursor"/>, an offset skips exactly what it says, <see cref="AlvoQuery.After"/>
/// and <see cref="AlvoQuery.Offset"/> cannot both be set, and a forged cursor is an empty page rather than
/// an oracle.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a suite of its own rather than a section of <c>AlvoDataOrderingTests</c>. That suite is
/// about what an engine's <b>storage</b> does to an order — SQLite's <c>TEXT</c>-stored decimals, a
/// timestamp written at a non-UTC offset — and every one of its facts needs a real column type to even ask
/// the question; there is nothing to prove about "what does SQLite storage do to a decimal" over a store
/// that keeps a <see cref="MMLib.Alvo.Rules.IPolicyEngine"/>-evaluated decimal in a C# field. This suite's claim is the
/// opposite shape: it holds regardless of storage, so it is exactly the one shared suite that must also run
/// over <see cref="InMemoryAlvoData"/>, which the ordering suite structurally cannot.
/// </para>
/// <para>
/// The central discipline here is that a fact's row-count/limit pair must actually discriminate the
/// mandated <c>Limit + 1</c> over-fetch (<see cref="AlvoPage.NextCursor"/> comes from whether the extra row
/// existed) from the forbidden <c>Items.Count == Limit</c> derivation (a cursor minted for an exactly-full
/// page, whether or not the visible set had anything left). The two agree everywhere except at the one
/// boundary where the visible set's remaining row count is an exact multiple of <c>Limit</c> — a page that
/// is simultaneously full <em>and</em> last. Every fact below that asserts on <see cref="AlvoPage.NextCursor"/>
/// either sits exactly on that boundary or, where it does not need to (the offset-skip and validation
/// facts test a different axis entirely), does not claim to.
/// </para>
/// </remarks>
public abstract class AlvoDataPagingTests
{
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
    /// A page not last carries a cursor; the final page — every remaining row returned in one go — does
    /// not. The reference and both shipped backends must agree on this exactly, because a page's shape is
    /// part of the port's contract, not an implementation detail one of them could answer more generously.
    /// </summary>
    [Fact]
    public async Task A_page_that_is_not_the_last_carries_a_cursor_and_the_last_one_does_not()
    {
        var world = await SeededWorldAsync(rowCount: 5);

        var first = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Limit = 2 }, world.Alice);
        first.Items.Count.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull();

        var last = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Limit = 10 }, world.Alice);
        last.Items.Count.ShouldBe(5);
        last.NextCursor.ShouldBeNull("a page that returned every remaining row has no next page");
    }

    /// <summary>
    /// The boundary the over-fetch exists to get right, and the one no row-count/limit pair above reaches: a
    /// page that is simultaneously <b>full</b> (it returned exactly <see cref="AlvoQuery.Limit"/> rows) and
    /// <b>last</b> (nothing remains after it). The forbidden <c>Items.Count == Limit</c> derivation cannot
    /// tell those apart and mints a cursor here; the mandated <c>Limit + 1</c> over-fetch can, because it
    /// actually asked the store for one more row and got none back.
    /// </summary>
    [Fact]
    public async Task The_last_page_of_an_exactly_divisible_set_carries_no_cursor()
    {
        var world = await SeededWorldAsync(rowCount: 6);

        var first = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Limit = 3 }, world.Alice);
        first.Items.Count.ShouldBe(3);
        first.NextCursor.ShouldNotBeNull();

        var second = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Limit = 3, After = first.NextCursor },
            world.Alice);
        second.Items.Count.ShouldBe(3);
        second.NextCursor.ShouldBeNull(
            "the second page is full AND last: 'Items.Count == Limit' would mint a cursor here, and the "
            + "client's next request would come back empty");
    }

    /// <summary>
    /// The over-fetch is what makes <see cref="AlvoPage.NextCursor"/> honest across a whole walk: following
    /// the cursor the provider itself issued, page after page, visits every row exactly once — never
    /// skipping one at a boundary and never looping back to the start on an exactly-full final page.
    /// </summary>
    [Fact]
    public async Task Paging_the_whole_set_by_cursor_visits_every_row_exactly_once()
    {
        var world = await SeededWorldAsync(rowCount: 7);
        var seen = new List<object?>();
        string? cursor = null;

        do
        {
            var page = await world.Data.QueryAsync(
                new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Limit = 2, After = cursor },
                world.Alice);
            seen.AddRange(page.Items.Select(row => row["id"]));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        seen.Count.ShouldBe(7);
        seen.Distinct().Count().ShouldBe(7);
    }

    /// <summary>
    /// The exactly-divisible sibling of the walk above, where the forbidden derivation's failure mode is not
    /// a missed row but a phantom page: a full-and-last second page would still mint a cursor, so the walk
    /// would take a third, empty round trip before stopping. Every row is still visited exactly once either
    /// way — the row <em>count</em> does not discriminate the two derivations, only the page count does.
    /// </summary>
    [Fact]
    public async Task Walking_an_exactly_divisible_set_by_cursor_stops_after_the_full_last_page()
    {
        var world = await SeededWorldAsync(rowCount: 6);
        var seen = new List<object?>();
        var pages = 0;
        string? cursor = null;

        do
        {
            pages++;
            var page = await world.Data.QueryAsync(
                new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Limit = 3, After = cursor },
                world.Alice);
            seen.AddRange(page.Items.Select(row => row["id"]));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        seen.Count.ShouldBe(6);
        seen.Distinct().Count().ShouldBe(6);
        pages.ShouldBe(
            2,
            "the forbidden 'Items.Count == Limit' derivation would mint a cursor on the full last page, "
            + "adding a third, empty page to the walk");
    }

    /// <summary>
    /// The second paging mode: an offset skips a caller-chosen number of leading rows of the same order a
    /// cursor would have walked, so a page starting at offset 2 is exactly the tail of the unpaged read
    /// starting at index 2.
    /// </summary>
    [Fact]
    public async Task An_offset_page_skips_exactly_that_many_rows_of_the_same_order()
    {
        var world = await SeededWorldAsync(rowCount: 5);
        var all = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")] }, world.Alice);

        var skipped = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Limit = 2, Offset = 2 },
            world.Alice);

        skipped.Items.Select(row => row["id"]).ShouldBe(all.Items.Skip(2).Take(2).Select(row => row["id"]));
    }

    /// <summary>
    /// <see cref="AlvoQuery.After"/> and <see cref="AlvoQuery.Offset"/> anchor the same paging window two
    /// different ways, so a query naming both does not know which one it meant — refused rather than
    /// resolved in favour of whichever an implementation happens to check first.
    /// </summary>
    [Fact]
    public async Task A_query_asking_for_both_a_cursor_and_an_offset_is_refused_as_malformed()
    {
        var world = await SeededWorldAsync(rowCount: 3);

        var refusal = await Should.ThrowAsync<ArgumentException>(() => world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], After = "x", Offset = 1 },
            world.Alice));

        refusal.Message.ShouldContain("offset");
    }

    /// <summary>A negative offset is a malformed query, the same channel a negative limit is refused on.</summary>
    [Fact]
    public async Task A_negative_offset_is_refused_as_malformed()
    {
        var world = await SeededWorldAsync(rowCount: 3);

        await Should.ThrowAsync<ArgumentException>(() => world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Offset = -1 }, world.Alice));
    }

    /// <summary>
    /// A fresh, global <c>notes</c> entity with <paramref name="rowCount"/> rows, titled so a sort by
    /// <c>title</c> is deterministic and total.
    /// </summary>
    /// <param name="rowCount">The number of rows to seed.</param>
    private async Task<SeededWorld> SeededWorldAsync(int rowCount)
    {
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "paging-fixture",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                ["notes"] = new EntityDescriptor
                {
                    Tenancy = EntityTenancy.Global,
                    Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                    {
                        ["title"] = new() { Type = DescField.String, Required = true },
                    },
                    Rules = new AccessRules { List = "true", Get = "true" },
                },
            },
        };

        var schema = new SchemaModel([
            new EntitySchema
            {
                Name = "notes",
                Tenancy = TenancyMode.Global,
                Fields =
                [
                    new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                    new FieldSchema { Name = "title", Type = SchemaField.String, Required = true, MaxLength = 32 },
                ],
            },
        ]);

        var seed = Enumerable.Range(0, rowCount)
            .Select(index => new AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Guid.NewGuid(),
                ["title"] = $"row-{index:D4}",
            }))
            .ToList();

        var data = await CreateAsync(
            schema,
            descriptor,
            new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal) { ["notes"] = seed });

        return new SeededWorld(data, Caller);
    }

    /// <summary>One seeded <c>notes</c> database, plus the caller every paging fact above queries as.</summary>
    private sealed record SeededWorld(IAlvoData Data, AlvoContext Alice);

    private static AlvoContext Caller => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = null,
    };
}
