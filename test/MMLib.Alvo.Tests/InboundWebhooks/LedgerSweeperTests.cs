using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.InboundWebhooks.Internal;

namespace MMLib.Alvo.Tests.InboundWebhooks;

/// <summary>The hourly sweep of both framework ledgers (Rulings 26, 41): never fatal, and quiet when it forgot nothing.</summary>
public sealed class LedgerSweeperTests : IDisposable
{
    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _factory;
    private readonly ServiceProvider _services;
    private readonly SweptLedger _webhooks = new();

    public LedgerSweeperTests()
    {
        _factory = LoggerFactory.Create(logging => logging.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        _services = new ServiceCollection().AddSingleton<IInboundWebhookLedger>(_webhooks).BuildServiceProvider();
    }

    /// <summary>A provider's timeout surfaced as a cancellation is a failed sweep, not a host shutdown.</summary>
    [Fact]
    public async Task A_cancellation_that_is_not_the_hosts_is_a_failed_sweep_and_never_escapes()
    {
        _webhooks.Sweep = _ => throw new TaskCanceledException("a command timeout");

        var swept = await Sweeper().SweepOnceAsync(TestContext.Current.CancellationToken);

        swept.ShouldBe(0);
        _logs.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Exception is TaskCanceledException);
    }

    [Fact]
    public async Task The_hosts_own_stop_still_cancels_the_sweep()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        _webhooks.Sweep = token => throw new OperationCanceledException(token);

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
        var swept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _webhooks.Sweep = _ =>
        {
            swept.TrySetResult();
            return 0;
        };
        await sweeper.StartAsync(CancellationToken.None);
        await swept.Task;

        sweeper.Dispose();

        await Should.NotThrowAsync(() => sweeper.ExecuteTask!);
        sweeper.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task A_provider_failure_while_the_host_stops_is_logged_and_never_escapes()
    {
        var sweeper = Sweeper();
        _webhooks.Sweep = _ =>
        {
            sweeper.Dispose();
            throw new InvalidOperationException("the provider");
        };

        await Should.NotThrowAsync(async () =>
        {
            await sweeper.StartAsync(CancellationToken.None);
            await sweeper.ExecuteTask!;
        });

        _logs.Entries.ShouldContain(entry => entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task A_sweep_that_forgot_nothing_logs_below_information()
    {
        _ = await Sweeper().SweepOnceAsync(TestContext.Current.CancellationToken);

        _logs.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Information);
        _logs.Entries.ShouldContain(entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task A_sweep_that_forgot_something_says_how_much_at_information()
    {
        _webhooks.Sweep = _ => 2;

        (await Sweeper().SweepOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(2);

        _logs.Entries.ShouldContain(entry => entry.Level == LogLevel.Information && entry.Message.Contains("Swept 2 inbound webhook ids", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _services.Dispose();
        _factory.Dispose();
        _logs.Dispose();
    }

    /// <summary>A ledger whose sweep is the fact's; NSubstitute cannot proxy an internal Abstractions port.</summary>
    private sealed class SweptLedger : IInboundWebhookLedger
    {
        internal Func<CancellationToken, int> Sweep { get; set; } = _ => 0;

        public Task<WebhookClaimResult> ClaimAsync(string receiver, string webhookId, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> CompleteAsync(string receiver, string webhookId, string token, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(string receiver, string webhookId, string token, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> SweepAsync(DateTimeOffset receivedBefore, CancellationToken cancellationToken) =>
            Task.FromResult(Sweep(cancellationToken));
    }

    private LedgerSweeper Sweeper() => new(_services, new FakeTimeProvider(), _factory.CreateLogger<LedgerSweeper>());
}
