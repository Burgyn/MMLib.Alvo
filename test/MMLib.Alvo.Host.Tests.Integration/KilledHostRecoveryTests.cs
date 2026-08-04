namespace MMLib.Alvo.Host.Tests.Integration;

/// <summary>
/// <c>baas-analyza.md:678</c>, both halves, against a host that is really killed:
/// <b>kill between commit and publish → the event is delivered after restart</b>, and
/// <b>kill mid-action → the action repeats</b>.
/// </summary>
/// <remarks>
/// <para>
/// The host runs as a <em>child process</em> against a temp SQLite file and is ended with
/// <c>Process.Kill(entireProcessTree: true)</c> — SIGKILL, no <c>StopAsync</c>, no disposal, no flush. That is the
/// only shape in this repository that exercises the crash path at all, and it is why it does not reuse
/// <c>AlvoHostWorld</c>. The in-process half of the same subject lives in
/// <c>MMLib.Alvo.Host.Tests.Events.OutboxRecoveryTests</c>, whose own summary says plainly that it proves nothing
/// about a real kill.
/// </para>
/// <para>
/// <b>The kill's exit code is asserted, and that is what makes the harness worth its cost.</b> Measured while
/// proving these facts discriminate: replacing the kill with a graceful <c>SIGTERM</c> shutdown leaves
/// <see cref="An_event_committed_before_a_kill_is_delivered_after_a_restart"/>'s <em>delivery</em> assertions
/// entirely green — a clean stop also leaves a committed event to be picked up by the next host. So the delivery
/// half cannot tell a crash from a stop, and the only assertion that can is
/// <c>ChildHostHarness.KilledExitCode</c>: 137 on Unix (128 + SIGKILL) and -1 on Windows, neither of which the
/// host's own exits (0 for a stop, 78 for a refused configuration) can produce.
/// </para>
/// <para>
/// <b>The window is proven, not assumed.</b> A crash fact whose kill lands after the work already finished passes
/// while proving nothing, so both facts read the outbox row off the database file — before the kill and again
/// after it — and assert <c>dispatched_at IS NULL</c> at the moment the process died, with the receiver's own
/// delivery log as the second witness that no delivery had yet been made in the first fact and exactly one had in
/// the second.
/// </para>
/// <para>
/// <b>The mid-action kill is deterministic, not timed.</b> The webhook receiver records the delivery and then
/// kills the child itself, from inside the request, before the response is written — so the action provably
/// reached its endpoint while <c>dispatched_at</c> provably did not get stamped. A <c>Task.Delay</c>-timed kill
/// would be a flaky approximation of the same idea.
/// </para>
/// <para>
/// <b>The repeat is proven by id, never by a count.</b> Two deliveries whose envelopes carry two different ids
/// would mean the write path emitted twice — a different defect wearing the same number — so the assertion is
/// that the set of delivered ids is a single item, and that the item is the id of the row the killed child left
/// claimed.
/// </para>
/// <para>
/// What this still does not prove: that a kill during the engine's own commit is atomic. That is the engine's
/// guarantee, not Alvo's, and Alvo's part of it — the outbox row riding the same <c>DbTransaction</c> — is proven
/// by <c>AlvoDataOutboxTests</c>.
/// </para>
/// </remarks>
public class KilledHostRecoveryTests
{
    /// <summary>
    /// <c>baas-analyza.md:678</c>, first half: an event committed before the process died is delivered by the
    /// process that replaces it.
    /// </summary>
    /// <remarks>
    /// The window between the commit and the publish is made deterministic by starting the child with its
    /// dispatcher off — emission and delivery are separate switches, so the row is committed and nothing is
    /// draining it — rather than by racing a running pump and hoping the kill lands in the gap.
    /// </remarks>
    [Fact]
    public async Task An_event_committed_before_a_kill_is_delivered_after_a_restart()
    {
        await using var harness = await ChildHostHarness.StartAsync(new ChildHostSetup { DispatcherEnabled = false });
        await harness.CreateOrderAsync();
        await ChildHostHarness.WaitOutADeliveryWindowAsync();

        var committed = harness.OutboxRows().ShouldHaveSingleItem(
            "the create must have emitted exactly one event, or the kill below lands on an empty queue");
        committed.Dispatched.ShouldBeFalse("nothing was draining the outbox, so the row must be undelivered");
        committed.Claimed.ShouldBeFalse("a dispatcher that never ran cannot have claimed it");
        harness.Receiver.Deliveries.ShouldBeEmpty(
            "no delivery may have happened before the kill — read after a window in which the mid-action fact "
            + "shows a delivery really does arrive, so this is a measured absence and not merely an early one");

        harness.Kill();

        AssertKilled(harness);
        harness.OutboxRows().ShouldHaveSingleItem().Dispatched.ShouldBeFalse(
            "read again after the process died: the kill landed between the commit and the publish, which is the "
            + "whole window this criterion is about");

        await harness.RestartAsync(new ChildHostSetup { DispatcherEnabled = true });

        var delivered = await harness.Receiver.WaitForDeliveriesAsync(count: 1);
        ChildHostHarness.EventIdOf(delivered.ShouldHaveSingleItem()).ShouldBe(
            committed.Id,
            "the event the killed process committed is the one the restart must deliver, by id — anything else "
            + "would be a new event rather than a recovered one");
        await harness.WaitUntilRetiredAsync();
    }

    /// <summary>
    /// <c>baas-analyza.md:678</c>, second half: a process killed inside an action repeats that action after a
    /// restart, and repeats it for the <em>same</em> event.
    /// </summary>
    /// <remarks>
    /// This is at-least-once delivery costing what it costs, stated as a fact rather than as a caveat in a
    /// document: the action's external effect happened, the queue never learned that it had, and the replacement
    /// process does it again once the dead claim's lease expires.
    /// </remarks>
    [Fact]
    public async Task A_kill_mid_action_makes_the_action_repeat_after_a_restart()
    {
        await using var harness = await ChildHostHarness.StartAsync(
            new ChildHostSetup { DispatcherEnabled = true, KillOnFirstDelivery = true });
        await harness.CreateOrderAsync();

        await harness.Receiver.WaitForDeliveriesAsync(count: 1);
        await harness.WaitUntilExitedAsync();

        AssertKilled(harness);
        var midAction = harness.OutboxRows().ShouldHaveSingleItem();
        midAction.Dispatched.ShouldBeFalse(
            "the action provably reached its endpoint — the receiver recorded the body before killing the child — "
            + "and dispatched_at provably was not stamped, which is what makes the repeat below mandatory");
        midAction.Claimed.ShouldBeTrue(
            "the dead process died holding the claim, and nothing but the lease hands it back");

        await harness.RestartAsync(new ChildHostSetup { DispatcherEnabled = true });

        var deliveries = await harness.Receiver.WaitForDeliveriesAsync(count: 2);
        deliveries.Count.ShouldBe(2, "the action must have run again after the restart");
        deliveries.Select(ChildHostHarness.EventIdOf).Distinct().ShouldHaveSingleItem(
            "the repeat must be the SAME event redelivered, not a second event; at-least-once delivery is the "
            + "claim, and a different id would mean the write path emitted twice")
            .ShouldBe(midAction.Id, "and it must be the entry the killed process left claimed");
        await harness.WaitUntilRetiredAsync();
    }

    /// <summary>Asserts the child died of a signal rather than of a graceful stop.</summary>
    /// <param name="harness">The harness whose child was just killed.</param>
    private static void AssertKilled(ChildHostHarness harness) => harness.ExitCode.ShouldBe(
        ChildHostHarness.KilledExitCode,
        "the child must have died of the kill, not of a stop: 0 is a graceful shutdown and 78 is a refused "
        + "configuration, so without this assertion a StopAsync-shaped shutdown would satisfy every other "
        + "assertion in this fact");
}
