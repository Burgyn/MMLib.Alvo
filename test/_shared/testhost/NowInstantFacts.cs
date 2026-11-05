using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// HX-11 acceptance (4): one now() instant per data-port call, whatever the clock does while the write runs — moved only
/// through the fake clock (this project holds no Abstractions grant; HX-11 Ruling 48). Stamps stay per-write clock reads
/// (Ruling 2). On whichever engine the deriving class supplies, as <see cref="TransactionFacts"/> is.
/// </summary>
public abstract class NowInstantFacts
{
    private const string Notes = "notes";

    private static readonly DateTimeOffset _t = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    /// <summary>How far each host hook moves the clock: well inside a before-write hook's 250 ms budget.</summary>
    private static readonly TimeSpan _hookTick = TimeSpan.FromMilliseconds(100);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract Task<NowDatabase> DatabaseAsync();

    [Fact]
    public async Task Every_now_in_one_write_is_the_instant_the_write_began_at_even_while_the_clock_moves()
    {
        var clock = new FakeTimeProvider(_t);
        await using var alvo = await StartAsync(clock, alvo => alvo.Entity(Notes, notes => notes
            .Validate("tick-validate", (ValidateWrite<AlvoRecord> _) =>
            {
                clock.Advance(_hookTick);
                return ValueTask.CompletedTask;
            })
            .BeforeWrite("tick-before", (BeforeWrite<AlvoRecord> write) =>
            {
                clock.Advance(_hookTick);
                write.Set("judged_at", clock.GetUtcNow());
                return ValueTask.CompletedTask;
            }, when: "now() == new.stamped_at")));

        var row = await alvo.Data.CreateAsync(Notes, Body("x"), AlvoTestHost.Caller("clerk"), cancellationToken: Ct);

        Instant(row, "stamped_at").ShouldBe(_t, "the descriptor before-hook's mutate: now() is the instant bound when the write began");
        row["judged_at"].ShouldNotBeNull("the host hook's when — now() == new.stamped_at — held after the clock had moved 200 ms");
        Instant(row, "updated_at").ShouldBeGreaterThan(_t, "the stamp is a per-write clock read (Ruling 2), so it lies after now()");
    }

    [Fact]
    public async Task A_rule_with_now_judges_each_call_at_its_own_instant()
    {
        var clock = new FakeTimeProvider(_t);
        await using var alvo = await StartAsync(clock, _ => { });
        var clerk = AlvoTestHost.Caller("clerk");
        await alvo.Data.CreateAsync(Notes, Body("first"), clerk, cancellationToken: Ct);

        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = Notes }, clerk, Ct)).Items.Count.ShouldBe(1);
        clock.Advance(TimeSpan.FromHours(2));
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = Notes }, clerk, Ct)).Items.ShouldBeEmpty("the list rule is expires_at > now(), and the note expired an hour ago");
    }

    [Fact]
    public async Task Two_writes_in_one_unit_see_one_now_and_keep_their_own_stamps()
    {
        var clock = new FakeTimeProvider(_t);
        await using var alvo = await StartAsync(clock, _ => { });
        var transactions = alvo.Services.GetRequiredService<IAlvoTransactions>();

        var (first, second) = await transactions.TransactAsync(AlvoTestHost.Caller("clerk"), async (unit, ct) =>
        {
            var a = await unit.CreateAsync(Notes, Body("a"), cancellationToken: ct);
            clock.Advance(TimeSpan.FromSeconds(10));
            var b = await unit.CreateAsync(Notes, Body("b"), cancellationToken: ct);
            return (a, b);
        }, cancellationToken: Ct);

        (Instant(first, "stamped_at"), Instant(second, "stamped_at")).ShouldBe((_t, _t));
        Instant(second, "created_at").ShouldBeGreaterThan(Instant(first, "created_at"), "two writes, two stamps");
    }

    [Fact]
    public async Task Two_committed_updates_of_one_row_in_one_unit_carry_two_versions()
    {
        var clock = new FakeTimeProvider(_t);
        await using var alvo = await StartAsync(clock, _ => { });
        var clerk = AlvoTestHost.Caller("clerk");
        var id = (Guid)(await alvo.Data.CreateAsync(Notes, Body("a"), clerk, cancellationToken: Ct))["id"]!;
        var transactions = alvo.Services.GetRequiredService<IAlvoTransactions>();

        var (after1, after2) = await transactions.TransactAsync(clerk, async (unit, ct) =>
        {
            clock.Advance(TimeSpan.FromTicks(10));
            var one = await unit.UpdateAsync(Notes, id, Body("b"), cancellationToken: ct);
            clock.Advance(TimeSpan.FromTicks(10));
            var two = await unit.UpdateAsync(Notes, id, Body("c"), cancellationToken: ct);
            return (one, two);
        }, cancellationToken: Ct);

        Instant(after2, "updated_at").ShouldBeGreaterThan(Instant(after1, "updated_at"), "a version (ETag) strictly increases per row (review I5, Ruling 2)");
    }

    private static Dictionary<string, object?> Body(string body) => new(StringComparer.Ordinal) { ["body"] = body };

    private static DateTimeOffset Instant(AlvoRecord row, string field) => (DateTimeOffset)row[field]!;

    private async Task<AlvoTestHost> StartAsync(FakeTimeProvider clock, Action<IAlvoBuilder> extend)
    {
        var database = await DatabaseAsync();
        return await AlvoTestHost.StartAsync(builder =>
        {
            database.Use(builder);
            builder.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "now-instant.alvo.json"));
            extend(builder);
        }, time: clock, cancellationToken: Ct);
    }

    protected sealed record NowDatabase(Action<IAlvoBuilder> Use);
}
