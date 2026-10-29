using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;
using System.Runtime.CompilerServices;

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
        await unit.CommitAsync();

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
        (await Should.ThrowAsync<AlvoUnitInProgressException>(() => unit.CommitAsync())).ShouldBeSameAs(original);
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
        await Should.ThrowAsync<InvalidOperationException>(() => unit.CommitAsync());
    }

    [Fact]
    public async Task A_call_made_by_the_call_holding_the_unit_is_admitted()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);

        var answer = await unit.RunAsync(async _ => await unit.RunAsync(_ => Task.FromResult(2), Ct) + 1, Ct);

        answer.ShouldBe(3);
        await unit.CommitAsync();
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
        await Should.ThrowAsync<InvalidOperationException>(() => unit.CommitAsync());
    }

    [Fact]
    public async Task A_call_started_inside_a_call_and_still_running_after_it_returned_poisons_the_unit()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? detached = null;

        await unit.RunAsync(
            async token =>
            {
                await InsertAsync(unit, token);
                detached = unit.RunAsync(
                    async _ =>
                    {
                        await release.Task;
                        return 1;
                    },
                    Ct);
                return 0;
            },
            Ct);
        release.SetResult();

        (await Should.ThrowAsync<InvalidOperationException>(() => detached!)).Message.ShouldContain("after the call that started it returned");
        (await Should.ThrowAsync<InvalidOperationException>(() => unit.CommitAsync())).Message.ShouldContain("after the call that started it returned");
        (await world.ScalarAsync("SELECT COUNT(*) FROM \"note\"")).ShouldBe(0L);
    }

    [Fact]
    public async Task A_call_started_inside_a_call_that_starts_after_it_returned_is_refused_and_poisons_the_unit()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? late = null;

        await unit.RunAsync(
            async token =>
            {
                await InsertAsync(unit, token);
                late = LateAsync();
                return 0;
            },
            Ct);
        start.SetResult();

        (await Should.ThrowAsync<InvalidOperationException>(() => late!)).Message.ShouldContain("after the call that started it returned");
        await Should.ThrowAsync<InvalidOperationException>(() => unit.CommitAsync());
        (await world.ScalarAsync("SELECT COUNT(*) FROM \"note\"")).ShouldBe(0L);

        async Task<int> LateAsync()
        {
            await start.Task;
            return await unit.RunAsync(_ => Task.FromResult(2), Ct);
        }
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
        await Should.ThrowAsync<TimeoutException>(() => unit.CommitAsync());
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

        var commit = unit.CommitAsync();
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

        var commit = unit.CommitAsync();
        await world.Commits.Reached;
        (await Should.ThrowAsync<InvalidOperationException>(() => unit.RunAsync(_ => Task.FromResult(2), Ct)))
            .Message.ShouldContain("has ended");
        unit.Poison(new InvalidOperationException("after the decision"));
        world.Commits.Release();

        await commit;
        (await world.ScalarAsync("SELECT COUNT(*) FROM \"note\"")).ShouldBe(1L);
    }

    [Fact]
    public async Task A_deadline_that_fired_before_the_commit_decision_rolls_the_unit_back()
    {
        var clock = new FakeTimeProvider();
        await using var world = await StartAsync(time: clock);
        await using var unit = await OpenAsync(world, new AlvoTransactionOptions { Timeout = TimeSpan.FromSeconds(1) });
        await unit.RunAsync(async token => await InsertAsync(unit, token), Ct);
        var gate = Gate(unit);
        Task commit;
        Task deadline;

        // Holding the unit's lock parks the deadline's callback before it records the timeout: the deadline has fired,
        // but only the decision itself can see it — the window a timer thread opens against a returning delegate.
        gate.Enter();
        try
        {
            deadline = Task.Run(() => clock.Advance(TimeSpan.FromSeconds(2)), Ct);
            SpinWait.SpinUntil(() => unit.TimedOut, TimeSpan.FromSeconds(30)).ShouldBeTrue();
            deadline.IsCompleted.ShouldBeFalse("the deadline's callback must be parked on the unit's lock, not done");
            commit = unit.CommitAsync();
        }
        finally
        {
            gate.Exit();
        }

        await deadline;
        (await Should.ThrowAsync<TimeoutException>(() => commit)).Message.ShouldContain("Timeout of 1 s");
        unit.HasEnded.ShouldBeTrue();
        (await world.ScalarAsync("SELECT COUNT(*) FROM \"note\"")).ShouldBe(0L);
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

    [Fact]
    public async Task A_failing_cleanup_step_on_release_still_releases_the_write_turn_and_the_scope()
    {
        await using var world = await StartAsync(services => services.AddScoped<DisposalProbe>());
        var (unit, turn, probe) = await DisposeFailingUnitAsync(world);

        await Should.ThrowAsync<CleanupFailure>(async () => await unit.DisposeAsync());

        turn.Disposed.ShouldBeTrue();
        probe.Disposed.ShouldBeTrue();
        unit.HasEnded.ShouldBeTrue();
    }

    [Fact]
    public async Task A_cleanup_failure_after_a_successful_commit_is_logged_not_thrown()
    {
        var logs = new CapturingLoggerProvider();
        await using var world = await StartAsync(services => services
            .AddScoped<DisposalProbe>()
            .AddLogging(logging => logging.AddProvider(logs)));
        var (unit, turn, probe) = await DisposeFailingUnitAsync(world);

        await unit.CommitAsync();

        turn.Disposed.ShouldBeTrue("a commit whose cleanup failed must still let go of the write turn");
        var warning = logs.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Exception.ShouldBeOfType<CleanupFailure>();
        await unit.DisposeAsync();
        probe.Disposed.ShouldBeTrue();
        unit.HasEnded.ShouldBeTrue();
    }

    [Fact]
    public async Task A_throwing_cancel_callback_does_not_stop_a_released_unit_rolling_back()
    {
        await using var world = await StartAsync();
        var unit = await OpenAsync(world, new AlvoTransactionOptions { Timeout = TimeSpan.FromHours(1) });
        unit.Token.Register(static () => throw new InvalidOperationException("a host callback"));
        var orphan = unit.RunAsync(
            async token =>
            {
                await InsertAsync(unit, token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            Ct);

        await unit.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await Should.ThrowAsync<OperationCanceledException>(() => orphan);
        unit.HasEnded.ShouldBeTrue();
        await using var next = await OpenAsync(world).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await next.CommitAsync();
        (await world.ScalarAsync("SELECT COUNT(*) FROM \"note\"")).ShouldBe(0L);
    }

    [Fact]
    public async Task A_throwing_cancel_callback_does_not_hide_the_not_awaited_refusal()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world, new AlvoTransactionOptions { Timeout = TimeSpan.FromHours(1) });
        unit.Token.Register(static () => throw new InvalidOperationException("a host callback"));
        var orphan = unit.RunAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            Ct);

        (await Should.ThrowAsync<InvalidOperationException>(() => unit.CommitAsync())).Message.ShouldContain("not awaited");
        await Should.ThrowAsync<OperationCanceledException>(() => orphan);
        unit.HasEnded.ShouldBeTrue();
    }

    [Fact]
    public async Task A_committed_unit_fires_no_cancel_callback_when_released()
    {
        await using var world = await StartAsync();
        var unit = await OpenAsync(world);
        var cancelled = false;
        unit.Token.Register(() => cancelled = true);

        await unit.CommitAsync();
        await unit.DisposeAsync();

        cancelled.ShouldBeFalse();
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
    public async Task A_unit_resolves_its_hooks_once_however_many_writes_read_them()
    {
        await using var world = await StartAsync(services => services.AddTransient<IBeforeHookRunner, StubBeforeHookRunner>());
        await using var unit = await OpenAsync(world);

        unit.Hooks.ShouldBeSameAs(unit.Hooks);
    }

    [Fact]
    public async Task A_poison_after_the_unit_ended_is_ignored()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        await unit.CommitAsync();

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
        await Should.ThrowAsync<OperationCanceledException>(() => unit.CommitAsync());
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
        await Should.ThrowAsync<OperationCanceledException>(() => unit.CommitAsync());
        unit.TimedOut.ShouldBeFalse();
    }

    [Fact]
    public async Task A_chain_depth_refusal_inside_a_call_poisons_the_unit()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() =>
            unit.RunAsync<int>(_ => throw new AlvoChainDepthExceededException("loop"), Ct));

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => unit.CommitAsync());
    }

    [Fact]
    public async Task A_refused_call_that_is_not_a_poison_leaves_the_unit_usable()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);

        await Should.ThrowAsync<AlvoAuthorizationException>(() =>
            unit.RunAsync<int>(_ => throw new AlvoAuthorizationException("no"), Ct));

        (await unit.RunAsync(_ => Task.FromResult(3), Ct)).ShouldBe(3);
        await unit.CommitAsync();
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

    /// <summary>A unit over real parts whose transaction fails to dispose, a turn probe, and a scoped probe.</summary>
    private static async Task<(WriteUnit Unit, TurnProbe Turn, DisposalProbe Probe)> DisposeFailingUnitAsync(WritePathWorld world)
    {
        var root = world.CreateContext();
        await root.Database.OpenConnectionAsync(Ct);
        var transaction = new DisposeFailingTransaction(await root.Database.BeginTransactionAsync(Ct));
        var turn = new TurnProbe();
        var scope = world.Scopes.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetRequiredService<DisposalProbe>();
        var unit = new WriteUnit(
            new WriteUnitParts(root, root.Database.GetDbConnection(), transaction, turn, scope),
            world.Caller, AlvoProvenance.None, TimeSpan.FromHours(1), TimeProvider.System, Ct);

        return (unit, turn, probe);
    }

    /// <summary>Writes one fixture row on the unit's own connection and transaction, as a call inside it will.</summary>
    private static async Task<int> InsertAsync(WriteUnit unit, CancellationToken token)
    {
        await using var command = unit.Connection.CreateCommand();
        command.Transaction = unit.Transaction;
        command.CommandText =
            $"INSERT INTO \"note\" (\"id\", \"tenant_id\", \"title\") VALUES ('{Guid.NewGuid()}', '{Guid.NewGuid()}', 'unit')";

        return await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>The unit's own lock, so a test can hold the deadline's callback at the point it would record the timeout.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_gate")]
    private static extern ref Lock Gate(WriteUnit unit);

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10, Ct);
        }

        condition().ShouldBeTrue();
    }

    private sealed record LogEntry(string Category, LogLevel Level, Exception? Exception);

    /// <summary>Records what <see cref="WriteUnit"/> logs, and nothing any other category does.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        internal IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, this);

        public void Dispose()
        {
        }

        private void Add(LogEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        private sealed class Capturing(string category, CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => category == typeof(WriteUnit).FullName;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    owner.Add(new LogEntry(category, logLevel, exception));
                }
            }
        }
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
