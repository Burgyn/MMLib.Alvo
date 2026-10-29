using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// One transaction unit's life (spec 2026-10-14 §4.5): its provenance, one call at a time, poisoning, its deadline, its
/// scope, and what is refused after it ends — over SQLite in memory, with no write inside it yet.
/// </summary>
public sealed class WriteUnitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_unit_reports_its_caller_s_depth_and_one_correlation_id_of_its_own()
    {
        await using var world = await StartAsync();
        var caller = world.Caller with { Provenance = new AlvoProvenance { Depth = 3 } };

        await using var unit = await world.Data.OpenUnitAsync(caller, new AlvoTransactionOptions(), world.Scopes, Ct);

        unit.Provenance.Depth.ShouldBe(3);
        unit.Provenance.CorrelationId.ShouldNotBeNullOrWhiteSpace();
        unit.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task A_unit_keeps_the_correlation_id_its_caller_carries()
    {
        await using var world = await StartAsync();
        var caller = world.Caller with { Provenance = new AlvoProvenance { CorrelationId = "request-7" } };

        await using var unit = await world.Data.OpenUnitAsync(caller, new AlvoTransactionOptions(), world.Scopes, Ct);

        unit.Provenance.CorrelationId.ShouldBe("request-7");
    }

    [Fact]
    public async Task A_caller_past_the_depth_limit_opens_no_unit()
    {
        await using var world = await StartAsync();
        var caller = world.Caller with { Provenance = new AlvoProvenance { Depth = AlvoChainDepthLimit.DefaultMaxDepth + 1 } };

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => world.Data.OpenUnitAsync(caller, new AlvoTransactionOptions(), world.Scopes, Ct));
    }

    [Fact]
    public async Task A_committed_unit_has_ended_and_is_off_the_flow_once_released()
    {
        await using var world = await StartAsync();
        var unit = await OpenAsync(world);

        (await unit.RunAsync(_ => Task.FromResult(7), Ct)).ShouldBe(7);
        await unit.CommitAsync(Ct);

        unit.HasEnded.ShouldBeTrue();
        unit.IsOpen.ShouldBeTrue("the TransactAsync that opened it has not returned yet");
        await unit.DisposeAsync();
        unit.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task A_poisoned_unit_refuses_further_calls_and_its_commit_throws_the_original_failure()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        var original = new AlvoUnitInProgressException("refused");

        unit.Poison(original);

        (await Should.ThrowAsync<InvalidOperationException>(() => unit.RunAsync(_ => Task.FromResult(1), Ct))).InnerException.ShouldBeSameAs(original);
        (await Should.ThrowAsync<AlvoUnitInProgressException>(() => unit.CommitAsync(Ct))).ShouldBeSameAs(original);
        unit.HasEnded.ShouldBeTrue();
    }

    [Fact]
    public async Task A_second_call_while_one_runs_is_refused_and_poisons_the_unit()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = unit.RunAsync(_ => release.Task, Ct);

        (await Should.ThrowAsync<InvalidOperationException>(() => unit.RunAsync(_ => Task.FromResult(2), Ct)))
            .Message.ShouldContain("one call at a time");
        release.SetResult(1);

        (await first).ShouldBe(1);
        await Should.ThrowAsync<InvalidOperationException>(() => unit.CommitAsync(Ct));
    }

    [Fact]
    public async Task A_call_made_by_the_call_holding_the_unit_is_admitted()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);

        var answer = await unit.RunAsync(async _ => await unit.RunAsync(_ => Task.FromResult(2), Ct) + 1, Ct);

        answer.ShouldBe(3);
        await unit.CommitAsync(Ct);
    }

    [Fact]
    public async Task Two_calls_at_once_under_the_holding_call_are_still_refused()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var refusal = await unit.RunAsync(
            async _ =>
            {
                var holding = unit.RunAsync(_ => release.Task, Ct);
                var sibling = await Should.ThrowAsync<InvalidOperationException>(() => unit.RunAsync(_ => Task.FromResult(9), Ct));
                release.SetResult(1);
                await holding;
                return sibling;
            },
            Ct);

        refusal.Message.ShouldContain("one call at a time");
        await Should.ThrowAsync<InvalidOperationException>(() => unit.CommitAsync(Ct));
    }

    [Fact]
    public async Task The_deadline_cancels_a_running_call_and_rolls_the_unit_back_without_waiting_for_its_caller()
    {
        var clock = new FakeTimeProvider();
        await using var world = await StartAsync(time: clock);
        await using var unit = await OpenAsync(world, new AlvoTransactionOptions { Timeout = TimeSpan.FromSeconds(1) });
        var running = unit.RunAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            Ct);

        clock.Advance(TimeSpan.FromSeconds(2));

        (await Should.ThrowAsync<TimeoutException>(() => running)).Message.ShouldContain("Timeout of 1 s");
        await EventuallyAsync(() => unit.HasEnded);
        unit.TimedOut.ShouldBeTrue();
        await Should.ThrowAsync<TimeoutException>(() => unit.RunAsync(_ => Task.FromResult(1), Ct));
        await Should.ThrowAsync<TimeoutException>(() => unit.CommitAsync(Ct));
    }

    [Fact]
    public async Task A_call_still_running_when_the_delegate_returns_rolls_the_unit_back()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unawaited = unit.RunAsync(
            async token =>
            {
                await InsertAsync(unit, token);
                await release.Task;
                return 1;
            },
            Ct);

        var commit = unit.CommitAsync(Ct);
        var late = new InvalidOperationException("after the decision");
        unit.Poison(late);
        (await Should.ThrowAsync<InvalidOperationException>(() => unit.RunAsync(_ => Task.FromResult(2), Ct)))
            .Message.ShouldContain("has already failed");
        release.SetResult();

        (await unawaited).ShouldBe(1);
        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => commit);
        refusal.ShouldNotBeSameAs(late);
        refusal.Message.ShouldContain("not awaited");
        unit.HasEnded.ShouldBeTrue();
        (await world.ScalarAsync("SELECT COUNT(*) FROM \"note\"")).ShouldBe(0L);
    }

    [Fact]
    public async Task A_call_refused_while_a_decided_commit_runs_does_not_undo_it()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        await unit.RunAsync(async token => await InsertAsync(unit, token), Ct);
        world.Commits.Hold();

        var commit = unit.CommitAsync(Ct);
        await world.Commits.Reached;
        (await Should.ThrowAsync<InvalidOperationException>(() => unit.RunAsync(_ => Task.FromResult(2), Ct)))
            .Message.ShouldContain("has ended");
        unit.Poison(new InvalidOperationException("after the decision"));
        world.Commits.Release();

        await commit;
        (await world.ScalarAsync("SELECT COUNT(*) FROM \"note\"")).ShouldBe(1L);
    }

    [Fact]
    public async Task Releasing_a_unit_cancels_a_call_still_running_instead_of_waiting_for_its_deadline()
    {
        await using var world = await StartAsync();
        var unit = await OpenAsync(world, new AlvoTransactionOptions { Timeout = TimeSpan.FromHours(1) });
        var orphan = unit.RunAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            Ct);

        await unit.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await Should.ThrowAsync<OperationCanceledException>(() => orphan);
        unit.HasEnded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failing_cleanup_step_still_releases_the_write_turn_and_the_scope(bool commit)
    {
        await using var world = await StartAsync(services => services.AddScoped<DisposalProbe>());
        var root = world.CreateContext();
        await root.Database.OpenConnectionAsync(Ct);
        var transaction = new DisposeFailingTransaction(await root.Database.BeginTransactionAsync(Ct));
        var turn = new TurnProbe();
        var scope = world.Scopes.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetRequiredService<DisposalProbe>();
        var unit = new WriteUnit(
            new WriteUnitParts(root, root.Database.GetDbConnection(), transaction, turn, scope),
            world.Caller, AlvoProvenance.None, TimeSpan.FromHours(1), TimeProvider.System, Ct);

        if (commit)
        {
            await Should.ThrowAsync<CleanupFailure>(() => unit.CommitAsync(Ct));
            turn.Disposed.ShouldBeTrue("a commit whose cleanup failed must still let go of the write turn");
            await unit.DisposeAsync();
        }
        else
        {
            await Should.ThrowAsync<CleanupFailure>(async () => await unit.DisposeAsync());
        }

        turn.Disposed.ShouldBeTrue();
        probe.Disposed.ShouldBeTrue();
        unit.HasEnded.ShouldBeTrue();
    }

    [Fact]
    public async Task A_write_inside_a_unit_resolves_its_hooks_from_the_unit_s_scope()
    {
        await using var world = await StartAsync(services => services.AddScoped<IBeforeHookRunner, StubBeforeHookRunner>());
        await using var unit = await OpenAsync(world);

        unit.Hooks.ShouldBeOfType<StubBeforeHookRunner>().ShouldNotBeSameAs(world.Hooks);
        unit.Hooks.ShouldBeSameAs(unit.Services.GetRequiredService<IBeforeHookRunner>());
    }

    [Fact]
    public async Task A_poison_after_the_unit_ended_is_ignored()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        await unit.CommitAsync(Ct);

        unit.Poison(new InvalidOperationException("too late"));

        (await Should.ThrowAsync<InvalidOperationException>(() => unit.RunAsync(_ => Task.FromResult(1), Ct)))
            .Message.ShouldContain("has ended");
    }

    [Fact]
    public async Task Cancelling_the_token_the_unit_was_opened_with_is_a_cancellation_not_a_timeout()
    {
        await using var world = await StartAsync();
        using var opener = new CancellationTokenSource();
        await using var unit = await world.Data.OpenUnitAsync(world.Caller, new AlvoTransactionOptions(), world.Scopes, opener.Token);
        var running = unit.RunAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            Ct);

        await opener.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => running);
        unit.TimedOut.ShouldBeFalse();
        await Should.ThrowAsync<OperationCanceledException>(() => unit.CommitAsync(Ct));
    }

    [Fact]
    public async Task Releasing_a_unit_twice_is_harmless()
    {
        await using var world = await StartAsync();
        var unit = await OpenAsync(world);
        await unit.DisposeAsync();

        await Should.NotThrowAsync(async () => await unit.DisposeAsync());
    }

    [Fact]
    public async Task A_unit_used_after_it_was_released_is_refused()
    {
        await using var world = await StartAsync();
        var unit = await OpenAsync(world);
        await unit.DisposeAsync();

        (await Should.ThrowAsync<InvalidOperationException>(() => unit.RunAsync(_ => Task.FromResult(1), Ct)))
            .Message.ShouldContain("has ended");
    }

    [Fact]
    public async Task A_cancelled_call_poisons_the_unit_with_its_cancellation()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        using var caller = new CancellationTokenSource();
        var running = unit.RunAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            caller.Token);

        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => running);
        await Should.ThrowAsync<OperationCanceledException>(() => unit.CommitAsync(Ct));
        unit.TimedOut.ShouldBeFalse();
    }

    [Fact]
    public async Task A_chain_depth_refusal_inside_a_call_poisons_the_unit()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() =>
            unit.RunAsync<int>(_ => throw new AlvoChainDepthExceededException("loop"), Ct));

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => unit.CommitAsync(Ct));
    }

    [Fact]
    public async Task A_refused_call_that_is_not_a_poison_leaves_the_unit_usable()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);

        await Should.ThrowAsync<AlvoAuthorizationException>(() =>
            unit.RunAsync<int>(_ => throw new AlvoAuthorizationException("no"), Ct));

        (await unit.RunAsync(_ => Task.FromResult(3), Ct)).ShouldBe(3);
        await unit.CommitAsync(Ct);
    }

    [Fact]
    public async Task A_unit_s_scope_is_one_per_unit_and_ends_with_it()
    {
        await using var world = await StartAsync(services => services.AddScoped<DisposalProbe>());
        var unit = await OpenAsync(world);
        var probe = unit.Services.GetRequiredService<DisposalProbe>();

        unit.Services.GetRequiredService<DisposalProbe>().ShouldBeSameAs(probe);
        await unit.DisposeAsync();

        probe.Disposed.ShouldBeTrue();
    }

    private static Task<WritePathWorld> StartAsync(Action<IServiceCollection>? services = null, TimeProvider? time = null) =>
        WritePathWorld.StartAsync(WritePathFixture.Descriptor(), WritePathFixture.Schema(), services, time);

    private static Task<WriteUnit> OpenAsync(WritePathWorld world, AlvoTransactionOptions? options = null) =>
        world.Data.OpenUnitAsync(world.Caller, options ?? new AlvoTransactionOptions(), world.Scopes, Ct);

    /// <summary>Writes one fixture row on the unit's own connection and transaction, as a call inside it will.</summary>
    private static async Task<int> InsertAsync(WriteUnit unit, CancellationToken token)
    {
        await using var command = unit.Connection.CreateCommand();
        command.Transaction = unit.Transaction;
        command.CommandText =
            $"INSERT INTO \"note\" (\"id\", \"tenant_id\", \"title\") VALUES ('{Guid.NewGuid()}', '{Guid.NewGuid()}', 'unit')";

        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10, Ct);
        }

        condition().ShouldBeTrue();
    }

    private sealed class TurnProbe : IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class CleanupFailure() : Exception("the provider failed to dispose the transaction");

    /// <summary>A real transaction whose disposal fails after it let go of the engine's — a broken connector's shape.</summary>
    private sealed class DisposeFailingTransaction(IDbContextTransaction inner) : IDbContextTransaction
    {
        public Guid TransactionId => inner.TransactionId;

        public void Commit() => inner.Commit();

        public Task CommitAsync(CancellationToken cancellationToken = default) => inner.CommitAsync(cancellationToken);

        public void Rollback() => inner.Rollback();

        public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);

        public void Dispose()
        {
            inner.Dispose();
            throw new CleanupFailure();
        }

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            throw new CleanupFailure();
        }
    }

    private sealed class DisposalProbe : IAsyncDisposable
    {
        internal bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
