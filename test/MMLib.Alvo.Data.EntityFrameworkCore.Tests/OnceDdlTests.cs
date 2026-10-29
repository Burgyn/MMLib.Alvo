using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// A store's framework DDL runs once per process (Ruling 47): concurrent first callers share one run, a finished run is
/// never repeated, and a failed one is forgotten so the next call tries again.
/// </summary>
public sealed class OnceDdlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_first_callers_share_one_run()
    {
        var runs = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ddl = new OnceDdl(async _ =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
        });

        var first = ddl.EnsureAsync(Ct);
        var second = ddl.EnsureAsync(Ct);
        second.IsCompleted.ShouldBeFalse("the second caller waits for the first run");
        gate.SetResult();
        await Task.WhenAll(first, second);

        runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_finished_run_is_not_repeated()
    {
        var runs = 0;
        var ddl = new OnceDdl(_ =>
        {
            runs++;
            return Task.CompletedTask;
        });

        await ddl.EnsureAsync(Ct);
        await ddl.EnsureAsync(Ct);

        runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_run_is_forgotten_and_the_next_call_runs_again()
    {
        var runs = 0;
        var ddl = new OnceDdl(_ => ++runs == 1 ? Task.FromException(new InvalidOperationException("cold")) : Task.CompletedTask);

        await Should.ThrowAsync<InvalidOperationException>(() => ddl.EnsureAsync(Ct));
        await ddl.EnsureAsync(Ct);

        runs.ShouldBe(2);
    }

    [Fact]
    public async Task A_caller_waiting_on_a_failed_run_sees_its_failure()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ddl = new OnceDdl(_ => gate.Task);

        var first = ddl.EnsureAsync(Ct);
        var second = ddl.EnsureAsync(Ct);
        gate.SetException(new InvalidOperationException("cold"));

        await Should.ThrowAsync<InvalidOperationException>(() => first);
        await Should.ThrowAsync<InvalidOperationException>(() => second);
    }

    [Fact]
    public async Task The_run_ignores_the_first_caller_s_cancellation()
    {
        var seen = new CancellationToken(canceled: true);
        var ddl = new OnceDdl(token =>
        {
            seen = token;
            return Task.CompletedTask;
        });
        using var cancelled = new CancellationTokenSource();

        await ddl.EnsureAsync(cancelled.Token);

        seen.CanBeCanceled.ShouldBeFalse("a caller hanging up must not leave a half-created table behind");
    }
}
