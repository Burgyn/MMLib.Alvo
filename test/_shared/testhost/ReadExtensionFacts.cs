using CsCheck;

using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Reads;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;

using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

[AlvoEntity("notes")]
public sealed class ReadsNote
{
    public Guid Id { get; init; }

    public required string Title { get; init; }

    public Guid OwnerId { get; init; }

    public string? Region { get; init; }
}

/// <summary>
/// Query narrowing on a real engine: ANDed into the SQL of list, get and count for every caller but the system, inside a
/// unit too, never widening what the rules allow, never narrowing a write — and failing closed.
/// </summary>
public abstract class ReadExtensionFacts
{
    private const string Notes = "notes";

    private static readonly Gen<string> _hostile = Gen.Char["abcXYZ01_ '\"%;-()\\é中"].Array[1, 24].Select(characters => new string(characters));

    private readonly Guid _tenant = Guid.NewGuid();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> WideningAttempts => ["everyone", "the-other-owner", "or-true"];

    [Fact]
    public async Task A_narrowing_is_anded_into_the_list_get_and_count_statements_and_changes_the_count()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Notes, n => n.Narrow(
            "north-only", (QueryNarrowing _) => new AlvoComparison("region", AlvoFilterOperator.Eq, "north"))));
        var clerk = Clerk();
        var north = await CreateAsync(alvo, clerk, "a", "north");
        var south = await CreateAsync(alvo, clerk, "b", "south");
        using var capture = new SqlCapture(database.Marker);

        var page = await alvo.Data.QueryAsync(new AlvoQuery { Entity = Notes, IncludeTotalCount = true }, clerk, Ct);
        var hidden = await alvo.Data.GetAsync(Notes, south, clerk, Ct);

        page.TotalCount.ShouldBe(1L);
        page.Items.ShouldHaveSingleItem()[AlvoManagedColumns.Id].ShouldBe(north);
        hidden.ShouldBeNull();
        Narrowed(capture).ShouldBe(3, "page, count and get each carry the narrowing");
    }

    [Theory]
    [MemberData(nameof(WideningAttempts))]
    public async Task A_narrowing_never_returns_a_row_the_caller_s_rules_hide(string attempt)
    {
        var database = await DatabaseAsync();
        var other = Clerk();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Notes, n => n.Narrow(attempt, (QueryNarrowing _) => Widening(attempt, other))));
        var clerk = Clerk();
        await CreateAsync(alvo, other, "theirs", "north");
        using var capture = new SqlCapture(database.Marker);

        var page = await alvo.Data.QueryAsync(new AlvoQuery { Entity = Notes, IncludeTotalCount = true }, clerk, Ct);

        (page.Items.Count, page.TotalCount).ShouldBe((0, (long?)0L));
        Narrowed(capture).ShouldBe(2, "the page and the count carry the narrowing — a silently skipped narrowing fails here, not only the row count");
    }

    [Fact]
    public async Task A_handler_s_system_session_reads_are_not_narrowed()
    {
        var seen = new ConcurrentQueue<long?>();
        await using var alvo = await StartAsync(await DatabaseAsync(), alvo => alvo.Entity(Notes, n => n
            .Narrow("nowhere", (QueryNarrowing _) => new AlvoComparison("region", AlvoFilterOperator.Eq, "nowhere"))
            .OnCreated("count-notes", (AlvoCreated<AlvoRecord> _, AlvoEventContext context, CancellationToken token) => CountAsync(context, seen, token), when: "new.title == 'b'")));
        var clerk = Clerk();
        await CreateAsync(alvo, clerk, "a", "north");
        await CreateAsync(alvo, clerk, "b", "south");

        await alvo.DrainEventsAsync(Ct);

        seen.ShouldHaveSingleItem().ShouldBe(2L);
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = Notes }, clerk, Ct)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_read_inside_a_unit_is_narrowed_for_its_caller()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), NowhereOnly);
        var clerk = Clerk();
        await CreateAsync(alvo, clerk, "a", "north");

        var seen = await alvo.Services.GetRequiredService<IAlvoTransactions>().TransactAsync(
            clerk, async (unit, token) => (await unit.QueryAsync(new AlvoQuery { Entity = Notes }, token)).Items.Count, cancellationToken: Ct);

        seen.ShouldBe(0);
    }

    [Fact]
    public async Task AsSystem_reads_inside_a_unit_are_not_narrowed_but_the_caller_s_are()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), NowhereOnly);
        var clerk = Clerk();
        await CreateAsync(alvo, clerk, "a", "north");
        await CreateAsync(alvo, clerk, "b", "south");

        var (asCaller, asSystem) = await alvo.Services.GetRequiredService<IAlvoTransactions>().TransactAsync(
            clerk,
            async (unit, token) => (
                (await unit.QueryAsync(new AlvoQuery { Entity = Notes }, token)).Items.Count,
                (await unit.AsSystem().QueryAsync(new AlvoQuery { Entity = Notes }, token)).Items.Count),
            cancellationToken: Ct);

        (asCaller, asSystem).ShouldBe((0, 2));
    }

    [Fact]
    public async Task A_cursor_whose_anchor_the_narrowing_excludes_answers_an_empty_page()
    {
        var region = new RegionSwitch("north");
        await using var alvo = await StartAsync(await DatabaseAsync(), alvo => alvo.Entity(Notes, n => n.Narrow(
            "switched", (QueryNarrowing _) => new AlvoComparison("region", AlvoFilterOperator.Eq, region.Value))));
        var clerk = Clerk();
        foreach (var (title, where) in new[] { ("a", "north"), ("b", "north"), ("c", "north"), ("d", "south") })
        {
            await CreateAsync(alvo, clerk, title, where);
        }

        var first = await alvo.Data.QueryAsync(new AlvoQuery { Entity = Notes, Limit = 1 }, clerk, Ct);
        region.Value = "south";
        var second = await alvo.Data.QueryAsync(new AlvoQuery { Entity = Notes, Limit = 1, After = first.NextCursor, IncludeTotalCount = true }, clerk, Ct);

        first.NextCursor.ShouldNotBeNull();
        second.Items.ShouldBeEmpty("the anchor is read under the narrowing, so a cursor into rows it now excludes opens no window");
        second.TotalCount.ShouldBe(1L);
    }

    [Fact]
    public async Task Writes_are_not_narrowed_but_their_echo_is()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), alvo => alvo.Entity(Notes, n => n.Narrow(
            "north-only", (QueryNarrowing _) => new AlvoComparison("region", AlvoFilterOperator.Eq, "north"))));
        var owner = Clerk();
        var south = await CreateAsync(alvo, owner, "b", "south");

        var updated = await alvo.Data.UpdateAsync(Notes, south, new Dictionary<string, object?> { ["title"] = "renamed" }, owner, cancellationToken: Ct);
        await alvo.Data.DeleteAsync(Notes, south, owner, cancellationToken: Ct);

        updated.Values.Keys.ShouldBe([AlvoManagedColumns.Id], "a narrowed-out echo answers the id alone, as a get-excluded one does");
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = Notes }, AlvoContext.System(owner.Tenant), Ct)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_narrowing_inside_a_unit_poisons_it()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), alvo => alvo.Entity(Notes, n => n.Narrow(
            "list-breaker", (QueryNarrowing query) => query.Operation == DataOperation.List ? throw new InvalidOperationException("host bug") : null)));
        var clerk = Clerk();

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => alvo.Services.GetRequiredService<IAlvoTransactions>().TransactAsync(
            clerk,
            async (unit, token) =>
            {
                await Should.ThrowAsync<InvalidOperationException>(() => unit.QueryAsync(new AlvoQuery { Entity = Notes }, token));
                await unit.CreateAsync(Notes, NoteValues(clerk, "kept?", "north"), token);
            },
            cancellationToken: Ct));

        failure.Message.ShouldContain("read extension 'list-breaker' failed (exception)");
        failure.Message.ShouldNotContain("host bug");
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = Notes }, AlvoContext.System(clerk.Tenant), Ct)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_captured_narrowing_value_reaches_the_sql_text()
    {
        var database = await DatabaseAsync();
        var captured = new RegionSwitch("benign");
        await using var alvo = await StartAsync(database, alvo => alvo.Entity<ReadsNote>(n => n.Narrow("cap", _ => note => note.Region == captured.Value)));
        var clerk = Clerk();
        using var capture = new SqlCapture(database.Marker);
        var baseline = await StatementsAsync(alvo, clerk, capture);
        var sampled = 0;

        await _hostile.SampleAsync(
            async value =>
            {
                captured.Value = value;
                (await StatementsAsync(alvo, clerk, capture)).ShouldBe(baseline, $"captured {value}");
                Interlocked.Increment(ref sampled);
            },
            iter: 50,
            threads: 1);

        baseline.ShouldNotBeEmpty();
        sampled.ShouldBe(50);
    }

    protected abstract Task<ReadsDatabase> DatabaseAsync();

    private static void NowhereOnly(IAlvoBuilder alvo) => alvo.Entity(Notes, n => n.Narrow(
        "nowhere", (QueryNarrowing _) => new AlvoComparison("region", AlvoFilterOperator.Eq, "nowhere")));

    private static AlvoFilter Widening(string attempt, AlvoContext other) => attempt switch
    {
        "everyone" => new AlvoComparison("id", AlvoFilterOperator.Neq, Guid.Empty),
        "the-other-owner" => new AlvoComparison("owner_id", AlvoFilterOperator.Eq, other.User.Value),
        _ => new AlvoOr([new AlvoComparison("region", AlvoFilterOperator.Eq, "x"), new AlvoNot(new AlvoComparison("region", AlvoFilterOperator.Eq, "x"))]),
    };

    private static int Narrowed(SqlCapture capture) =>
        capture.Statements.Count(sql => sql.Contains("\"notes\"", StringComparison.Ordinal) && sql.Contains("alvo_n0", StringComparison.Ordinal));

    private static async Task CountAsync(AlvoEventContext context, ConcurrentQueue<long?> seen, CancellationToken token) =>
        seen.Enqueue((await context.Data.QueryAsync(new AlvoQuery { Entity = Notes, IncludeTotalCount = true }, token)).TotalCount);

    private static async Task<IReadOnlyList<string>> StatementsAsync(AlvoTestHost alvo, AlvoContext clerk, SqlCapture capture)
    {
        capture.Clear();
        await alvo.Data.QueryAsync(new AlvoQuery { Entity = Notes, IncludeTotalCount = true }, clerk, Ct);

        return capture.Statements;
    }

    private static async Task<AlvoTestHost> StartAsync(ReadsDatabase database, Action<IAlvoBuilder> configure) =>
        await AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "reads-desk.alvo.json"));
                configure(alvo);
            },
            cancellationToken: Ct);

    private AlvoContext Clerk() => AlvoTestHost.Caller("clerk") with { Tenant = new TenantId(_tenant) };

    private static Dictionary<string, object?> NoteValues(AlvoContext caller, string title, string region) => new()
    {
        ["title"] = title,
        ["owner_id"] = caller.User.Value,
        ["region"] = region,
        ["tenant_id"] = caller.Tenant!.Value.Value,
    };

    private static async Task<Guid> CreateAsync(AlvoTestHost alvo, AlvoContext caller, string title, string region)
    {
        var created = await alvo.Data.CreateAsync(Notes, NoteValues(caller, title, region), caller, cancellationToken: Ct);

        return (Guid)created[AlvoManagedColumns.Id]!;
    }

    protected sealed record ReadsDatabase(Action<IAlvoBuilder> Use, string Marker);

    private sealed class RegionSwitch(string value)
    {
        private string _value = value;

        internal string Value
        {
            get => Volatile.Read(ref _value);
            set => Volatile.Write(ref _value, value);
        }
    }
}
