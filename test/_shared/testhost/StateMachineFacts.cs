using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;

using System.Data.Common;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>An engine a lifecycle fact runs on: how the host uses it, and a raw connection to read what a write left behind.</summary>
/// <param name="Use">Points the host at the database.</param>
/// <param name="Connect">A fresh, unopened connection to the same database.</param>
public sealed record LifecycleDatabase(Action<IAlvoBuilder> Use, Func<DbConnection> Connect);

/// <summary>
/// The descriptor's <c>stateMachine</c> over the real pipeline (spec 2026-10-14 §7 HX-18 acceptance (1), (2)), on whichever engine
/// the deriving class supplies — the SQLite leg in <c>MMLib.Alvo.TestHost.Tests</c>, the PostgreSQL leg in
/// <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
public abstract class StateMachineFacts
{
    internal const string Bookings = "bookings";
    internal const string LifecycleDesk = "lifecycle-desk.alvo.json";

    private static readonly DateTimeOffset _now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected FakeTimeProvider Time { get; } = new(_now);

    protected TenantId Tenant { get; } = new(Guid.NewGuid());

    [Fact]
    public async Task An_undeclared_transition_is_refused_and_writes_nothing()
    {
        EnsureEngineAvailable();
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database);
        var id = await DraftAsync(alvo);

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(() => MoveAsync(alvo, id, "done"));

        (refused.Kind, refused.Fields.ShouldHaveSingleItem()).ShouldBe((AlvoConstraintKind.Transition, "status"));
        (await StatusAsync(alvo, id)).ShouldBe("draft");
        (await WriteHookRows.CountAsync(database.Connect, "alvo_outbox", "event_type = 'entity.bookings.updated'")).ShouldBe(0);
    }

    [Fact]
    public async Task A_declared_transition_is_taken()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(await DatabaseAsync());
        var id = await DraftAsync(alvo);

        await MoveAsync(alvo, id, "confirmed");

        (await StatusAsync(alvo, id)).ShouldBe("confirmed");
    }

    [Fact]
    public async Task A_transition_whose_when_is_false_is_refused()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(await DatabaseAsync());
        var id = await DraftAsync(alvo, _now.AddHours(-1));

        (await Should.ThrowAsync<AlvoConstraintViolationException>(() => MoveAsync(alvo, id, "confirmed"))).Kind.ShouldBe(AlvoConstraintKind.Transition);
        (await StatusAsync(alvo, id)).ShouldBe("draft");
    }

    [Fact]
    public async Task A_transition_needing_a_role_the_caller_lacks_is_forbidden_and_the_role_holder_takes_it()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(await DatabaseAsync());
        var id = await DraftAsync(alvo);
        await MoveAsync(alvo, id, "confirmed");

        await Should.ThrowAsync<AlvoAuthorizationException>(() => MoveAsync(alvo, id, "done"));
        (await StatusAsync(alvo, id)).ShouldBe("confirmed");
        await MoveAsync(alvo, id, "done", Caller("manager"));

        (await StatusAsync(alvo, id)).ShouldBe("done");
    }

    [Fact]
    public async Task A_declarative_mutate_cannot_skip_the_machine()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(await DatabaseAsync());
        var id = await DraftAsync(alvo);

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            alvo.Data.UpdateAsync(Bookings, id, new Dictionary<string, object?> { ["note"] = "skip" }, Caller("staff"), cancellationToken: Ct));

        refused.Kind.ShouldBe(AlvoConstraintKind.Transition);
        (await StatusAsync(alvo, id)).ShouldBe("draft");
    }

    [Fact]
    public async Task A_patch_not_naming_the_state_is_never_refused_by_the_machine()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(await DatabaseAsync());
        var id = await DraftAsync(alvo);
        await MoveAsync(alvo, id, "cancelled");

        var updated = await alvo.Data.UpdateAsync(Bookings, id, new Dictionary<string, object?> { ["note"] = "late call" }, Caller("staff"), cancellationToken: Ct);

        (updated["note"], updated["status"]).ShouldBe(("late call", "cancelled"));
    }

    [Fact]
    public async Task A_batch_row_taking_an_undeclared_transition_is_refused_by_its_index()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(await DatabaseAsync());
        var (first, second) = (await DraftAsync(alvo), await DraftAsync(alvo));

        var result = await alvo.Data.UpdateManyAsync(
            Bookings,
            [new AlvoRowPatch(first, Status("confirmed")), new AlvoRowPatch(second, Status("done"))],
            Caller("staff"),
            cancellationToken: Ct);

        var refusal = result.Refusals.ShouldHaveSingleItem();
        (refusal.Index, refusal.Code, result.Affected).ShouldBe((1, "transition", 0));
        ((await StatusAsync(alvo, first)), (await StatusAsync(alvo, second))).ShouldBe(("draft", "draft"));
    }

    [Fact]
    public async Task A_host_before_write_setting_the_state_fails_invalid_set()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(await DatabaseAsync(), alvo => alvo.Entity(Bookings, b => b.BeforeWrite("force", (BeforeWrite<AlvoRecord> write) =>
        {
            write.Set("status", "confirmed");
            return ValueTask.CompletedTask;
        })));

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.CreateAsync(Bookings, Booking(_now.AddHours(48)), Caller("staff"), cancellationToken: Ct)))
            .Message.ShouldContain("invalid-set");
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = Bookings }, Caller("staff"), Ct)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_host_before_write_setting_a_field_the_when_reads_cannot_pass_a_refused_move()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(await DatabaseAsync(), alvo => alvo.Entity(Bookings, b => b.BeforeWrite("backdate", (BeforeWrite<AlvoRecord> write) =>
        {
            if (write.Operation == AlvoWriteOperation.Update)
            {
                write.Set("starts_at", _now.AddHours(-1));
            }

            return ValueTask.CompletedTask;
        })));
        var id = await DraftAsync(alvo);

        (await Should.ThrowAsync<AlvoConstraintViolationException>(() => MoveAsync(alvo, id, "confirmed"))).Kind.ShouldBe(AlvoConstraintKind.Transition);
        (await StatusAsync(alvo, id)).ShouldBe("draft");
    }

    protected abstract Task<LifecycleDatabase> DatabaseAsync();

    /// <summary>Skips the fact where the deriving engine cannot run; every fact calls it first, before any assertion can catch the skip.</summary>
    protected virtual void EnsureEngineAvailable()
    {
    }

    private async Task<AlvoTestHost> StartAsync(LifecycleDatabase database, Action<IAlvoBuilder>? configure = null) =>
        await AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", LifecycleDesk));
                configure?.Invoke(alvo);
            },
            Time,
            cancellationToken: Ct);

    private async Task<Guid> DraftAsync(AlvoTestHost alvo, DateTimeOffset? startsAt = null) =>
        (Guid)(await alvo.Data.CreateAsync(Bookings, Booking(startsAt ?? _now.AddHours(48)), Caller("staff"), cancellationToken: Ct))["id"]!;

    private Task<AlvoRecord> MoveAsync(AlvoTestHost alvo, Guid id, string status, AlvoContext? caller = null) =>
        alvo.Data.UpdateAsync(Bookings, id, Status(status), caller ?? Caller("staff"), cancellationToken: Ct);

    private async Task<string?> StatusAsync(AlvoTestHost alvo, Guid id) =>
        (string?)(await alvo.Data.GetAsync(Bookings, id, Caller("staff"), Ct))!["status"];

    private AlvoContext Caller(string role) => AlvoTestHost.Caller(role) with { Tenant = Tenant };

    private Dictionary<string, object?> Booking(DateTimeOffset startsAt) => new()
    {
        ["starts_at"] = startsAt,
        ["note"] = "booked",
        ["tenant_id"] = Tenant.Value,
    };

    private static Dictionary<string, object?> Status(string status) => new() { ["status"] = status };
}
