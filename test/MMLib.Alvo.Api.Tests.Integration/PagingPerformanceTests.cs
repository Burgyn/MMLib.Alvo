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
/// A third fact measures a property neither number is about — see
/// <see cref="A_sorted_keyset_walks_late_pages_do_not_cost_an_unbounded_multiple_of_its_early_pages"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both §2.1 facts are written against the way a performance test usually fails to mean anything.</b> A
/// latency assertion passes trivially on an empty or tiny table, so the seeded row count and the filter's own
/// match count are asserted before a single measurement is taken. A threshold assertion whose failure message
/// says only "false" cannot be acted on, so the measured p95 — with the median and the maximum beside it — is
/// in the message. And a stability assertion over a <em>static</em> table proves nothing at all: it is
/// satisfied by offset paging, which is precisely the scheme keyset paging exists to replace, so the walk
/// below runs against a table a concurrent writer is inserting into and updating throughout.
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

    /// <summary>Rows seeded for the depth-cost ratio fact — enough to separate early pages from late ones.</summary>
    private const int SortedWalkRowCount = 100_000;

    /// <summary>
    /// Distinct <c>make</c> values the ratio fact's seed carries, so the sort key has real cardinality — a
    /// two-value <c>make</c> (as the p95 fact uses) would not exercise the same shape the measurement behind
    /// issue #100 used.
    /// </summary>
    private const int SortedWalkDistinctMakes = 1_000;

    /// <summary>The fixed page size the depth-cost ratio walk uses; small enough for enough pages to bucket.</summary>
    private const int SortedWalkPageSize = 200;

    /// <summary>
    /// The bound the early/late page-cost ratio must stay under. <b>Not a target</b> — measured at 4.0x–6.9x
    /// over six local runs against the current, unrewritten renderer, so 20x carries roughly 3–5x headroom.
    /// See the fact's own remarks and issue #100, which records the rewrite that would tighten this.
    /// </summary>
    private const double SortedWalkRatioBound = 20d;

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    /// <summary>The engine and build configuration a reported number was measured under.</summary>
    private const string EngineDescription = "postgres:16-alpine, " + BuildConfiguration;

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
        (await world.CountRowsAsync("vehicles", "make", RareMake)).ShouldBe(
            FilteredRowCount / RareEvery, "the filter must match a real subset — neither nothing nor everything");

        var samples = await MeasureAsync(world, admin);
        await ReportAsync(
            $"§2.1 filtered-list p95 (budget {P95BudgetMs:F0}ms, {FilteredRowCount} rows, indexed column, "
            + $"{EngineDescription}): {Describe(samples)}; seeded in {seed.Elapsed.TotalSeconds:F1}s");

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
    /// <para>
    /// <b>The walk sorts by the default key (<c>id</c>) rather than by a declared column, deliberately.</b> A
    /// v4 <see cref="Guid"/> key means concurrent inserts land uniformly, which is what makes the offset
    /// substitution below actually lose rows rather than be theoretically worse. It also means the keyset
    /// predicate collapses to a single-term <c>id &gt; @cursor</c>, so this fact says nothing about a
    /// multi-term sort's cost — that is
    /// <see cref="A_sorted_keyset_walks_late_pages_do_not_cost_an_unbounded_multiple_of_its_early_pages"/>'s job.
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

        var walk = new Walk();
        var writer = new ConcurrentWriter(world, admin, seed, () => walk.Pages);
        await WalkWhileWritingAsync(world, admin, walk, writer);

        await ReportAsync(
            $"§2.1 keyset stability ({KeysetRowCount} rows, page size {PageSize}, {EngineDescription}): "
            + $"{walk.Describe()}; {writer.Describe()}; seeded in {seed.Elapsed.TotalSeconds:F1}s");
        walk.AssertStable(seed.Ids, writer);
        (await world.CountRowsAsync("vehicles")).ShouldBeGreaterThan(
            KeysetRowCount, "the walk must have run against a table that was still growing");
    }

    /// <summary>
    /// The depth-cost characteristic §2.1 does not name: on a keyset walk sorted by a declared column, a page
    /// near the end of the walk must not cost an unbounded multiple of a page near the start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neither of §2.1's two numbers is about this. Its 50 ms budget is scoped to a filtered list over
    /// 100 000 rows on an indexed column — <see cref="P95_of_a_filtered_list_over_100k_rows_on_an_indexed_column_is_under_50ms"/>
    /// already measures exactly that, and this fact must not be read as moving that budget onto a different
    /// shape. Its keyset criterion asks for stability over 1 000 000 rows, which
    /// <see cref="Keyset_paging_stays_stable_over_a_million_rows"/> proves. This fact exists because
    /// <c>KeysetSqlRenderer</c>'s nested-OR predicate is not sargable on a multi-term sort — PostgreSQL's
    /// planner falls back to an index scan plus a filter whose cost grows with cursor depth, even though
    /// neither §2.1 number is violated by it. It breaches keyset paging's own defining property (page N costs
    /// what page 1 costs) rather than a spec criterion, which is exactly how issue #100 — the full
    /// measurement, the <c>EXPLAIN</c> plans, the SQLite/T-SQL portability analysis and the fix shape — files
    /// it. This fact only guards against the ratio getting worse than it is today.
    /// </para>
    /// <para>
    /// <b>The bound is not a target.</b> It carries headroom over what the current, unrewritten renderer
    /// measures, so this stays green until #100's rewrite lands — at which point, per #100's own acceptance
    /// criteria, the bound tightens to become the assertion that proves the rewrite worked.
    /// </para>
    /// <para>
    /// The seed's <c>make</c> carries 1 000 distinct values rather than the p95 fact's two, so the walk's sort
    /// key has real cardinality — the shape issue #100's own measurement used — and the fixture's
    /// <c>(make, id)</c> index is what lets the planner use the sort at all; the descriptor's other index,
    /// <c>(make, model)</c>, does not cover <c>ORDER BY make, id</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_sorted_keyset_walks_late_pages_do_not_cost_an_unbounded_multiple_of_its_early_pages()
    {
        var admin = Admin();
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([admin], engine: _engine);
        await SeedAsync(world, admin, SortedWalkRowCount, index => $"make-{index % SortedWalkDistinctMakes:D4}");

        var pageLatenciesMs = await WalkTimingSortedByMakeAsync(world, admin);
        var tenth = pageLatenciesMs.Length / 10;
        var early = P95(pageLatenciesMs[..tenth]);
        var late = P95(pageLatenciesMs[^tenth..]);
        var ratio = late / early;
        await ReportAsync(
            $"depth-cost ratio (issue #100, not a §2.1 criterion — bound {SortedWalkRatioBound:F0}x, "
            + $"{SortedWalkRowCount} rows, page size {SortedWalkPageSize}, {EngineDescription}): "
            + $"early p95={early:F2}ms late p95={late:F2}ms ratio={ratio:F1}x over {pageLatenciesMs.Length} pages");

        ratio.ShouldBeLessThan(
            SortedWalkRatioBound,
            $"a keyset page's cost must not scale unboundedly with cursor depth; early p95 {early:F2}ms, late "
            + $"p95 {late:F2}ms, ratio {ratio:F1}x over {pageLatenciesMs.Length} pages. Issue #100 tracks the "
            + "nested-OR predicate that makes this ratio non-trivial today — the bound here has headroom over "
            + "the current measurement and is not a target to relax toward.");
    }

    /// <summary>
    /// Pages the whole visible set at a fixed size while <paramref name="writer"/> writes into
    /// <paramref name="walk"/>, and stops the writer as soon as the walk ends.
    /// </summary>
    private static async Task WalkWhileWritingAsync(
        AlvoApiWorld world, TestApiKey admin, Walk walk, ConcurrentWriter writer)
    {
        var writing = writer.RunAsync();
        try
        {
            await WalkAsync(world, admin, walk);
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
    private static async Task WalkAsync(AlvoApiWorld world, TestApiKey admin, Walk walk)
    {
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
    }

    /// <summary>
    /// Pages the sorted set at a fixed size, timing each page — the raw material for the depth-cost ratio
    /// fact. Sorted by <c>make</c>, so the cursor predicate carries the multi-term shape issue #100 measures.
    /// </summary>
    private static async Task<double[]> WalkTimingSortedByMakeAsync(AlvoApiWorld world, TestApiKey admin)
    {
        var latenciesMs = new List<double>();
        string? cursor = null;

        do
        {
            var path = $"/api/vehicles?select=id&order=make&limit={SortedWalkPageSize}"
                + (cursor is null ? string.Empty : $"&after={Uri.EscapeDataString(cursor)}");
            var stopwatch = Stopwatch.StartNew();
            using var response = await world.SendAsync(HttpMethod.Get, path, admin);
            latenciesMs.Add(stopwatch.Elapsed.TotalMilliseconds);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
            cursor = (await response.ReadJsonObjectAsync())["next"]?.GetValue<string>();
        }
        while (cursor is not null);

        return [.. latenciesMs];
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
        var ids = await ((PostgresApiDatabase)world.Database).CopyVehiclesAsync(owner, rowCount, make, Cancellation);
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

    private static async Task<Guid> CreateOwnerAsync(AlvoApiWorld world, TestApiKey admin)
    {
        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners", admin, body: new JsonObject { ["name"] = "Fleet Ltd" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private static string Make(int index) => index % RareEvery == 0 ? RareMake : CommonMake;

    private static TestApiKey Admin() => new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>The 95th percentile by nearest rank, which is what "p95 over N requests" names.</summary>
    private static double P95(double[] samples)
    {
        var sorted = samples.Order().ToArray();
        return sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1];
    }

    /// <summary>The median: the middle sample, or the mean of the two middle samples when the count is even.</summary>
    private static double Median(double[] sorted)
    {
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2d : sorted[middle];
    }

    /// <summary>Every number a reader needs to act on the result, not just the one that was compared.</summary>
    private static string Describe(double[] samples)
    {
        var sorted = samples.Order().ToArray();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"n={sorted.Length} p50={Median(sorted):F1}ms p95={P95(sorted):F1}ms max={sorted[^1]:F1}ms");
    }

    /// <summary>
    /// Writes a measured number three ways, since a criterion's value is the point of these facts and not
    /// only their verdict — and, measured, only one of the three is visible on a passing run.
    /// </summary>
    /// <remarks>
    /// <c>TestContext.Current.TestOutputHelper</c> output is not printed on a passing test under MTP, even
    /// with <c>-- --output Detailed</c>, so it is kept only as the source every failure message also quotes.
    /// <c>TestContext.Current.AddAttachment</c> <em>is</em> printed on a passing run, but the file lands in OS
    /// temp, which <c>--results-directory</c> does not collect — a good local affordance and not a durable
    /// one. The durable copy is the line <see cref="AppendCriterionAsync"/> appends to
    /// <c>artifacts/criteria/paging.md</c>, which <c>ci.yml</c> reads into the PR's own checks summary and
    /// uploads — the one copy a reader can compare across runs without re-running anything.
    /// </remarks>
    private static async Task ReportAsync(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        TestContext.Current.AddAttachment("paging-criteria", line);
        await AppendCriterionAsync(line);
    }

    /// <summary>
    /// Appends one measured line, timestamped, to <c>artifacts/criteria/paging.md</c> — already gitignored,
    /// and already the directory CI packs into — so §2.1's two numeric criteria, and the depth-cost ratio
    /// beside them, stay trackable for drift instead of surfacing only when one of them is violated.
    /// </summary>
    private static async Task AppendCriterionAsync(string line)
    {
        var path = Path.Combine(RepositoryRoot.Find(), "artifacts", "criteria", "paging.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, $"- {DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}", Cancellation);
    }

    /// <summary>What one bulk load produced.</summary>
    /// <param name="Ids">Every seeded row's id — the set present for the whole of a following walk.</param>
    /// <param name="Owner">The owner row every seeded vehicle references.</param>
    /// <param name="Elapsed">How long the load itself took, reported apart from any measurement.</param>
    private sealed record Seed(List<Guid> Ids, Guid Owner, TimeSpan Elapsed);

    /// <summary>
    /// The rows one walk visited, kept as a set so a repeat is caught as it happens rather than by a second
    /// pass over a million ids.
    /// </summary>
    private sealed class Walk
    {
        private readonly HashSet<Guid> _visited = [];
        private int _total;
        private Guid? _repeated;

        /// <summary>How many pages the walk has taken so far.</summary>
        /// <remarks>
        /// Read concurrently by <see cref="ConcurrentWriter"/> to record which page was in progress at each of
        /// its writes — the walk and the writer run on the same thread pool with no lock between them, but an
        /// <see langword="int"/> read/increment on one field is safe without one, and a stale-by-one read costs
        /// this fact nothing.
        /// </remarks>
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

        internal string Describe() => $"pages={Pages} rows={_total} distinct={_visited.Count}";

        /// <summary>
        /// The stability claim itself: no row twice, every row present for the whole run visited, and a writer
        /// that wrote <em>throughout</em> the walk rather than merely at some point during it — because a
        /// writer that stopped after one insert and one update would satisfy a bare "greater than zero" guard
        /// while the walk it ran against was, in every way that matters, the static-table version that offset
        /// paging also passes.
        /// </summary>
        /// <param name="seeded">Every id present before the writer started.</param>
        /// <param name="writer">The writer that ran alongside this walk.</param>
        internal void AssertStable(List<Guid> seeded, ConcurrentWriter writer)
        {
            writer.Failure.ShouldBeNull($"the concurrent writer must not have failed; {writer.Describe()}");

            _repeated.ShouldBeNull($"a keyset walk must never return one row twice; {Describe()}");
            Pages.ShouldBeGreaterThan(
                seeded.Count / (PageSize + 1), $"the walk must really have paged rather than read it all; {Describe()}");

            var missing = seeded.Count(id => !_visited.Contains(id));
            missing.ShouldBe(
                0,
                $"every row present for the whole walk must be visited exactly once; {missing} of {seeded.Count} "
                + "were missed. This is the assertion an 'offset' page fails under concurrent inserts. "
                + Describe());

            var minimumWrites = Pages / 4;
            writer.Inserted.ShouldBeGreaterThan(
                minimumWrites,
                $"the writer must have inserted throughout the walk, not once at the start; {writer.Describe()}");
            writer.Updated.ShouldBeGreaterThan(
                minimumWrites, $"and updated throughout it; {writer.Describe()}");

            var minimumLastWritePage = Pages / 2;
            writer.LastInsertPage.ShouldBeGreaterThan(
                minimumLastWritePage,
                "the writer's last successful insert must have landed past the walk's midpoint, or every write "
                + $"could have happened before page 2 while the count above still passed; {writer.Describe()}");
            writer.LastUpdatePage.ShouldBeGreaterThan(
                minimumLastWritePage,
                "and likewise for its last update, or an idle writer after an early burst would satisfy the "
                + $"count above too; {writer.Describe()}");
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
    /// nothing. <see cref="Walk.AssertStable"/> checks the failure and every count.
    /// </para>
    /// <para>
    /// <paramref name="pagesSoFar"/> is how this writer records <em>when</em>, not only <em>whether</em>, it
    /// wrote: <see cref="LastInsertPage"/> and <see cref="LastUpdatePage"/> let
    /// <see cref="Walk.AssertStable"/> tell "884 writes spread over 501 pages" from "884 writes before page 2".
    /// </para>
    /// </remarks>
    private sealed class ConcurrentWriter(AlvoApiWorld world, TestApiKey admin, Seed seed, Func<int> pagesSoFar)
    {
        private volatile bool _stopped;
        private int _serial;

        /// <summary>How many rows the writer created while the walk ran.</summary>
        internal int Inserted { get; private set; }

        /// <summary>How many existing rows it updated.</summary>
        internal int Updated { get; private set; }

        /// <summary>The walk's own page count at the writer's last successful insert.</summary>
        internal int LastInsertPage { get; private set; }

        /// <summary>The walk's own page count at the writer's last successful update.</summary>
        internal int LastUpdatePage { get; private set; }

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

        internal string Describe() =>
            $"inserted={Inserted} (last at page {LastInsertPage}) updated={Updated} (last at page "
            + $"{LastUpdatePage}) failure={Failure ?? "none"}";

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
                LastInsertPage = pagesSoFar();
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
                LastUpdatePage = pagesSoFar();
                return;
            }

            Failure ??= $"update answered {(int)response.StatusCode}: {await response.ReadTextAsync()}";
        }
    }
}
