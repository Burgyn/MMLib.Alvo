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
    /// A boot that read no snapshot at all — <see cref="AlvoSchemaStartupMode.Skip"/> over a database Alvo
    /// has never recorded — is still Ready. Readiness is "this process primed", not "a revision exists".
    /// </summary>
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

    [Fact]
    public void Re_publishing_one_project_replaces_its_entry_rather_than_adding_a_second()
    {
        var state = new AlvoBootState();

        state.Ready(Project, appliedRevision: 1);
        state.Ready(Project, appliedRevision: 2);

        state.AppliedRevision.ShouldBe(2);
    }
}
