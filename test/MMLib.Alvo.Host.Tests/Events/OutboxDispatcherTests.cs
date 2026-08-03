using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Api;

using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests.Events;

/// <summary>
/// The dispatcher inside a running standalone host: a delivery that can never succeed must not take the host
/// down, the switch and the ceiling really come from configuration, and a bad setting is refused before the
/// process serves anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every event here is emitted by a real write over HTTP</b>, through the composed host, so what is measured
/// is the whole path — the outbox row on the write's own transaction, the claim, the compiled hook, and the
/// delivery — rather than a queue this suite filled by hand. The endpoint is <c>127.0.0.1:1</c>, where nothing
/// listens, so every attempt is refused immediately and the failing path is the one under test.
/// </para>
/// <para>
/// <b>The readiness gate is deliberately not asserted here, and that is a structural fact rather than an
/// omission.</b> <c>AlvoBootService</c> does all of its work in <c>IHostedLifecycleService.StartingAsync</c>, and
/// the host runs every service's <c>StartingAsync</c> before <em>any</em> service's <c>StartAsync</c> — so a
/// fact that held the boot mid-flight would hold the whole <c>StartAsync</c> phase with it and the pump would
/// never have begun. It would pass because nothing ran, which is the exact vacuity a background-service test
/// invites. The gate is proven in <c>MMLib.Alvo.Tests.Events.OutboxDispatcherTests</c>, where the pump really is
/// running while the boot state is still Pending.
/// </para>
/// </remarks>
public class OutboxDispatcherTests
{
    /// <summary>
    /// Deviation 71: one poison event must not stop a host serving HTTP.
    /// </summary>
    /// <remarks>
    /// <c>HostOptions.BackgroundServiceExceptionBehavior</c> defaults to <c>StopHost</c>, and from .NET 11
    /// <c>RunAsync</c>/<c>StopAsync</c> also throw and the process exits non-zero — with the documented
    /// recommended action being "do nothing", because a failing app should fail. So the containment belongs
    /// inside the loop, and this is the fact that says the host survives it: the readiness probe still answers
    /// and the write path still accepts a row after the dispatcher has given an event up.
    /// </remarks>
    [Fact]
    public async Task A_delivery_that_always_throws_does_not_stop_the_host()
    {
        await using var world = await StartAsync();
        (await Create(world, "W-1")).StatusCode.ShouldBe(HttpStatusCode.Created);

        await UntilAsync(world, GaveUp, "the dispatcher to give the event up");

        (await world.SendAnonymouslyAsync(HttpMethod.Get, AlvoHealth.ReadinessPath))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "a poison event must not make the host unready");
        (await Create(world, "W-2")).StatusCode.ShouldBe(
            HttpStatusCode.Created, "the write path is unaffected by a delivery that cannot succeed");
    }

    /// <summary>
    /// Every failed attempt is a Warning naming the event, and the attempt ceiling comes from configuration —
    /// so the ceiling is honoured rather than hard-coded, and a retried event is visible per attempt.
    /// </summary>
    [Fact]
    public async Task The_configured_attempt_ceiling_is_what_bounds_the_retries()
    {
        await using var world = await StartAsync();
        await Create(world, "W-1");

        await UntilAsync(world, GaveUp, "the dispatcher to give the event up");

        world.Logs.Entries.Count(Failed).ShouldBe(
            ConfiguredAttempts,
            "one warning per attempt, and the ceiling is the configured one — a hard-coded ceiling would show "
            + "its own number here");
        world.Logs.Entries.Single(GaveUp).Message.ShouldContain("dispatched_at");
    }

    /// <summary>
    /// The switch really is read from configuration: with the dispatcher off, the same descriptor and the same
    /// write produce no delivery attempt at all.
    /// </summary>
    /// <remarks>
    /// Paired with <see cref="A_delivery_that_always_throws_does_not_stop_the_host"/>, which shows the attempts
    /// arrive well inside this wait, so the absence below is a measured absence rather than an unfinished one.
    /// </remarks>
    [Fact]
    public async Task The_dispatcher_can_be_switched_off_entirely()
    {
        await using var world = await StartAsync(enabled: false);
        (await Create(world, "W-1")).StatusCode.ShouldBe(HttpStatusCode.Created);

        await Task.Delay(_absenceBudget, TestContext.Current.CancellationToken);

        world.Logs.Entries.ShouldNotContain(
            entry => Failed(entry) || GaveUp(entry),
            "the queue still fills — the write emitted its event — and nothing drains it");
    }

    /// <summary>
    /// A batch size nothing could claim with is refused at startup, naming the key an operator sets and a value
    /// that would have worked.
    /// </summary>
    /// <remarks>
    /// Before the process serves anything, because the dispatcher contains its own failures: a bad value
    /// discovered inside the pump would be one log line in a host that otherwise looks entirely healthy.
    /// </remarks>
    [Fact]
    public async Task A_batch_size_of_zero_is_refused_at_startup_naming_the_key_and_a_usable_value()
    {
        var refusal = await Should.ThrowAsync<OptionsValidationException>(
            () => AlvoHostWorld.StartAsync(Descriptor, Settings(batchSize: "0")));

        refusal.Message.ShouldContain("Alvo:Events:BatchSize");
        refusal.Message.ShouldContain("Alvo__Events__BatchSize");
        refusal.Message.ShouldContain("1");
    }

    /// <summary>
    /// A lease shorter than the poll interval is refused too, and the refusal names both keys — the pair is what
    /// is wrong, not either value on its own.
    /// </summary>
    /// <remarks>
    /// A lease under the poll interval re-claims an entry that is still in flight on the very next tick, which
    /// is a duplicate delivery per tick rather than at-least-once delivery.
    /// </remarks>
    [Fact]
    public async Task A_claim_lease_shorter_than_the_poll_interval_is_refused_at_startup()
    {
        var refusal = await Should.ThrowAsync<OptionsValidationException>(
            () => AlvoHostWorld.StartAsync(Descriptor, Settings(claimLease: "00:00:00.010")));

        refusal.Message.ShouldContain("Alvo:Events:ClaimLease");
        refusal.Message.ShouldContain("Alvo:Events:PollInterval");
    }

    /// <summary>
    /// A shutdown returns promptly rather than waiting out the host's <c>ShutdownTimeout</c>.
    /// </summary>
    /// <remarks>
    /// The host blocks in <c>StopAsync</c> waiting for <c>ExecuteAsync</c>, with a 30 s default
    /// <c>ShutdownTimeout</c>, so a pump that ignored its token would turn every clean stop into a half-minute
    /// hang. Measured with the pump busy failing deliveries, which is the state that has something to abandon.
    /// </remarks>
    [Fact]
    public async Task A_shutdown_returns_promptly_rather_than_waiting_out_the_shutdown_timeout()
    {
        var world = await StartAsync();
        await Create(world, "W-1");
        await UntilAsync(world, Failed, "the first delivery attempt");

        var started = DateTimeOffset.UtcNow;
        await world.DisposeAsync();
        var elapsed = DateTimeOffset.UtcNow - started;

        elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(5),
            "the host blocks in StopAsync waiting for ExecuteAsync, with a 30 s ShutdownTimeout, so the loop "
            + "must observe its cancellation token promptly");
    }

    private const string Descriptor = "host-boot-poison-hook.alvo.json";
    private const int ConfiguredAttempts = 2;

    private static readonly TimeSpan _absenceBudget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _waitBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _pollDelay = TimeSpan.FromMilliseconds(20);

    private static Task<AlvoHostWorld> StartAsync(bool enabled = true) =>
        AlvoHostWorld.StartAsync(Descriptor, Settings(enabled: enabled ? "true" : "false"));

    /// <summary>
    /// The event settings every world here runs with: a fast pump, a low ceiling, and a lease that still
    /// outlasts the interval.
    /// </summary>
    private static Dictionary<string, string?> Settings(
        string enabled = "true",
        string batchSize = "10",
        string claimLease = "00:00:05") =>
        new(StringComparer.Ordinal)
        {
            ["Alvo:Events:Enabled"] = enabled,
            ["Alvo:Events:PollInterval"] = "00:00:00.050",
            ["Alvo:Events:BatchSize"] = batchSize,
            ["Alvo:Events:MaxAttempts"] = ConfiguredAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Alvo:Events:ClaimLease"] = claimLease,
        };

    private static Task<HttpResponseMessage> Create(AlvoHostWorld world, string code) =>
        world.SendAsync(HttpMethod.Post, "/api/warehouses", new JsonObject
        {
            ["code"] = code,
            ["city"] = "Bratislava",
        });

    private static bool Failed(LoggedRecord entry) =>
        entry.Level == LogLevel.Warning && entry.Message.Contains("failed to deliver", StringComparison.Ordinal);

    private static bool GaveUp(LoggedRecord entry) =>
        entry.Level == LogLevel.Error && entry.Message.Contains("gave up", StringComparison.Ordinal);

    /// <summary>Waits for a log record the host really wrote, and throws rather than giving up quietly.</summary>
    private static async Task UntilAsync(AlvoHostWorld world, Func<LoggedRecord, bool> matches, string what)
    {
        var deadline = DateTimeOffset.UtcNow + _waitBudget;
        while (!world.Logs.Entries.Any(matches))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Waited {_waitBudget} for {what}. The host wrote: "
                    + string.Join(" | ", world.Logs.Records));
            }

            await Task.Delay(_pollDelay, TestContext.Current.CancellationToken);
        }
    }
}
