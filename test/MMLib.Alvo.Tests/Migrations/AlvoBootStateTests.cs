using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// The state the boot publishes and a readiness probe reads. Its default is the whole point: a probe that
/// answers before anything booted must answer "not ready".
/// </summary>
public sealed class AlvoBootStateTests
{
    private const string Project = "host-boot";

    [Fact]
    public void A_state_nobody_published_to_is_Pending_so_readiness_starts_closed()
    {
        var state = new AlvoBootState();

        state.Phase.ShouldBe(AlvoBootPhase.Pending);
        state.AppliedRevision.ShouldBeNull();
        state.Failure.ShouldBeNull();
    }

    [Fact]
    public void The_default_phase_is_the_closed_one()
        => default(AlvoBootPhase).ShouldBe(AlvoBootPhase.Pending);

    [Fact]
    public void A_ready_project_publishes_the_revision_it_primed_from()
    {
        var state = new AlvoBootState();

        state.Ready(Project, appliedRevision: 3);

        state.Phase.ShouldBe(AlvoBootPhase.Ready);
        state.AppliedRevision.ShouldBe(3);
        state.Failure.ShouldBeNull();
    }

    /// <summary>
    /// A boot that found no snapshot to prime from — an adopted database whose live schema already matched the
    /// descriptor, so there was nothing to apply and nothing to record — is still Ready. Readiness is "this
    /// process primed", not "a revision exists".
    /// </summary>
    /// <remarks>
    /// It is <em>not</em> <see cref="AlvoSchemaStartupMode.Skip"/> over an unrecorded database, which used to be
    /// this fact's stated example: that state is now refused by <c>SchemaStartupPolicy</c>, because under Skip
    /// nothing diffs the live schema and Ready would then mean "nobody checked".
    /// </remarks>
    [Fact]
    public void A_project_that_primed_without_a_snapshot_is_still_Ready()
    {
        var state = new AlvoBootState();

        state.Ready(Project, appliedRevision: null);

        state.Phase.ShouldBe(AlvoBootPhase.Ready);
        state.AppliedRevision.ShouldBeNull();
    }

    [Fact]
    public void A_refusal_fails_the_phase_and_keeps_the_reason_an_operator_has_to_read()
    {
        var state = new AlvoBootState();

        state.Failed(Project, "the descriptor does not match the applied schema");

        state.Phase.ShouldBe(AlvoBootPhase.Failed);
        state.Failure.ShouldBe("the descriptor does not match the applied schema");
        state.AppliedRevision.ShouldBeNull();
    }

    /// <summary>
    /// Stage 0 can fail before the descriptor has been parsed, so before any project name exists. That failure
    /// still has to leave the phase Failed rather than Pending — the reason is the only thing an operator has.
    /// </summary>
    [Fact]
    public void A_boot_that_failed_before_it_knew_the_project_still_reports_Failed()
    {
        var state = new AlvoBootState();

        state.Failed("the descriptor file does not exist");

        state.Phase.ShouldBe(AlvoBootPhase.Failed);
        state.Failure.ShouldBe("the descriptor file does not exist");
    }

    /// <summary>
    /// The state is keyed by project (#141's door, kept closed but unlocked), so one project's success cannot
    /// hide another's refusal. A single slot would report Ready here.
    /// </summary>
    [Fact]
    public void One_projects_success_cannot_hide_another_projects_refusal()
    {
        var state = new AlvoBootState();

        state.Ready("first", appliedRevision: 1);
        state.Failed("second", "second refused");

        state.Phase.ShouldBe(AlvoBootPhase.Failed);
        state.Failure.ShouldBe("second refused");
    }

    /// <summary>
    /// The revision is the single-project view of a project-keyed collection, so it is published only while
    /// exactly one project has booted. A single slot would answer with whichever project wrote last.
    /// </summary>
    [Fact]
    public void A_revision_is_published_only_while_one_project_is_booted()
    {
        var state = new AlvoBootState();

        state.Ready("first", appliedRevision: 1);
        state.Ready("second", appliedRevision: 4);

        state.Phase.ShouldBe(AlvoBootPhase.Ready);
        state.AppliedRevision.ShouldBeNull();
    }

    /// <summary>
    /// A published failure is <b>terminal for the process</b>: nothing clears it, so no later success can restore
    /// the phase. Documented as behaviour rather than left as an accident of the expression's order.
    /// </summary>
    /// <remarks>
    /// Every path that records a failure either stops the start or freezes the empty route table it was recording
    /// about, so there is no such thing here as recovering and becoming servable again — a restart builds a new
    /// state, which is the intended way out. The direction matters: the alternative (clearing the reason on a
    /// later <see cref="AlvoBootState.Ready"/>) would let a process that refused a schema report Ready while
    /// serving nothing, which is what readiness exists to prevent.
    /// </remarks>
    [Fact]
    public void A_published_failure_is_terminal_even_if_a_project_later_reports_ready()
    {
        var state = new AlvoBootState();

        state.Failed(Project, "the descriptor does not match the applied schema");
        state.Ready(Project, appliedRevision: 1);

        state.Phase.ShouldBe(AlvoBootPhase.Failed);
        state.Failure.ShouldNotBeNull();
    }

    [Fact]
    public void Re_publishing_one_project_replaces_its_entry_rather_than_adding_a_second()
    {
        var state = new AlvoBootState();

        state.Ready(Project, appliedRevision: 1);
        state.Ready(Project, appliedRevision: 2);

        state.AppliedRevision.ShouldBe(2);
    }

    /// <summary>
    /// The readiness signal a background service can await, rather than a phase it has to poll.
    /// </summary>
    /// <remarks>
    /// .NET 10 runs all of <c>BackgroundService.ExecuteAsync</c> off the startup thread, so "not before the
    /// schema is primed" is inexpressible as registration order and <c>await Task.Yield()</c> as a first line is
    /// dead code. The state has to be awaited, and this member is internal on purpose: only the dispatcher needs
    /// it, and a public awaitable would foreclose the state's shape for #141.
    /// </remarks>
    [Fact]
    public async Task Settled_completes_when_a_project_reports_ready()
    {
        var state = new AlvoBootState();
        var settled = state.SettledAsync(TestContext.Current.CancellationToken);
        settled.IsCompleted.ShouldBeFalse("a pending boot has settled on nothing yet");

        state.Ready(Project, appliedRevision: 1);

        (await settled).ShouldBe(AlvoBootPhase.Ready);
    }

    /// <summary>
    /// A refused boot settles too, so a waiter learns the answer instead of hanging on it.
    /// </summary>
    /// <remarks>
    /// Completing only on <see cref="AlvoBootPhase.Ready"/> would leave the dispatcher parked forever on a host
    /// whose boot refused — and because the host blocks in <c>StopAsync</c> waiting for <c>ExecuteAsync</c>, that
    /// parked wait would turn one refusal into a shutdown that waits out its 30-second timeout.
    /// </remarks>
    [Fact]
    public async Task Settled_completes_with_failed_when_the_boot_refused()
    {
        var state = new AlvoBootState();
        state.Failed("stage 0 refused");

        (await state.SettledAsync(TestContext.Current.CancellationToken)).ShouldBe(AlvoBootPhase.Failed);
    }

    [Fact]
    public async Task Settled_returns_immediately_when_the_boot_already_finished()
    {
        var state = new AlvoBootState();
        state.Ready(Project, appliedRevision: 1);

        state.SettledAsync(TestContext.Current.CancellationToken).IsCompleted.ShouldBeTrue(
            "a boot that finished before anyone asked must not make the asker wait for a second publication");
        (await state.SettledAsync(TestContext.Current.CancellationToken)).ShouldBe(AlvoBootPhase.Ready);
    }

    /// <summary>
    /// The wait observes its own token, which is what keeps a shutdown from waiting out the host's 30-second
    /// <c>ShutdownTimeout</c> while the boot never settles.
    /// </summary>
    /// <remarks>
    /// The bounded <c>WaitAsync</c> is there so that a wait which <em>ignores</em> the token fails this fact
    /// instead of hanging it: the boot never settles here, so an unobserved token leaves the task incomplete
    /// forever, and a hang is a timed-out CI job rather than a named failure. Measured — a version of this
    /// without the bound hung for five minutes under exactly that mutation.
    /// </remarks>
    [Fact]
    public async Task Settled_observes_its_cancellation_token_so_shutdown_never_waits_thirty_seconds()
    {
        var state = new AlvoBootState();
        using var cancellation = new CancellationTokenSource();
        var settled = state.SettledAsync(cancellation.Token);

        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => settled.WaitAsync(_cancellationBudget, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// How long a cancelled wait may take to report itself — long enough not to be flaky, short enough that a
    /// wait which ignores its token fails rather than hangs.
    /// </summary>
    private static readonly TimeSpan _cancellationBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A waiter that wakes reads a <em>settled</em> snapshot: the completion is published after the interlocked
    /// update, never before.
    /// </summary>
    /// <remarks>
    /// The failure this rules out is a race nothing else would catch — a dispatcher woken by the completion,
    /// reading <see cref="AlvoBootState.Phase"/> as still Pending, and treating a primed catalog as absent.
    /// </remarks>
    [Fact]
    public async Task A_waiter_that_wakes_reads_the_phase_the_wait_reported()
    {
        var state = new AlvoBootState();
        var observed = state.SettledAsync(TestContext.Current.CancellationToken)
            .ContinueWith(settled => (settled.Result, state.Phase), TestContext.Current.CancellationToken,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        state.Ready(Project, appliedRevision: 1);

        (await observed).ShouldBe((AlvoBootPhase.Ready, AlvoBootPhase.Ready));
    }
}
