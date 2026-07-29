using MMLib.Alvo.Api.Tests;
using Npgsql;
using NpgsqlTypes;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace MMLib.Alvo.Api.Tests.Integration;

/// <summary>
/// §2.1's two <b>numeric</b> acceptance criteria, measured over the real HTTP Data API on real PostgreSQL —
/// the only quantitative criteria in the whole milestone:
/// <list type="bullet">
///   <item>p95 of a filtered list over 100 000 rows on an indexed column under 50 ms locally.</item>
///   <item>Keyset pagination stable over 1 000 000 rows.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Both facts are written against the way a performance test usually fails to mean anything.</b> A latency
/// assertion passes trivially on an empty or tiny table, so the seeded row count and the filter's own match
/// count are asserted before a single measurement is taken. A threshold assertion whose failure message says
/// only "false" cannot be acted on, so the measured p95 — with the median and the maximum beside it — is in the
/// message. And a stability assertion over a <em>static</em> table proves nothing at all: it is satisfied by
/// offset paging, which is precisely the scheme keyset paging exists to replace, so the walk below runs against
/// a table a concurrent writer is inserting into and updating throughout.
/// </para>
/// <para>
/// <b>The seed goes through <c>COPY</c>, not through the API.</b> Creating a million rows one request at a time
/// is the test rather than the setup — hours of it. The risk that trades against, and the reason
/// <c>AlvoDataSeed</c> exists for the port suites, is that a hand-written seed can store a value the production
/// path would never write and that no query then matches. Two things hold it off here: the values go over
/// Npgsql's binary <c>COPY</c> as their CLR types (no hand-formatted <see cref="Guid"/>, no invariant date
/// text), and every seeded batch is read back <em>through the API by id</em> before anything is measured, so a
/// representation the read path cannot match fails in the setup rather than flattering the result.
/// </para>
/// <para>
/// Seeding time is reported separately from the measurement, because it is setup cost and folding it in would
/// make the number mean something other than what the criterion says.
/// </para>
/// <para>
/// <b>Not gated behind an opt-in, and that was a decision rather than an omission.</b> The task allowed moving
/// these two to a CI job of their own if they could not hold ring2 to a sane duration; measured, they do — 6 s
/// and 20 s locally, seeding included, because the load goes over <c>COPY</c> and the walk projects a single
/// column. A separate job would have put §2.1's only numeric criteria outside the one check that blocks a
/// merge (the branch ruleset requires "Build &amp; test" and would not require a new job), which is a worse
/// trade than half a minute of ring2. If they ever grow past a minute or two, gate them then — and add the job
/// to the ruleset in the same change.
/// </para>
/// </remarks>
public sealed class PagingPerformanceTests : IAsyncLifetime
{
    private const int FilteredRowCount = 100_000;
    private const int KeysetRowCount = 1_000_000;

    /// <summary>One in fifty seeded rows carries the filtered make, so the filter matches a non-trivial subset.</summary>
    private const int RareEvery = 50;

    private const string RareMake = "rare";
    private const string CommonMake = "common";

    /// <summary>The criterion's own number, in milliseconds.</summary>
    private const double P95BudgetMs = 50d;

    /// <summary>Requests measured, and the warm-up that precedes them.</summary>
    /// <remarks>
    /// 200 is the floor the task sets, and it is also what makes a p95 mean anything: at 20 samples the 95th
    /// percentile is the second-worst observation, which on a shared machine is whatever the OS was doing at
    /// the time.
    /// </remarks>
    private const int MeasuredRequests = 200;

    private const int WarmupRequests = 20;

    /// <summary>The page size the filtered list asks for, and the page every sample must come back full.</summary>
    private const int FilteredPageSize = 50;

    /// <summary>The fixed page size the million-row walk uses.</summary>
    /// <remarks>
    /// Fixed, because a walk whose page size drifted would not be paging one query. 2 000 keeps the walk to
    /// roughly 500 round trips — enough interleaving for a concurrent writer to matter, few enough that the
    /// test is minutes rather than hours.
    /// </remarks>
    private const int PageSize = 2_000;

    private readonly PostgresApiEngine _engine = new();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _engine.InitializeAsync();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _engine.DisposeAsync();

    /// <summary>
    /// §2.1: <b>p95 of a filtered list over 100 000 rows on an indexed column is under 50 ms locally.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filter is <c>make</c>, the leading column of the descriptor's own <c>(make, model)</c> index, so
    /// "on an indexed column" is a property of the fixture rather than a hope. It matches 2 000 of the 100 000
    /// rows — asserted, both halves: a filter matching everything would measure an unfiltered read, and one
    /// matching nothing would measure an empty result set, which is the way this fact would most plausibly
    /// pass while proving nothing.
    /// </para>
    /// <para>
    /// The measurement brackets <c>SendAsync</c>, so it includes the in-process client round trip and this
    /// suite's global response screen as well as the server's own work. That makes it an <em>upper bound</em>
    /// on the number the criterion is about, which is the direction that cannot flatter the result.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task P95_of_a_filtered_list_over_100k_rows_on_an_indexed_column_is_under_50ms()
    {
        var admin = Admin();
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([admin], engine: _engine);
        var seed = await SeedAsync(world, admin, FilteredRowCount, Make);

        (await world.CountRowsAsync("vehicles")).ShouldBe(
            FilteredRowCount, "the criterion is about 100 000 rows, and a latency budget is trivial on fewer");
        (await world.CountRowsAsync("vehicles", $"make = '{RareMake}'")).ShouldBe(
            FilteredRowCount / RareEvery, "the filter must match a real subset — neither nothing nor everything");

        var samples = await MeasureAsync(world, admin);
        Report($"filtered list: {Describe(samples)}; seeding {FilteredRowCount} rows took {seed.Elapsed.TotalSeconds:F1}s");

        P95(samples).ShouldBeLessThan(
            P95BudgetMs,
            $"§2.1 budgets 50 ms for the p95 of a filtered list over {FilteredRowCount} rows on an indexed "
            + $"column. Measured {Describe(samples)}. Seeding took {seed.Elapsed.TotalSeconds:F1}s (not included).");
    }

    /// <summary>
    /// §2.1: <b>keyset pagination is stable over 1 000 000 rows</b> — no row visited twice, and no row present
    /// for the whole walk missed, <em>while a concurrent writer inserts and updates rows</em>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The concurrent writer is the fact.</b> Paging a static million-row table proves only that a loop
    /// terminates: offset paging passes that version perfectly, and offset paging is exactly what
    /// <c>AlvoQuery.After</c> exists instead of. Under concurrent inserts the two come apart — every row
    /// inserted <em>before</em> the current window shifts an offset's meaning, so an <c>offset</c>-paged walk
    /// skips a row for each such insert while a keyset walk anchored on the row key does not notice.
    /// Substituting <c>offset={rows so far}</c> for <c>after={cursor}</c> below fails this fact, and that
    /// substitution is the discriminating check.
    /// </para>
    /// <para>
    /// The writer touches <c>color</c> and never a key: an update that moved a row's <em>sort key</em> would
    /// make "no row is missed" false for any correct pager, keyset or otherwise, so it would be a fact about
    /// nothing. Inserts, by contrast, land at uniformly distributed row keys (a v4 <see cref="Guid"/>), so
    /// roughly half of them land behind the cursor — which is what makes the offset substitution fail rather
    /// than merely be theoretically worse.
    /// </para>
    /// <para>
    /// Rows the writer inserts <em>may or may not</em> be visited, and the fact does not claim otherwise: a
    /// keyset walk sees an inserted row exactly when its key sorts after the cursor. The claim is over the rows
    /// present for the whole run, which is the million that were there before the writer started.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Keyset_paging_stays_stable_over_a_million_rows()
    {
        var admin = Admin();
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [admin], new AlvoApiWorldSetup(api => api.MaxPageSize = PageSize), _engine);
        var seed = await SeedAsync(world, admin, KeysetRowCount, _ => CommonMake);
        (await world.CountRowsAsync("vehicles")).ShouldBe(KeysetRowCount, "the criterion is about a million rows");

        var writer = new ConcurrentWriter(world, admin, seed);
        var walk = await WalkWhileWritingAsync(world, admin, writer);

        Report($"keyset walk: {walk.Describe()}; seeding {KeysetRowCount} rows took {seed.Elapsed.TotalSeconds:F1}s");
        walk.AssertStable(seed.Ids);
        (await world.CountRowsAsync("vehicles")).ShouldBeGreaterThan(
            KeysetRowCount, "the walk must have run against a table that was still growing");
    }

    /// <summary>
    /// Pages the whole visible set at a fixed size while <paramref name="writer"/> writes, and stops the
    /// writer as soon as the walk ends.
    /// </summary>
    private static async Task<Walk> WalkWhileWritingAsync(AlvoApiWorld world, TestApiKey admin, ConcurrentWriter writer)
    {
        var writing = writer.RunAsync();
        try
        {
            return await WalkAsync(world, admin, writer);
        }
        finally
        {
            writer.Stop();
            await writing;
        }
    }

    /// <summary>Follows the cursor the API itself issued to exhaustion, recording every id it hands back.</summary>
    /// <remarks>
    /// <c>select=id</c> because the walk asserts on ids alone, and a million rows of full records would spend
    /// the run in JSON rather than in paging. The projection does not weaken the fact: the cursor is anchored
    /// on the row key, which is exactly what is projected.
    /// </remarks>
    private static async Task<Walk> WalkAsync(AlvoApiWorld world, TestApiKey admin, ConcurrentWriter writer)
    {
        var walk = new Walk(writer);
        string? cursor = null;

        do
        {
            var path = $"/api/vehicles?select=id&limit={PageSize}"
                + (cursor is null ? string.Empty : $"&after={Uri.EscapeDataString(cursor)}");
            using var response = await world.SendAsync(HttpMethod.Get, path, admin);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
            walk.Add(await response.ReadItemsAsync());
            cursor = (await response.ReadJsonObjectAsync())["next"]?.GetValue<string>();
        }
        while (cursor is not null);

        return walk;
    }

    /// <summary>
    /// Times <see cref="MeasuredRequests"/> filtered list requests after a warm-up, and returns every sample
    /// in milliseconds.
    /// </summary>
    /// <remarks>
    /// The warm-up is not decoration: the first request pays EF's model build, the SQL composition, the policy
    /// compilation and Npgsql's first physical connection, which are one-off costs a p95 over a warm process is
    /// not about. Every response is checked for a full page, so a request that started failing — or quietly
    /// returning nothing — cannot be measured as a fast one.
    /// </remarks>
    private static async Task<double[]> MeasureAsync(AlvoApiWorld world, TestApiKey admin)
    {
        foreach (var _ in Enumerable.Range(0, WarmupRequests))
        {
            await FilteredPageAsync(world, admin);
        }

        var samples = new double[MeasuredRequests];
        foreach (var index in Enumerable.Range(0, MeasuredRequests))
        {
            samples[index] = await FilteredPageAsync(world, admin);
        }

        return samples;
    }

    /// <summary>One filtered list request, timed, with its page asserted full so a fast refusal cannot count.</summary>
    private static async Task<double> FilteredPageAsync(AlvoApiWorld world, TestApiKey admin)
    {
        var stopwatch = Stopwatch.StartNew();
        using var response = await world.SendAsync(
            HttpMethod.Get, $"/api/vehicles?make=eq.{RareMake}&order=make&limit={FilteredPageSize}", admin);
        var elapsed = stopwatch.Elapsed.TotalMilliseconds;

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        (await response.ReadItemsAsync()).Count.ShouldBe(
            FilteredPageSize, "a request that returned no rows is not the read this criterion is about");
        return elapsed;
    }

    /// <summary>
    /// Bulk-loads <paramref name="rowCount"/> vehicles and proves the load is readable through the API.
    /// </summary>
    /// <param name="world">The world whose database is seeded.</param>
    /// <param name="admin">The caller the owner row and the read-back are made as.</param>
    /// <param name="rowCount">How many vehicles to load.</param>
    /// <param name="make">The <c>make</c> for a given row index.</param>
    private static async Task<Seed> SeedAsync(
        AlvoApiWorld world, TestApiKey admin, int rowCount, Func<int, string> make)
    {
        var owner = await CreateOwnerAsync(world, admin);
        var stopwatch = Stopwatch.StartNew();
        var ids = await CopyVehiclesAsync((PostgresApiDatabase)world.Database, owner, rowCount, make);
        var elapsed = stopwatch.Elapsed;

        await EnsureSeedIsReadableAsync(world, admin, ids[^1]);
        return new Seed(ids, owner, elapsed);
    }

    /// <summary>
    /// Reads one seeded row back <b>through the API, by id</b>, before anything is measured.
    /// </summary>
    /// <remarks>
    /// This is the guard that earns the right to bypass the write path. A seed that stored a value the read
    /// path cannot match — the classic being a hand-formatted <see cref="Guid"/> — would otherwise leave every
    /// query below fast and every result empty, and the latency criterion would be met by a table nothing can
    /// read.
    /// </remarks>
    private static async Task EnsureSeedIsReadableAsync(AlvoApiWorld world, TestApiKey admin, Guid id)
    {
        using var response = await world.SendAsync(HttpMethod.Get, $"/api/vehicles/{id}", admin);
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "a bulk-seeded row must be readable through the API by its own id, or the seed stored a "
            + $"representation the read path cannot match: {await response.ReadTextAsync()}");
        (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>().ShouldBe(id);
    }

    /// <summary>
    /// Loads the vehicles over Npgsql's binary <c>COPY</c> — the one place this suite speaks PostgreSQL rather
    /// than SQL a portable <c>DbCommand</c> could carry.
    /// </summary>
    /// <remarks>
    /// Binary rather than text: every value crosses as its CLR type, so no <see cref="Guid"/>, timestamp or
    /// integer is ever formatted into text and re-parsed — which is where a hand-rolled seed diverges from what
    /// the production write path stores. The nullable managed columns (<c>created_by</c>, <c>updated_by</c>) and
    /// the nullable <c>color</c> are left out of the column list rather than written as null, so the table
    /// itself supplies them.
    /// </remarks>
    private static async Task<List<Guid>> CopyVehiclesAsync(
        PostgresApiDatabase database, Guid owner, int rowCount, Func<int, string> make)
    {
        var ids = new List<Guid>(rowCount);
        var stamped = DateTimeOffset.UtcNow;
        await using var connection = database.ConnectToPostgres();
        await connection.OpenAsync(Cancellation);
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY vehicles (id, created_at, updated_at, vin, plate, make, model, year, owner_id) "
            + "FROM STDIN (FORMAT BINARY)",
            Cancellation);

        foreach (var index in Enumerable.Range(0, rowCount))
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await WriteVehicleAsync(writer, id, owner, index, make(index), stamped);
        }

        await writer.CompleteAsync(Cancellation);
        return ids;
    }

    private static async Task WriteVehicleAsync(
        NpgsqlBinaryImporter writer, Guid id, Guid owner, int index, string make, DateTimeOffset stamped)
    {
        await writer.StartRowAsync(Cancellation);
        await writer.WriteAsync(id, NpgsqlDbType.Uuid, Cancellation);
        await writer.WriteAsync(stamped, NpgsqlDbType.TimestampTz, Cancellation);
        await writer.WriteAsync(stamped, NpgsqlDbType.TimestampTz, Cancellation);
        await writer.WriteAsync($"VIN{index:D14}", NpgsqlDbType.Varchar, Cancellation);
        await writer.WriteAsync($"S-{index:D9}", NpgsqlDbType.Varchar, Cancellation);
        await writer.WriteAsync(make, NpgsqlDbType.Varchar, Cancellation);
        await writer.WriteAsync("model", NpgsqlDbType.Varchar, Cancellation);

        // An `integer` descriptor field maps to a PostgreSQL `bigint`, so the operand is a long — a mismatch
        // here is a COPY-time failure rather than a silent conversion.
        await writer.WriteAsync((long)(1990 + (index % 30)), NpgsqlDbType.Bigint, Cancellation);
        await writer.WriteAsync(owner, NpgsqlDbType.Uuid, Cancellation);
    }

    private static async Task<Guid> CreateOwnerAsync(AlvoApiWorld world, TestApiKey admin)
    {
        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners", admin, body: new JsonObject { ["name"] = "Fleet Ltd" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private static string Make(int index) => index % RareEvery == 0 ? RareMake : CommonMake;

    private static TestApiKey Admin() => new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>The 95th percentile by nearest rank, which is what "p95 over 200 requests" names.</summary>
    private static double P95(double[] samples)
    {
        var sorted = samples.Order().ToArray();
        return sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1];
    }

    /// <summary>Every number a reader needs to act on the result, not just the one that was compared.</summary>
    private static string Describe(double[] samples)
    {
        var sorted = samples.Order().ToArray();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"n={sorted.Length} p50={sorted[sorted.Length / 2]:F1}ms p95={P95(sorted):F1}ms max={sorted[^1]:F1}ms");
    }

    /// <summary>
    /// Writes a measured number where a passing run can still be read, since a criterion's value is the point
    /// of the fact and not only its verdict. The measured numbers are also in every failure message.
    /// </summary>
    private static void Report(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>What one bulk load produced.</summary>
    /// <param name="Ids">Every seeded row's id — the set present for the whole of a following walk.</param>
    /// <param name="Owner">The owner row every seeded vehicle references.</param>
    /// <param name="Elapsed">How long the load itself took, reported apart from any measurement.</param>
    private sealed record Seed(List<Guid> Ids, Guid Owner, TimeSpan Elapsed);

    /// <summary>
    /// The rows one walk visited, kept as a set so a repeat is caught as it happens rather than by a second
    /// pass over a million ids.
    /// </summary>
    private sealed class Walk(ConcurrentWriter writer)
    {
        private readonly HashSet<Guid> _visited = [];
        private int _total;
        private Guid? _repeated;

        /// <summary>How many pages the walk took.</summary>
        internal int Pages { get; private set; }

        internal void Add(IReadOnlyList<JsonObject> items)
        {
            Pages++;
            foreach (var id in items.Select(item => item["id"]!.GetValue<Guid>()))
            {
                _total++;
                if (!_visited.Add(id))
                {
                    _repeated ??= id;
                }
            }
        }

        internal string Describe() =>
            $"pages={Pages} rows={_total} distinct={_visited.Count} {writer.Describe()}";

        /// <summary>
        /// The stability claim itself: no row twice, every row present for the whole run visited, and a writer
        /// that actually wrote — because with an idle writer this is the static-table version that offset
        /// paging also passes.
        /// </summary>
        /// <param name="seeded">Every id present before the writer started.</param>
        internal void AssertStable(List<Guid> seeded)
        {
            _repeated.ShouldBeNull($"a keyset walk must never return one row twice; {Describe()}");
            _total.ShouldBe(_visited.Count, $"and the totals must agree; {Describe()}");
            Pages.ShouldBeGreaterThan(
                seeded.Count / (PageSize + 1), $"the walk must really have paged rather than read it all; {Describe()}");

            var missing = seeded.Count(id => !_visited.Contains(id));
            missing.ShouldBe(
                0,
                $"every row present for the whole walk must be visited exactly once; {missing} of {seeded.Count} "
                + "were missed. This is the assertion an 'offset' page fails under concurrent inserts. "
                + Describe());

            writer.Failure.ShouldBeNull($"the concurrent writer must not have failed; {Describe()}");
            writer.Inserted.ShouldBeGreaterThan(0, "the walk must really have run against concurrent inserts");
            writer.Updated.ShouldBeGreaterThan(0, "and against concurrent updates");
        }
    }

    /// <summary>
    /// A second caller writing through the <b>production HTTP path</b> for as long as the walk lasts: new
    /// vehicles, and colour changes on rows the walk expects to see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Over HTTP rather than over SQL, so the concurrency is the concurrency a deployed Alvo actually meets —
    /// two callers, one host, one connection pool, real transactions.
    /// </para>
    /// <para>
    /// It records the <em>first</em> failure rather than swallowing errors, because a writer that silently
    /// stopped writing would turn this into the static-table version of the fact — the version that proves
    /// nothing. <see cref="Walk.AssertStable"/> checks the failure and both counts.
    /// </para>
    /// </remarks>
    private sealed class ConcurrentWriter(AlvoApiWorld world, TestApiKey admin, Seed seed)
    {
        private volatile bool _stopped;
        private int _serial;

        /// <summary>How many rows the writer created while the walk ran.</summary>
        internal int Inserted { get; private set; }

        /// <summary>How many existing rows it updated.</summary>
        internal int Updated { get; private set; }

        /// <summary>The first failure it met, or <see langword="null"/> when it met none.</summary>
        internal string? Failure { get; private set; }

        internal async Task RunAsync()
        {
            // Yield first, so the caller gets to start its walk instead of running the writer inline.
            await Task.Yield();
            while (!_stopped && Failure is null)
            {
                await InsertAsync();
                await UpdateAsync();
            }
        }

        internal void Stop() => _stopped = true;

        internal string Describe() => $"inserted={Inserted} updated={Updated} failure={Failure ?? "none"}";

        /// <summary>
        /// Creates a row at a uniformly distributed row key, which is what makes the insert land behind the
        /// walk's cursor about half the time — the case an offset page loses a row to.
        /// </summary>
        private async Task InsertAsync()
        {
            var serial = _serial++;
            using var response = await world.SendAsync(HttpMethod.Post, "/api/vehicles", admin, body: new JsonObject
            {
                ["vin"] = $"WRT{serial:D14}",
                ["plate"] = $"W-{serial:D9}",
                ["make"] = "written",
                ["model"] = "model",
                ["year"] = 2026,
                ["owner_id"] = seed.Owner.ToString(),
            });

            if (response.StatusCode == HttpStatusCode.Created)
            {
                Inserted++;
                return;
            }

            Failure ??= $"insert answered {(int)response.StatusCode}: {await response.ReadTextAsync()}";
        }

        /// <summary>
        /// Updates <c>color</c> — never a key. An update that moved a row's sort key would make "no row is
        /// missed" false for any pager, so it would be a fact about nothing rather than a harder fact.
        /// </summary>
        private async Task UpdateAsync()
        {
            var id = seed.Ids[Random.Shared.Next(seed.Ids.Count)];
            using var response = await world.SendAsync(
                HttpMethod.Patch, $"/api/vehicles/{id}", admin,
                body: new JsonObject { ["color"] = $"c{Updated % 1000}" });

            if (response.StatusCode == HttpStatusCode.OK)
            {
                Updated++;
                return;
            }

            Failure ??= $"update answered {(int)response.StatusCode}: {await response.ReadTextAsync()}";
        }
    }
}
