using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Tests.Events;

using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests.Events;

/// <summary>
/// Recovery facts over an in-process host. <b>None of these exercises a real process kill.</b>
/// </summary>
/// <remarks>
/// <para>
/// <c>AlvoHostWorld</c> runs over <c>TestServer</c> and a graceful stop calls <c>StopAsync</c>, so a simulated
/// crash here is a dispatcher that never claimed, or a claim abandoned by disposal — not a process that died
/// mid-write. What that leaves unproven is exactly one thing: that an OS-level kill between the engine's commit
/// and the dispatcher's claim loses nothing. That is <c>KilledHostRecoveryTests</c>' job, in
/// <c>MMLib.Alvo.Host.Tests.Integration</c>, and the two files exist separately so neither can be mistaken for
/// the other.
/// </para>
/// <para>
/// <b>What these two do prove, and the crash facts cannot.</b> They isolate the <em>store's</em> recovery from
/// the process's: the first says an event committed with nothing draining it survives to be drained by a later
/// host, the second says a claim nobody will ever finish is recovered by its lease and by nothing else. Both are
/// preconditions of the crash criterion, and both fail in seconds rather than after a publish and two boots.
/// </para>
/// <para>
/// <b>Every claim about the queue is read off the database file</b> (<see cref="SqliteOutboxProbe"/>) rather than
/// off the host, because a host reporting its own queue state is the same process whose failure the fact is
/// about. The delivered event's <em>id</em> is compared against the row's id, so "something was delivered"
/// cannot stand in for "that event was delivered".
/// </para>
/// </remarks>
public class OutboxRecoveryTests
{
    /// <summary>
    /// An event committed while nothing was draining the outbox is delivered by the next host to start over the
    /// same database.
    /// </summary>
    /// <remarks>
    /// The window is made deterministic by switching the pump off rather than by racing it: with
    /// <c>Alvo:Events:Enabled</c> false the write still emits — emission and delivery are separate switches — so
    /// the row is provably committed and provably undelivered before the first host goes away.
    /// </remarks>
    [Fact]
    public async Task An_event_committed_while_the_dispatcher_was_off_is_delivered_by_the_next_host()
    {
        var database = AlvoHostWorld.TempDatabasePath();
        try
        {
            var committed = await CommitOneEventWithNoDispatcherAsync(database);

            await DeliveredByTheNextHostAsync(
                database,
                committed,
                Settings(),
                "the event the first host committed is the one that must arrive, by id — a delivery of anything "
                + "else would mean the second host emitted an event of its own rather than recovering this one");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(database);
        }
    }

    /// <summary>
    /// A claim held by a host that went away is recovered once its lease expires, and the entry is delivered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lease is the only mechanism that recovers a claim nobody will finish — nothing releases it, nothing
    /// times it out at the store — so this is the fact the crash criterion's second half depends on. The first
    /// host holds a delivery open until it is disposed, which leaves <c>claimed_at</c> set and
    /// <c>dispatched_at</c> unset, exactly as a killed process would.
    /// </para>
    /// <para>
    /// <b>The second host's lease is short, and that is the whole instrument.</b> A lease longer than the fact's
    /// own patience would make this pass by timing out rather than by recovering, so the second host runs with
    /// the shortest lease the options validation accepts above its poll interval.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_claim_abandoned_by_a_disposed_host_is_recovered_after_its_lease_expires()
    {
        var database = AlvoHostWorld.TempDatabasePath();
        try
        {
            var abandoned = await AbandonOneClaimAsync(database);

            await DeliveredByTheNextHostAsync(
                database,
                abandoned,
                Settings(claimLease: ShortLease),
                "the abandoned claim's own entry is what the lease must hand back");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(database);
        }
    }

    /// <summary>
    /// Starts a second host over the same database and asserts it delivers and retires <paramref name="pending"/>.
    /// </summary>
    /// <param name="database">The database the first host left the entry in.</param>
    /// <param name="pending">The row the first host left undelivered.</param>
    /// <param name="settings">The event settings the second host runs with.</param>
    /// <param name="because">Why this entry, by id, is the one that had to arrive.</param>
    private static async Task DeliveredByTheNextHostAsync(
        string database, OutboxRowState pending, Dictionary<string, string?> settings, string because)
    {
        var receiver = new RecordingWebhookHandler();
        await using var second = await StartAsync(database, receiver, settings);

        var delivered = await UntilDeliveredAsync(receiver, count: 1);
        EventIdOf(delivered[0]).ShouldBe(pending.Id, because);
        await UntilRetiredAsync(database, pending.Id);
    }

    /// <summary>
    /// Commits one event through a host whose dispatcher is off, and hands back the row it left behind.
    /// </summary>
    /// <param name="database">The database both hosts run over.</param>
    private static async Task<OutboxRowState> CommitOneEventWithNoDispatcherAsync(string database)
    {
        await using (var first = await StartAsync(database, new RecordingWebhookHandler(), Settings(enabled: "false")))
        {
            await CreateAsync(first, "W-1");
        }

        var row = SqliteOutboxProbe.Rows(database).ShouldHaveSingleItem(
            "the write must have emitted exactly one event, or nothing below is about a committed event");
        row.Dispatched.ShouldBeFalse("nothing drained the queue, so the row must still be undelivered");
        row.Claimed.ShouldBeFalse("a dispatcher that never ran cannot have claimed it");

        return row;
    }

    /// <summary>
    /// Leaves one entry claimed by a host that is then disposed, and hands back the row it abandoned.
    /// </summary>
    /// <param name="database">The database both hosts run over.</param>
    private static async Task<OutboxRowState> AbandonOneClaimAsync(string database)
    {
        var hanging = new HangingWebhookHandler();
        await using (var first = await StartAsync(database, hanging, Settings()))
        {
            await CreateAsync(first, "W-1");
            await hanging.UntilRequestedAsync();
        }

        var row = SqliteOutboxProbe.Rows(database).ShouldHaveSingleItem();
        row.Claimed.ShouldBeTrue("the first host was disposed mid-delivery, so its claim must still be on the row");
        row.Dispatched.ShouldBeFalse("a delivery that never completed must not have retired the entry");

        return row;
    }

    private static Task<AlvoHostWorld> StartAsync(
        string database, HttpMessageHandler receiver, Dictionary<string, string?> settings) =>
        AlvoHostWorld.StartAsync(
            Descriptor,
            settings,
            database,
            builder => builder.Services
                .AddHttpClient(WebhookDelivery.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => receiver));

    /// <summary>The event settings every world here runs with: a fast pump and a lease that outlasts it.</summary>
    /// <param name="enabled">Whether this host drains the outbox.</param>
    /// <param name="claimLease">How long a claim holds; the default outlasts anything this file waits for.</param>
    private static Dictionary<string, string?> Settings(string enabled = "true", string claimLease = "00:05:00") =>
        new(StringComparer.Ordinal)
        {
            ["Alvo:Events:Enabled"] = enabled,
            ["Alvo:Events:PollInterval"] = PollInterval,
            ["Alvo:Events:BatchSize"] = "10",
            ["Alvo:Events:MaxAttempts"] = "10",
            ["Alvo:Events:ClaimLease"] = claimLease,
        };

    private static async Task CreateAsync(AlvoHostWorld world, string code)
    {
        var response = await world.SendAsync(HttpMethod.Post, "/api/warehouses", new JsonObject
        {
            ["code"] = code,
            ["city"] = "Bratislava",
        });

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created, "the write must have been accepted, or there is no event to recover");
    }

    /// <summary>Waits until <paramref name="count"/> deliveries have arrived, and throws rather than under-counting.</summary>
    /// <param name="receiver">The handler standing in for the network under the named webhook client.</param>
    /// <param name="count">How many deliveries to wait for.</param>
    private static async Task<IReadOnlyList<string>> UntilDeliveredAsync(RecordingWebhookHandler receiver, int count)
    {
        await UntilAsync(
            () => receiver.Bodies.Count >= count,
            $"{count} delivery/deliveries; {receiver.Bodies.Count} arrived");

        return receiver.Bodies;
    }

    /// <summary>Waits until the outbox row for <paramref name="id"/> is retired.</summary>
    /// <param name="database">The database file to read.</param>
    /// <param name="id">The event id whose row must end up with <c>dispatched_at</c> set.</param>
    /// <remarks>
    /// Asserted after the delivery rather than instead of it: the dispatcher retires an entry only once every
    /// matched action has run, so an entry still pending here would mean the delivery was counted without the
    /// queue ever agreeing.
    /// </remarks>
    private static Task UntilRetiredAsync(string database, Guid id) => UntilAsync(
        () => SqliteOutboxProbe.Rows(database).Single(row => row.Id == id).Dispatched,
        $"the outbox row for {id} to be retired");

    /// <summary>Polls <paramref name="satisfied"/> until it holds, and throws when the budget runs out.</summary>
    /// <param name="satisfied">The condition being waited for.</param>
    /// <param name="what">What the failure message says was being waited for.</param>
    private static async Task UntilAsync(Func<bool> satisfied, string what)
    {
        var deadline = DateTimeOffset.UtcNow + _waitBudget;
        while (!satisfied())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited {_waitBudget} for {what}.");
            }

            await Task.Delay(_pollDelay, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The <c>id</c> of the CloudEvents envelope a delivery carried.</summary>
    /// <param name="body">The delivery's request body.</param>
    private static Guid EventIdOf(string body) =>
        Guid.Parse((JsonNode.Parse(body) as JsonObject)!["id"]!.GetValue<string>());

    private const string Descriptor = "host-boot-poison-hook.alvo.json";
    private const string PollInterval = "00:00:00.050";

    /// <summary>
    /// The shortest claim lease the options validation accepts above <see cref="PollInterval"/>.
    /// </summary>
    /// <remarks>
    /// A lease under the poll interval is refused at startup, because it re-claims an entry that is still in
    /// flight on the very next tick. This is the value just above that floor, so the recovery a fact waits for is
    /// the lease expiring rather than the fact's own patience running out.
    /// </remarks>
    private const string ShortLease = "00:00:00.100";

    private static readonly TimeSpan _waitBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _pollDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>Records every delivery the named webhook client made, and answers <c>200 OK</c>.</summary>
    /// <remarks>
    /// Installed as the named client's <em>primary handler</em>, so the factory, the named configuration, the
    /// content type and the timeout all stay in the path and only the socket is stood in for — the same seam
    /// <c>RecordingWebhookReceiver</c> uses, for the same reason.
    /// </remarks>
    private sealed class RecordingWebhookHandler : HttpMessageHandler
    {
        /// <summary>Every delivery's body, in arrival order.</summary>
        internal IReadOnlyList<string> Bodies
        {
            get
            {
                lock (_gate)
                {
                    return [.. _bodies];
                }
            }
        }

        /// <inheritdoc/>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _bodies.Add(body);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private readonly List<string> _bodies = [];
        private readonly object _gate = new();
    }

    /// <summary>
    /// Holds every delivery open until the host is shut down, so disposal abandons a claim rather than finishing
    /// one.
    /// </summary>
    private sealed class HangingWebhookHandler : HttpMessageHandler
    {
        /// <summary>Completes as soon as the dispatcher has claimed an entry and begun delivering it.</summary>
        internal Task UntilRequestedAsync() => _requested.Task.WaitAsync(TimeSpan.FromSeconds(30));

        /// <inheritdoc/>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requested.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private readonly TaskCompletionSource _requested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
