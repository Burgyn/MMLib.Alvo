using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

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

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10, Ct);
        }

        condition().ShouldBeTrue();
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
