using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The boot sequence as a hosted lifecycle service: what runs, when it runs relative to the server, and what
/// it publishes for a readiness probe to read.
/// </summary>
/// <remarks>
/// Measured over a real host through <see cref="AlvoBootWorld"/>, whose remarks say why it is not
/// <see cref="AlvoHostWorld"/>. The decision table itself is a unit test
/// (<c>SchemaStartupDecisionTests</c>); what is left here is the carrying out of it.
/// </remarks>
public class AlvoBootServiceTests
{
    /// <summary>
    /// The guarantee the whole design rests on, and the one deviation 38 protects: nothing can reach Alvo
    /// before the schema it serves has been decided.
    /// </summary>
    /// <remarks>
    /// The listening half is the fact. Asserting only <c>Phase == Ready</c> would pass from any lifecycle hook,
    /// including one that runs after the server is already answering — which is the regression this exists to
    /// catch, and the mutation (<c>StartingAsync</c> → <c>StartedAsync</c>) that proves it is not vacuous.
    /// </remarks>
    [Fact]
    public async Task The_boot_runs_before_the_server_listens()
    {
        await using var world = await AlvoBootWorld.StartAsync();

        world.DescriptorReads.ShouldBe(
            1, "the boot must really have read the descriptor, or the observation below is of nothing");
        world.ServerWasListeningDuringBoot.ShouldBeFalse();
        world.BootState.Phase.ShouldBe(AlvoBootPhase.Ready);
    }

    /// <summary>
    /// A first boot over an empty database initializes it — in no configured mode at all — and publishes the
    /// revision it wrote, which is what readiness compares against.
    /// </summary>
    [Fact]
    public async Task A_successful_boot_publishes_the_applied_revision()
    {
        await using var world = await AlvoBootWorld.StartAsync();

        world.BootState.AppliedRevision.ShouldBe(1);
        world.BootState.Failure.ShouldBeNull();
        world.PrimedEntities.ShouldContain("warehouses");
    }

    /// <summary>
    /// A descriptor that cannot be applied stops the start, and leaves behind a state that says so plus a fix
    /// an operator can act on.
    /// </summary>
    /// <remarks>
    /// The refusal type and the fix suggestion are both the fact. A start that failed because the fixture
    /// mistyped a path would satisfy "it threw" just as well, so the message has to name the step that was
    /// refused. The unprimed catalog is the other half: a refused boot must not leave a policy catalog behind
    /// that something could serve from.
    /// </remarks>
    [Fact]
    public async Task A_descriptor_that_cannot_apply_leaves_the_state_Failed_and_stops_the_start()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();

        try
        {
            await InitializeAsync(databasePath);

            await using var refused = await AlvoBootWorld.TryStartAsync(
                AlvoBootWorld.DroppedFieldDescriptorFileName, databasePath);

            var refusal = refused.StartFailure.ShouldBeOfType<AlvoStartupRefusedException>();
            refusal.Message.ShouldContain("warehouses.city");
            refusal.FixSuggestion.ShouldNotBeNullOrWhiteSpace();
            refused.BootState.Phase.ShouldBe(AlvoBootPhase.Failed);
            refused.BootState.Failure.ShouldNotBeNull().ShouldContain("warehouses.city");
            refused.PrimedEntities.ShouldBeEmpty(
                "a refused boot must leave the policy catalog unprimed, so every operation denies");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// The gap <c>RuntimeSchemaService</c>'s own remarks call real: a restart that applies nothing must still
    /// prime, or the process comes back denying everything.
    /// </summary>
    /// <remarks>
    /// The revision staying at 1 is what makes this a priming fact rather than a second apply — a boot that
    /// re-applied would have written revision 2.
    /// </remarks>
    [Fact]
    public async Task An_unchanged_restart_still_primes_the_schema_without_applying_anything()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();

        try
        {
            await InitializeAsync(databasePath);

            await using var restarted = await AlvoBootWorld.StartAsync(databasePath: databasePath);

            restarted.PrimedEntities.ShouldContain(
                "warehouses",
                "an unchanged restart applies no DDL, so priming is the only thing that can put the entity "
                + "in the registry the routes and the field validation read");
            restarted.BootState.AppliedRevision.ShouldBe(1);
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// Drift under <c>Apply</c> is applied and the new revision published — the branch that proves the boot
    /// advances the applied snapshot rather than re-writing revision 1.
    /// </summary>
    [Fact]
    public async Task A_drift_applied_on_boot_advances_the_applied_revision()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();

        try
        {
            await InitializeAsync(databasePath);

            await using var migrated = await AlvoBootWorld.StartAsync(
                AlvoBootWorld.AddedFieldDescriptorFileName, databasePath, AlvoSchemaStartupMode.Apply);

            migrated.BootState.Phase.ShouldBe(AlvoBootPhase.Ready);
            migrated.BootState.AppliedRevision.ShouldBe(2);
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// The same drift under <c>Verify</c> refuses, naming the step and the setting that would allow it — the
    /// non-vacuity control for the fact above.
    /// </summary>
    /// <remarks>
    /// The mode is configured explicitly because <c>Apply</c> is the default: a version of this fact that
    /// relied on the default would have flipped meaning with it and gone green by applying the drift it exists
    /// to see refused.
    /// </remarks>
    [Fact]
    public async Task The_same_drift_under_Verify_refuses_and_names_the_setting_that_would_apply_it()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();

        try
        {
            await InitializeAsync(databasePath);

            await using var refused = await AlvoBootWorld.TryStartAsync(
                AlvoBootWorld.AddedFieldDescriptorFileName, databasePath, AlvoSchemaStartupMode.Verify);

            var refusal = refused.StartFailure.ShouldBeOfType<AlvoStartupRefusedException>();
            refusal.FixSuggestion.ShouldContain("Alvo__Schema__Startup=Apply");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// <c>Skip</c> over a database Alvo has recorded nothing for, and whose schema is not there either, refuses —
    /// rather than reporting <c>Ready</c> over a schema nothing verified.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scenario is an operator who set <c>Skip</c> because "the migration job owns the schema" and whose job
    /// never ran. Serving published <c>Ready</c> with <c>AppliedRevision</c> null — the phase and the revision
    /// contradicting each other — so every replica answered 200 to a readiness probe, traffic was routed, and
    /// every request died at the SQL layer.
    /// </para>
    /// <para>
    /// End to end rather than only in <c>SchemaStartupDecisionTests</c>, because the decision is not the defect:
    /// the boot service publishing <c>Ready</c> from what the decision returned is, and only a real boot exercises
    /// that.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Skip_over_a_database_alvo_has_recorded_nothing_for_refuses_rather_than_reporting_ready()
    {
        await using var refused = await AlvoBootWorld.TryStartAsync(startup: AlvoSchemaStartupMode.Skip);

        var refusal = refused.StartFailure.ShouldBeOfType<AlvoStartupRefusedException>(
            "Skip must not serve a schema nothing has verified");
        refusal.FixSuggestion.ShouldContain("Alvo__Schema__Startup=Verify");
        refused.BootState.Phase.ShouldBe(AlvoBootPhase.Failed);
        refused.BootState.AppliedRevision.ShouldBeNull();
    }

    /// <summary>
    /// What <c>Skip</c> is <em>for</em>: a recorded schema is served as it stands, the drift the boot read is
    /// ignored, and no DDL runs.
    /// </summary>
    /// <remarks>
    /// The revision staying at 1 while the descriptor asks for a second field is the whole fact — under
    /// <c>Apply</c> the same start writes revision 2 (<see cref="A_drift_applied_on_boot_advances_the_applied_revision"/>)
    /// and under <c>Verify</c> it refuses. It is also the end-to-end half of deviation 58: the snapshot was read,
    /// or there would be no revision to publish, and what it found was ignored.
    /// </remarks>
    [Fact]
    public async Task Skip_serves_the_recorded_schema_and_ignores_the_drift_it_read()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();

        try
        {
            await InitializeAsync(databasePath);

            await using var skipped = await AlvoBootWorld.StartAsync(
                AlvoBootWorld.AddedFieldDescriptorFileName, databasePath, AlvoSchemaStartupMode.Skip);

            skipped.BootState.Phase.ShouldBe(AlvoBootPhase.Ready);
            skipped.BootState.AppliedRevision.ShouldBe(
                1, "Skip applies nothing, so the recorded revision is the one it serves");
            skipped.PrimedEntities.ShouldContain("warehouses");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// A host that attached a driver and forgot the descriptor is refused by name — and the refusal is
    /// <b>recorded</b>, so the phase is <c>Failed</c> rather than <c>Pending</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recording is the half that was missing: stage 0 raises this refusal before any project name exists,
    /// and <c>StartingAsync</c> rethrows an <c>AlvoStartupRefusedException</c> untouched so it cannot overwrite a
    /// project-scoped failure — so nothing wrote it down at all, and an embedded host holding the state could not
    /// tell "no descriptor configured" from "the boot has not run yet". <c>AlvoBootState.Failed(string)</c>'s own
    /// remarks require the opposite, which is what makes this a contract violation rather than a preference.
    /// </para>
    /// <para>
    /// Built inline rather than through <see cref="AlvoBootWorld"/>, because that world always attaches a
    /// descriptor — the whole subject here is the composition that does not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_host_with_no_descriptor_source_is_refused_by_name_and_the_refusal_is_recorded()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAlvo(alvo => alvo.UseSqlite($"Data Source={databasePath}"));

        await using var app = builder.Build();

        try
        {
            var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(
                () => app.StartAsync(TestContext.Current.CancellationToken));
            refusal.FixSuggestion.ShouldContain("FromDescriptor");

            var state = app.Services.GetRequiredService<AlvoBootState>();
            state.Phase.ShouldBe(
                AlvoBootPhase.Failed, "a stage-0 refusal must not be indistinguishable from a boot that never ran");
            state.Failure.ShouldNotBeNull().ShouldContain("FromDescriptor");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// A refused boot binds <b>no</b> socket even under <c>HostOptions.ServicesStartConcurrently</c> — the one
    /// supported option that could plausibly have let a host start degraded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured because it was claimed to be the other way round.</b> That option flips the host's
    /// <c>abortOnFirstException</c> to <see langword="false"/>, which does mean every <em>service</em> in a phase
    /// gets its turn — so the reading that the start then continues into the <c>IHostedService.StartAsync</c> that
    /// binds the socket is a natural one. It is wrong: <c>Host.StartAsync</c> calls its own <c>LogAndRethrow</c>
    /// after <em>each</em> phase, so collected <c>StartingAsync</c> exceptions abort the start before the web host
    /// service is ever started (.NET 10; the fixture's client cannot be created, which for
    /// <see cref="TestServer"/> is the spelling of "the server was never started").
    /// </para>
    /// <para>
    /// It is worth pinning rather than deleting, because it is the <em>only</em> composition that could break the
    /// strong end of deviation 38's guarantee, and because it says what the readiness barrier is and is not for:
    /// not for a refused boot, which cannot serve at all, but for a state published <em>after</em> a successful
    /// boot — a route table that refuses the applied schema is today's reachable one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_refused_boot_binds_no_socket_even_when_services_start_concurrently()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();

        try
        {
            await InitializeAsync(databasePath);

            await using var refused = await AlvoBootWorld.TryStartAsync(
                AlvoBootWorld.DroppedFieldDescriptorFileName,
                databasePath,
                startServicesConcurrently: true);

            refused.StartFailure.ShouldNotBeNull("the destructive descriptor must still refuse the start");
            refused.BootState.Phase.ShouldBe(AlvoBootPhase.Failed);
            refused.ServerIsListening.ShouldBeFalse(
                "a refused start must not leave a bound socket, whichever way the host was told to start its "
                + "services");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// The rollback under <c>Apply</c>: a boot holding the descriptor the database has already moved on from
    /// <b>starts</b>, reports not ready, and says which revision it is versus which one the database is at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is #145's acceptance criterion, and the three halves are each a separate regression.</b> Before
    /// the ordering gate this exact sequence was a <c>DropField</c> refusal — correct, and diagnosed as
    /// "destructive change refused", which sends an operator to
    /// <c>Alvo__Schema__AllowDestructive=true</c> (discarding the column's data) to recover from being one
    /// deploy behind. It also exited 78, so every pod of the rolled-back deployment crash-looped.
    /// </para>
    /// <para>
    /// So: the start must <em>not</em> throw (the pod is drained, not killed); the phase must still be
    /// <c>Failed</c> with nothing primed (a process that stood down must be structurally unable to serve); the
    /// message must name both revisions (or it is the same unactionable sentence as before); and the history must
    /// be untouched (the whole point is that the older descriptor did not apply).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_boot_holding_an_older_descriptor_starts_not_ready_instead_of_crash_looping()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();

        try
        {
            await InitializeAsync(databasePath);
            await using (await AlvoBootWorld.StartAsync(
                AlvoBootWorld.AddedFieldDescriptorFileName, databasePath, AlvoSchemaStartupMode.Apply))
            {
            }

            await using var rolledBack = await AlvoBootWorld.TryStartAsync(databasePath: databasePath);

            rolledBack.StartFailure.ShouldBeNull(
                "a pod that is merely one deploy behind must be drained by its orchestrator, not restart-looped");
            rolledBack.BootState.Phase.ShouldBe(AlvoBootPhase.Failed);
            rolledBack.BootState.AppliedRevision.ShouldBeNull();
            rolledBack.PrimedEntities.ShouldBeEmpty(
                "a process that stood down must leave the policy catalog unprimed, so every operation denies");

            var reason = rolledBack.BootState.Failure.ShouldNotBeNull();
            reason.ShouldContain("older descriptor");
            reason.ShouldContain("revision 1");
            reason.ShouldContain("revision 2");

            (await rolledBack.RecordedRevisionsAsync("host-boot")).ShouldBe(
                [1, 2], "the older descriptor must not have applied anything, in either direction");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// The hazard no other gate can see: a difference that is destructive in <b>neither</b> direction — an
    /// index — oscillated between two deployed descriptors on every boot. Now the older one stands down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the fact the ordering gate exists for, and the only one that would have gone <em>green by
    /// applying</em> before it.</b> `DestructiveScan` sets no `IsDestructive` on any index, constraint or
    /// foreign-key operation, in either direction, so `AddIndex` one way and `DropIndex` the other both sail
    /// through the destructive gate and through `Apply`: two pods holding these two descriptors flip the index
    /// on and off forever, each reporting `Ready` over a schema the other is about to change. Removing the
    /// ordering gate turns this fact red by recording revision 3 — the oscillation itself — which is what makes
    /// it discriminating rather than a second spelling of the rollback fact above.
    /// </para>
    /// <para>
    /// The declared-rename pair is the same shape and is not pinned here: it needs both descriptors to declare
    /// mutually inverse `renamedFrom` markers, because an *undeclared* rename back is split into drop + add by
    /// `RenameGuessSplitter` and refused as destructive. Same mechanism, narrower reach, and this fact covers
    /// the mechanism.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_backwards_change_the_destructive_gate_cannot_see_is_stood_down_rather_than_oscillating()
    {
        var databasePath = AlvoHostWorld.TempDatabasePath();

        try
        {
            await InitializeAsync(databasePath);
            await using (var indexed = await AlvoBootWorld.StartAsync(
                AlvoBootWorld.IndexedDescriptorFileName, databasePath, AlvoSchemaStartupMode.Apply))
            {
                indexed.BootState.AppliedRevision.ShouldBe(
                    2, "adding an index must really be a non-empty, non-destructive apply, or nothing below has "
                    + "a backwards change to make");
            }

            await using var backwards = await AlvoBootWorld.TryStartAsync(databasePath: databasePath);

            backwards.StartFailure.ShouldBeNull();
            backwards.BootState.Phase.ShouldBe(AlvoBootPhase.Failed);
            (await backwards.RecordedRevisionsAsync("host-boot")).ShouldBe(
                [1, 2],
                "dropping the index again is destructive to nothing, so only the ordering gate can stop this "
                + "pod from flipping the schema back");
        }
        finally
        {
            AlvoHostWorld.TryDeleteDatabase(databasePath);
        }
    }

    /// <summary>Boots once over <paramref name="databasePath"/> so the boots above diff against something.</summary>
    private static async Task InitializeAsync(string databasePath)
    {
        await using var first = await AlvoBootWorld.StartAsync(databasePath: databasePath);

        first.BootState.AppliedRevision.ShouldBe(
            1, "the first boot must really initialize the database, or the boots below diff against nothing");
    }
}
