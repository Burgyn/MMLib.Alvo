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
            .AddSingleton<ISweepJobRegistration>(new Registered(_first))
            .AddSingleton<ISweepJobRegistration>(new Registered(_second))
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

    /// <summary>
    /// #527 review: a job that cannot even be resolved — a constructor that throws — is logged under its registration's
    /// name, and the jobs after it still run.
    /// </summary>
    [Fact]
    public async Task A_job_that_cannot_be_resolved_is_logged_and_the_others_still_run()
    {
        _second.Batches.Enqueue(4);
        using var services = new ServiceCollection()
            .AddSingleton<ISweepJobRegistration>(new Unresolvable())
            .AddSingleton<ISweepJobRegistration>(new Registered(_second))
            .BuildServiceProvider();
        var sweeper = new FrameworkSweeper(services, new FakeTimeProvider(), _factory.CreateLogger<FrameworkSweeper>());

        (await sweeper.SweepOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(4);

        _logs.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Exception is InvalidOperationException && entry.Message.Contains("Broken", StringComparison.Ordinal));
    }

    /// <summary>#527 review: a batch that loses a deadlock to another replica's sweep is retried once, at Debug.</summary>
    [Fact]
    public async Task A_deadlocked_batch_is_retried_once()
    {
        _first.Failures.Enqueue(new DeadlockException());
        _first.Batches.Enqueue(5);

        (await Sweeper().SweepOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(5);

        _first.Limits.Count.ShouldBe(2);
        _logs.Entries.ShouldContain(entry => entry.Level == LogLevel.Debug && entry.Message.Contains("deadlock", StringComparison.Ordinal));
        _logs.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Warning);
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

        provider.GetServices<ISweepJobRegistration>().Select(registration => registration.Resolve(provider).Name)
            .ShouldBe(["inbound webhook ids", "command keys"]);
        provider.GetServices<IHostedService>().OfType<FrameworkSweeper>().Count().ShouldBe(1);
    }

    public void Dispose()
    {
        _services.Dispose();
        _factory.Dispose();
        _logs.Dispose();
    }

    private FrameworkSweeper Sweeper() => new(_services, new FakeTimeProvider(), _factory.CreateLogger<FrameworkSweeper>());

    /// <summary>A registration of an instance the fact holds.</summary>
    private sealed class Registered(ISweepJob job) : ISweepJobRegistration
    {
        public string Name => job.Name;

        public ISweepJob Resolve(IServiceProvider services) => job;
    }

    /// <summary>A registration whose job's constructor throws.</summary>
    private sealed class Unresolvable : ISweepJobRegistration
    {
        public string Name => "Broken";

        public ISweepJob Resolve(IServiceProvider services) => throw new InvalidOperationException("a dependency that cannot be built");
    }

    /// <summary>The engine breaking a deadlock: <c>40P01</c>.</summary>
#pragma warning disable RCS1194 // A test double: DbException's serialization constructor is obsolete.
    private sealed class DeadlockException : System.Data.Common.DbException
#pragma warning restore RCS1194
    {
        public DeadlockException()
        {
        }

        public DeadlockException(string message)
            : base(message)
        {
        }

        public DeadlockException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public override string? SqlState => "40P01";
    }

    /// <summary>A job whose batches are the fact's.</summary>
    private sealed class ScriptedJob(string name) : ISweepJob
    {
        internal Queue<int> Batches { get; } = new();

        internal List<int> Limits { get; } = [];

        internal int? Always { get; set; }

        internal Exception? Failure { get; set; }

        internal Queue<Exception> Failures { get; } = new();

        internal TaskCompletionSource Swept { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => name;

        public Task<int> SweepAsync(DateTimeOffset now, int limit, CancellationToken ct)
        {
            Limits.Add(limit);
            Swept.TrySetResult();
            if (Failures.Count > 0)
            {
                return Task.FromException<int>(Failures.Dequeue());
            }

            return Failure is { } failure
                ? Task.FromException<int>(failure)
                : Task.FromResult(Always ?? (Batches.Count > 0 ? Batches.Dequeue() : 0));
        }
    }
}
