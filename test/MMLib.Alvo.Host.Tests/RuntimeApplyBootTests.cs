using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The dashboard-first boot (#83): a host that receives its descriptor at runtime rather than from a file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Before this, the composition every fact here starts from could not run at all.</b> #83 described the
/// symptom as "a runtime-applied project needs one apply after every restart" — an unprimed policy catalog
/// denying everything. The truth was worse and is what these facts pin the fix for:
/// <c>DescriptorBootPlan.LoadAsync</c> threw <c>AlvoStartupRefusedException</c> when no
/// <c>IDescriptorSource</c> was configured, <c>AlvoBootService.StartingAsync</c> rethrew it, and the host
/// <em>failed to start</em>. Mode 1 of the specification — download the image, run it, open the dashboard,
/// create a project — did not exist.
/// </para>
/// <para>
/// <b>The refusal that replaced it had no test either</b>, which is its own finding: a stated start-time
/// behaviour that nothing asserts. <see cref="A_host_that_configured_neither_a_descriptor_nor_a_project_refuses_by_name"/>
/// is the fact it never had, and it now also covers the fix suggestion naming <em>both</em> right answers
/// rather than only <c>FromDescriptor</c>.
/// </para>
/// <para>
/// Design: <c>docs/superpowers/specs/2026-09-13-f5-runtime-apply-boot-design.md</c>.
/// </para>
/// </remarks>
public class RuntimeApplyBootTests
{
    /// <summary>
    /// <b>The fact #83 exists for.</b> A host with a driver and a project name — and no descriptor source —
    /// starts, resumes from the stored descriptor, and serves the entities it declares.
    /// </summary>
    /// <remarks>
    /// Asserted on <c>PrimedEntities</c> rather than on "the boot reported Ready", because Ready is what the
    /// old broken state would also have published had it got that far. The schema registry is what route
    /// generation reads its literals off and what the data port validates field names against, so an entity
    /// appearing there is the operational meaning of "this process can serve".
    /// </remarks>
    [Fact]
    public async Task A_dashboard_first_host_resumes_from_the_stored_descriptor()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();
        try
        {
            var project = await RuntimeApplyBootWorld.SeedAsync(databasePath);

            await using var world = await RuntimeApplyBootWorld.TryStartAsync(databasePath, project);

            world.StartFailure.ShouldBeNull("a dashboard-first host with a stored descriptor must start");
            world.BootState.Phase.ShouldBe(AlvoBootPhase.Ready);
            world.BootState.AppliedRevision.ShouldBe(
                1, "it resumed from the revision the seeding apply wrote, rather than applying again");
            world.PrimedEntities.ShouldNotBeEmpty(
                "the catalog and the registry are primed from the stored descriptor — this is the whole point");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// <b>A fresh <c>docker run</c> comes up.</b> An empty descriptor history starts, reports ready, and
    /// serves nothing — which is the expected first state, not a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A:557 is the binding criterion: <c>docker run mmlib/alvo</c> must be a working backend with a
    /// dashboard inside 60 s with no configuration. Refusing would delete mode 1; reporting <em>not</em>
    /// ready would keep the process alive while taking the pod out of rotation behind an ingress, which is
    /// exactly where the first-run wizard has to be reachable.
    /// </para>
    /// <para>
    /// <b>Nothing is primed, and that is the only representable state.</b>
    /// <c>schema/project.schema.json</c> puts <c>minProperties: 1</c> on <c>entities</c>, so a zero-entity
    /// descriptor is not a valid descriptor and an "empty catalog" does not exist. It is harmless: zero
    /// entities means zero Data API routes, so a data request 404s at routing before authorization is
    /// consulted at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_empty_history_starts_and_serves_nothing()
    {
        await using var world = await RuntimeApplyBootWorld.TryStartAsync(project: "brand-new");

        world.StartFailure.ShouldBeNull("a first run with no descriptor yet must come up, or mode 1 does not exist");
        world.BootState.Phase.ShouldBe(AlvoBootPhase.Ready);
        world.BootState.AppliedRevision.ShouldBeNull("nothing has been applied to this project");
        world.PrimedEntities.ShouldBeEmpty("there is no descriptor to prime from");
    }

    /// <summary>
    /// <b>And it says so, once, at warning level</b> — because "ready" and "serving nothing" read as a
    /// contradiction to the operator who has just run the image for the first time.
    /// </summary>
    /// <remarks>
    /// Asserted on what the line <em>names</em>, not on the fact that a line exists: a warning that says
    /// "ready" and stops there sends that operator looking for a bug. The two substrings are the two things
    /// they have to learn — that nothing is served, and what to do about it.
    /// </remarks>
    [Fact]
    public async Task An_empty_history_warns_naming_the_state_and_what_to_do()
    {
        await using var world = await RuntimeApplyBootWorld.TryStartAsync(project: "brand-new");

        var awaiting = world.Logs
            .Where(record => record.StartsWith("Warning: Alvo is ready for project", StringComparison.Ordinal))
            .ToList();

        var warning = awaiting.ShouldHaveSingleItem(
            "one line, at warning level, for the one state an operator will not expect");

        warning.ShouldContain(
            "serving nothing", Case.Sensitive, "the half they can observe, so they stop looking for a bug");
        warning.ShouldContain(
            "Alvo__Schema__Project",
            Case.Sensitive,
            "the half they can act on — and it is the env spelling, because that is what a container sets");
    }

    /// <summary>
    /// <b>A host that configured neither refuses by name</b>, and the fix suggestion names both answers.
    /// </summary>
    /// <remarks>
    /// <c>NoDescriptorSourceMessage</c> has been a stated start-time refusal since PR4 with nothing asserting
    /// it — <c>grep -rn NoDescriptorSource</c> matched three declarations and a doc comment. This is that
    /// fact. It also pins the half #83 added: "call FromDescriptor" was the only advice, and it is now one of
    /// two, because a dashboard-first host is not misconfigured for lacking a file.
    /// </remarks>
    [Fact]
    public async Task A_host_that_configured_neither_a_descriptor_nor_a_project_refuses_by_name()
    {
        await using var world = await RuntimeApplyBootWorld.TryStartAsync();

        world.StartFailure.ShouldNotBeNull("nothing is configured, so there is nothing to serve");
        world.BootState.Phase.ShouldBe(
            AlvoBootPhase.Failed, "a stage-0 refusal must leave Failed rather than Pending");

        var refusal = world.BootState.Failure.ShouldNotBeNull();
        refusal.ShouldContain("no project descriptor source is configured", Case.Sensitive);
        refusal.ShouldContain("FromDescriptor", Case.Sensitive, "the code-first answer");
        refusal.ShouldContain(
            "Alvo__Schema__Project",
            Case.Sensitive,
            "the dashboard-first answer — a host without a file is not necessarily misconfigured");
    }

    /// <summary>
    /// <b>A host that configured BOTH is refused rather than having one of them silently preferred.</b>
    /// </summary>
    /// <remarks>
    /// The descriptor names its own project, so the two can disagree, and picking one is how a deployment
    /// ends up serving a project nobody chose. Refusing is also the cheaper error: the operator believed
    /// something untrue about which mode they were in, and a start that succeeds teaches them nothing.
    /// </remarks>
    [Fact]
    public async Task A_host_that_configured_both_a_descriptor_and_a_project_refuses()
    {
        await using var world = await RuntimeApplyBootWorld.TryStartAsync(
            project: "some-other-project", descriptorSource: AlvoBootWorld.DefaultDescriptorFileName);

        world.StartFailure.ShouldNotBeNull("two answers that can disagree is not a configuration Alvo resolves");
        world.BootState.Phase.ShouldBe(AlvoBootPhase.Failed);

        var refusal = world.BootState.Failure.ShouldNotBeNull();
        refusal.ShouldContain("AND a project name", Case.Sensitive);
        refusal.ShouldContain(
            "some-other-project", Case.Sensitive, "naming the value, so the operator can see which one to delete");
    }
}
