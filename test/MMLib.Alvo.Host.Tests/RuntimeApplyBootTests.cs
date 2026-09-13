using MMLib.Alvo.Migrations;
using System.Net;

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
    /// <b>The security claim the whole <c>Awaiting</c> state rests on, asked of the router rather than
    /// asserted in prose.</b> A data request 404s.
    /// </summary>
    /// <remarks>
    /// "Zero entities means zero Data API routes, so a request 404s at routing before authorization is
    /// consulted" is the argument for why Ready-and-serving-nothing is safe, and it was written in five
    /// places and checked by none — <see cref="An_empty_history_starts_and_serves_nothing"/> asserts that
    /// nothing is <em>primed</em>, which is the claim's input, not the claim. This asks the host. 404 rather
    /// than 401/403 is the part that matters: it means the endpoint does not exist, so the answer cannot
    /// depend on a policy engine that has no catalog to consult.
    /// </remarks>
    [Fact]
    public async Task An_empty_history_serves_no_data_route()
    {
        await using var world = await RuntimeApplyBootWorld.TryStartAsync(project: "brand-new");
        using var client = world.Client;

        // The non-vacuity control, and it is not ceremony: without it this fact passes just as well against a
        // world that mapped nothing at all, which would make the 404 prove nothing about the route table.
        var health = await client.GetAsync(
            new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        health.StatusCode.ShouldBe(
            HttpStatusCode.OK, "MapAlvo ran and the router is answering — so the 404 below is a real miss");

        var response = await client.GetAsync(
            new Uri("/api/notes", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "the route table is empty, so this is the matcher answering — not an authorization decision taken "
            + "against a catalog that does not exist");
    }

    /// <summary>
    /// <b>An unprimed boot must not let the outbox dispatcher touch pending deliveries</b> — the worst
    /// consequence this change could have had, and the one a security pass caught.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Awaiting</c> publishes <c>Ready</c>, and the dispatcher's pump gated on exactly that. Downstream,
    /// <c>Catalog</c> throws when nothing is primed, the per-entry containment catches it and calls
    /// <c>AbandonAttemptAsync</c>, and <c>attempts</c> climbs to <c>MaxAttempts</c> — after which
    /// <c>ClaimAsync</c> excludes the entry <b>permanently</b>, and this build has no DLQ. At the shipped
    /// defaults that is about a minute.
    /// </para>
    /// <para>
    /// <b>The scenario is a typo, not an attack.</b> A wrong <c>Alvo__Schema__Project</c> against a database
    /// that <em>does</em> have pending deliveries: the outbox has no project column and the claim takes no
    /// project filter, so the entries burned are the real project's. Before #83 that host simply refused to
    /// start.
    /// </para>
    /// <para>
    /// Asserted on <c>attempts</c> rather than on the log line, because the log says what the code intended
    /// and this says what the database holds.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_unprimed_boot_leaves_pending_outbox_entries_untouched()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();
        try
        {
            _ = await RuntimeApplyBootWorld.SeedAsync(databasePath);
            var queued = await RuntimeApplyBootWorld.QueueOutboxEntryAsync(databasePath);

            await using (var world = await RuntimeApplyBootWorld.TryStartAsync(
                databasePath, project: "typo-in-the-project-name"))
            {
                world.BootState.Phase.ShouldBe(
                    AlvoBootPhase.Ready, "an unknown project is indistinguishable from a first run");
                world.PrimedEntities.ShouldBeEmpty("nothing is primed under that name");

                // Long enough that a pump gated only on Ready would have claimed and abandoned: the shipped
                // poll interval is one second.
                await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            }

            (await RuntimeApplyBootWorld.OutboxAttemptsAsync(databasePath, queued)).ShouldBe(
                0,
                "the dispatcher must stand down when nothing is primed — an abandoned attempt here is "
                + "unrecoverable once attempts reach the ceiling, and there is no dead-letter queue");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// <b>A stored descriptor this build refuses stands down rather than crash-looping the container.</b>
    /// </summary>
    /// <remarks>
    /// The code-first answer — fail the start — is right when a human can edit the file. Here the descriptor
    /// lives in the database and the only sanctioned editor is the dashboard, which is in this process, so
    /// failing the start takes away the one tool that could fix it. Reachable without anyone doing anything
    /// wrong: an older image against a row a newer one wrote (deviation 55). Standing down is not "serving
    /// anyway" — readiness reports Failed, nothing is primed, and the route table stays empty.
    /// </remarks>
    [Fact]
    public async Task A_stored_descriptor_this_build_refuses_stands_down_instead_of_failing_the_start()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();
        try
        {
            var project = await RuntimeApplyBootWorld.SeedAsync(databasePath);
            await RuntimeApplyBootWorld.CorruptStoredDescriptorAsync(databasePath, project);

            await using var world = await RuntimeApplyBootWorld.TryStartAsync(databasePath, project);

            world.StartFailure.ShouldBeNull(
                "the process must stay up — the dashboard that can repair the descriptor is inside it");
            world.BootState.Phase.ShouldBe(
                AlvoBootPhase.Failed, "it is not serving, and a readiness probe has to say so");
            world.PrimedEntities.ShouldBeEmpty("nothing was primed from a descriptor this build refuses");

            var refusal = world.BootState.Failure.ShouldNotBeNull();
            refusal.ShouldContain("this build refuses", Case.Sensitive);
            refusal.ShouldContain(
                "staying up", Case.Sensitive, "the operator has to know the process is alive on purpose");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// <b>A stored descriptor whose own name does not match the configured project is refused</b>, rather
    /// than primed under one name while the boot state uses the other.
    /// </summary>
    /// <remarks>
    /// <c>Prime</c> publishes the catalog under <c>boot.Descriptor.Name</c> while the boot reads and reports
    /// under the configured key. Letting them differ means the next runtime apply for the configured key is
    /// refused as "already primed for project X" — a dashboard that can never apply again without a restart.
    /// It is reachable today because <c>RuntimeSchemaService.ApplyAsync</c> takes the project as a parameter
    /// independent of the descriptor's own <c>name</c>.
    /// </remarks>
    [Fact]
    public async Task A_stored_descriptor_naming_another_project_is_refused()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();
        try
        {
            var project = await RuntimeApplyBootWorld.SeedAsync(databasePath);
            await RuntimeApplyBootWorld.RenameStoredDescriptorAsync(databasePath, project, "someone-else");

            await using var world = await RuntimeApplyBootWorld.TryStartAsync(databasePath, project);

            world.BootState.Phase.ShouldBe(AlvoBootPhase.Failed);
            world.PrimedEntities.ShouldBeEmpty("priming under a name the boot does not use is the defect");

            var refusal = world.BootState.Failure.ShouldNotBeNull();
            refusal.ShouldContain("someone-else", Case.Sensitive, "naming what it found, so it is fixable");
            refusal.ShouldContain("Alvo__Schema__Project", Case.Sensitive);
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
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
