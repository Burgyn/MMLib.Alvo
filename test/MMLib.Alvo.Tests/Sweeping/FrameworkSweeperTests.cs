using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.InboundWebhooks;
using MMLib.Alvo.Sweeping;

namespace MMLib.Alvo.Tests.Sweeping;

/// <summary>
/// The one framework sweeper (#527): every registered job, in bounded batches, hourly; never fatal, and quiet when it forgot
/// nothing. It began as the ledger sweep (Rulings 26, 41), whose two ledgers are its first jobs.
/// </summary>
public sealed class FrameworkSweeperTests : IDisposable
{
    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _factory;
    private readonly ScriptedJob _first = new("first");
    private readonly ScriptedJob _second = new("second");
    private readonly ServiceProvider _services;

    public FrameworkSweeperTests()
    {
        _factory = LoggerFactory.Create(logging => logging.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        _services = new ServiceCollection()
            .AddSingleton<ISweepJob>(_first)
            .AddSingleton<ISweepJob>(_second)
            .BuildServiceProvider();
    }

    /// <summary>A full batch asks for another, until one comes back short — every job, every tick.</summary>
    [Fact]
    public async Task A_job_runs_again_while_its_batch_comes_back_full()
    {
        _first.Batches.Enqueue(SweepLimits.BatchSize);
        _first.Batches.Enqueue(SweepLimits.BatchSize);
        _first.Batches.Enqueue(7);
        _second.Batches.Enqueue(3);

        (await Sweeper().SweepOnceAsync(TestContext.Current.CancellationToken)).ShouldBe((2 * SweepLimits.BatchSize) + 10);

        _first.Limits.ShouldBe([SweepLimits.BatchSize, SweepLimits.BatchSize, SweepLimits.BatchSize]);
        _second.Limits.ShouldBe([SweepLimits.BatchSize]);
        _logs.Entries.ShouldContain(entry => entry.Level == LogLevel.Information && entry.Message.Contains("forgot 1007 rows of first", StringComparison.Ordinal));
    }

    /// <summary>A tick is bounded: a job whose every batch is full stops after <see cref="SweepLimits.MaxBatches"/>.</summary>
    [Fact]
    public async Task A_tick_runs_at_most_twenty_batches_of_one_job()
    {
        _first.Always = SweepLimits.BatchSize;

        (await Sweeper().SweepOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(SweepLimits.MaxBatches * SweepLimits.BatchSize);

        _first.Limits.Count.ShouldBe(SweepLimits.MaxBatches);
    }

    /// <summary>A failing job — a provider's timeout surfaced as a cancellation included — is logged, and the others still run.</summary>
    [Fact]
    public async Task A_failing_job_is_logged_and_the_others_still_run()
    {
        _first.Failure = new TaskCanceledException("a command timeout");
        _second.Batches.Enqueue(2);

        (await Sweeper().SweepOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(2);

        _logs.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Exception is TaskCanceledException && entry.Message.Contains("first", StringComparison.Ordinal));
        _second.Limits.ShouldBe([SweepLimits.BatchSize]);
    }

    [Fact]
    public async Task The_hosts_own_stop_still_cancels_the_sweep()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        _first.Failure = new OperationCanceledException(stopping.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => Sweeper().SweepOnceAsync(stopping.Token));
    }

    /// <summary>
    /// A host disposed without <c>StopAsync</c> — a failed start, a test's teardown — cancels the stopping token, which the
    /// generic host reports as a failed service unless the service ends quietly.
    /// </summary>
    [Fact]
    public async Task A_host_disposed_without_stopping_ends_the_sweeper_quietly()
    {
        var sweeper = Sweeper();
        await sweeper.StartAsync(CancellationToken.None);
        await _first.Swept.Task;

        sweeper.Dispose();

        await Should.NotThrowAsync(() => sweeper.ExecuteTask!);
        sweeper.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task A_sweep_that_forgot_nothing_logs_below_information()
    {
        _ = await Sweeper().SweepOnceAsync(TestContext.Current.CancellationToken);

        _logs.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Information);
        _logs.Entries.ShouldContain(entry => entry.Level == LogLevel.Debug);
    }

    /// <summary>The ledgers are the sweeper's first jobs, and a host registers one sweeper however many packages ask.</summary>
    [Fact]
    public void The_ledgers_register_two_jobs_and_one_sweeper()
    {
        var services = new ServiceCollection().AddLogging().AddAlvoInboundWebhooks().AddAlvoFrameworkSweeper();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<ISweepJob>().Select(job => job.Name).ShouldBe(["inbound webhook ids", "command keys"]);
        provider.GetServices<IHostedService>().OfType<FrameworkSweeper>().Count().ShouldBe(1);
    }

    public void Dispose()
    {
        _services.Dispose();
        _factory.Dispose();
        _logs.Dispose();
    }

    private FrameworkSweeper Sweeper() => new(_services, new FakeTimeProvider(), _factory.CreateLogger<FrameworkSweeper>());

    /// <summary>A job whose batches are the fact's.</summary>
    private sealed class ScriptedJob(string name) : ISweepJob
    {
        internal Queue<int> Batches { get; } = new();

        internal List<int> Limits { get; } = [];

        internal int? Always { get; set; }

        internal Exception? Failure { get; set; }

        internal TaskCompletionSource Swept { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => name;

        public Task<int> SweepAsync(DateTimeOffset now, int limit, CancellationToken ct)
        {
            Limits.Add(limit);
            Swept.TrySetResult();
            return Failure is { } failure
                ? Task.FromException<int>(failure)
                : Task.FromResult(Always ?? (Batches.Count > 0 ? Batches.Dequeue() : 0));
        }
    }
}
