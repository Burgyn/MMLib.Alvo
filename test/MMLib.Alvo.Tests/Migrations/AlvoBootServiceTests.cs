using Microsoft.Extensions.Logging;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Migrations.Internal;
using System.Reflection;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// What the boot decides for a <b>code-first</b> host: whether it initializes, applies, refuses or stands
/// down, what it publishes for a readiness probe, and what it leaves in the database on the way.
/// </summary>
/// <remarks>
/// Driven through <see cref="BootServiceWorld"/> — the lifecycle method a host calls, over DB-less ports, and
/// no host. The decision table itself is <c>SchemaStartupDecisionTests</c>; what is pinned here is the
/// carrying out of it, which is the half that publishes state, primes the catalog and writes revisions.
/// </remarks>
public sealed class AlvoBootServiceTests
{
    /// <summary>
    /// Every port is required, and refused by the name of the parameter that carries it.
    /// </summary>
    /// <remarks>
    /// The parameter name is the fact rather than decoration: an <see cref="ArgumentNullException"/> naming
    /// the wrong port sends whoever composed the container to the wrong registration, and a
    /// <see cref="NullReferenceException"/> raised three stages later during a boot names nothing at all.
    /// </remarks>
    [Theory]
    [InlineData("bootPlan")]
    [InlineData("migrator")]
    [InlineData("writer")]
    [InlineData("introspector")]
    [InlineData("store")]
    [InlineData("history")]
    [InlineData("policyCatalogProvider")]
    [InlineData("state")]
    [InlineData("options")]
    [InlineData("logger")]
    public void Every_port_the_boot_needs_is_refused_by_name_when_it_is_missing(string port)
    {
        using var world = new BootServiceWorld();
        var constructor = typeof(AlvoBootService).GetConstructors().Single();
        var ports = world.Ports();
        ports[Array.FindIndex(constructor.GetParameters(), parameter => parameter.Name == port)] = null;

        var thrown = Should.Throw<TargetInvocationException>(() => constructor.Invoke(ports));

        thrown.InnerException.ShouldBeOfType<ArgumentNullException>().ParamName.ShouldBe(port);
    }

    /// <summary>
    /// A first boot over a database Alvo has recorded nothing for initializes it, publishes the revision it
    /// wrote, and primes the catalog everything else serves from.
    /// </summary>
    [Fact]
    public async Task A_first_boot_initializes_the_database_and_publishes_revision_1()
    {
        using var world = new BootServiceWorld().BootingFrom(BootServiceWorld.City);

        await world.BootOrFailAsync();

        world.State.Phase.ShouldBe(AlvoBootPhase.Ready);
        world.State.AppliedRevision.ShouldBe(1);
        world.RecordedRevisions().ShouldBe([1]);
        world.PrimedEntities.ShouldContain("depots");
    }

    /// <summary>
    /// The gap a restart used to fall into: a boot that applies nothing must still prime, or the process
    /// comes back denying every operation.
    /// </summary>
    /// <remarks>
    /// The revision staying at 1 is what makes this a priming fact rather than a second apply.
    /// </remarks>
    [Fact]
    public async Task An_unchanged_restart_primes_without_applying_anything()
    {
        using var world = new BootServiceWorld()
            .Applied(BootServiceWorld.City)
            .BootingFrom(BootServiceWorld.City);

        await world.BootOrFailAsync();

        world.PrimedEntities.ShouldContain("depots");
        world.State.AppliedRevision.ShouldBe(1);
        world.RecordedRevisions().ShouldBe([1]);
    }

    /// <summary>
    /// The plan is diffed against the <em>recorded</em> snapshot, and the live schema is read only when there
    /// is none.
    /// </summary>
    /// <remarks>
    /// The two answers are deliberately contradictory here: the store reports the descriptor's own schema and
    /// the introspector reports an empty database. A boot that introspected anyway would see a create-the-world
    /// plan and write a second revision over a database that already matched.
    /// </remarks>
    [Fact]
    public async Task A_recorded_snapshot_is_what_the_plan_is_diffed_against_not_the_live_schema()
    {
        using var world = new BootServiceWorld()
            .Applied(BootServiceWorld.City)
            .BootingFrom(BootServiceWorld.City);
        world.LiveSchema = new([]);

        await world.BootOrFailAsync();

        world.RecordedRevisions().ShouldBe([1], "an unchanged restart must apply nothing");
        world.State.AppliedRevision.ShouldBe(1);
    }

    /// <summary>
    /// The ordering gate's O(N) history read is not paid by the most common boot in existence.
    /// </summary>
    /// <remarks>
    /// An unchanged restart cannot be the boot that rewrites a newer schema with an older one — the empty plan
    /// already says so — and <c>IDescriptorVersionStore.ListAsync</c> reads a project's whole history. Paying
    /// it to be told something the plan implies is a real tax on every restart of every deployment.
    /// </remarks>
    [Fact]
    public async Task An_unchanged_restart_never_reads_the_whole_descriptor_history()
    {
        using var world = new BootServiceWorld()
            .Applied(BootServiceWorld.City)
            .BootingFrom(BootServiceWorld.City);

        await world.BootOrFailAsync();

        world.HistoryReads.ShouldBe(0);
    }

    /// <summary>
    /// <c>Skip</c> never pays it either — it applies nothing, so the gate that governs an apply has nothing
    /// to govern — and it serves the recorded schema over the drift it read.
    /// </summary>
    [Fact]
    public async Task Skip_serves_the_recorded_revision_and_reads_no_history_either()
    {
        using var world = new BootServiceWorld()
            .Applied(BootServiceWorld.City)
            .BootingFrom(BootServiceWorld.CityAndTown);
        world.Options.Startup = AlvoSchemaStartupMode.Skip;

        await world.BootOrFailAsync();

        world.State.AppliedRevision.ShouldBe(1, "Skip applies nothing, so the recorded revision is what it serves");
        world.HistoryReads.ShouldBe(0);
    }

    /// <summary>
    /// Drift under <c>Apply</c> is applied against the revision the database is on, and the new one is
    /// published — the branch that proves the boot advances the snapshot rather than rewriting revision 1.
    /// </summary>
    [Fact]
    public async Task Drift_under_Apply_advances_the_applied_revision()
    {
        using var world = new BootServiceWorld()
            .Applied(BootServiceWorld.City)
            .BootingFrom(BootServiceWorld.CityAndTown);

        await world.BootOrFailAsync();

        world.State.Phase.ShouldBe(AlvoBootPhase.Ready);
        world.State.AppliedRevision.ShouldBe(2);
        world.RecordedRevisions().ShouldBe([1, 2]);
    }

    /// <summary>The same drift under <c>Verify</c> refuses, and names the setting that would apply it.</summary>
    /// <remarks>
    /// The mode is set explicitly because <c>Apply</c> is the default: a version of this fact that relied on
    /// the default would flip meaning with it and go green by applying the drift it exists to see refused.
    /// </remarks>
    [Fact]
    public async Task Drift_under_Verify_refuses_and_names_the_setting_that_would_apply_it()
    {
        using var world = VerifyOverDrift();

        (await world.BootAsync()).ShouldBeOfType<AlvoStartupRefusedException>()
            .FixSuggestion.ShouldContain("Alvo__Schema__Startup=Apply");
    }

    /// <summary>
    /// The refusal is <b>recorded</b> before it propagates, and carries the policy's own reason rather than
    /// the placeholder a refusal with nothing to say would get.
    /// </summary>
    [Fact]
    public async Task A_refused_boot_records_the_reason_the_policy_gave()
    {
        using var world = VerifyOverDrift();

        await world.BootAsync();

        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.State.Failure.ShouldNotBeNull().ShouldContain("Alvo__Schema__Startup=Apply");
    }

    /// <summary>A refused boot changes nothing and leaves nothing behind that could serve.</summary>
    /// <remarks>
    /// The unprimed catalog is the safety half: an unprimed <c>IPolicyCatalogProvider</c> denies every
    /// operation, so a refusal that primed anyway would leave a process able to answer from a schema the
    /// database was never brought to.
    /// </remarks>
    [Fact]
    public async Task A_refused_boot_writes_no_revision_and_primes_nothing()
    {
        using var world = VerifyOverDrift();

        await world.BootAsync();

        world.RecordedRevisions().ShouldBe([1]);
        world.PrimedEntities.ShouldBeEmpty();
    }

    /// <summary>
    /// The refusal is logged at <see cref="LogLevel.Critical"/>, which is where the design promises an
    /// operator finds the reason.
    /// </summary>
    /// <remarks>
    /// <c>AlvoBootState.Failure</c> is deliberately withheld from the readiness body, so without this line the
    /// promise held only for a standalone host whose stderr somebody was reading.
    /// </remarks>
    [Fact]
    public async Task A_refused_boot_writes_the_reason_to_the_log()
    {
        using var world = VerifyOverDrift();

        await world.BootAsync();

        world.Log.ShouldContain(entry => entry.Level == LogLevel.Critical);
    }

    /// <summary>
    /// A boot holding the descriptor the database has already moved on from <b>starts</b>, reports not ready,
    /// and applies nothing — it is behind, not misconfigured, so an orchestrator drains it rather than
    /// restart-looping it.
    /// </summary>
    /// <remarks>
    /// The history staying at two revisions is the half that separates this from a refusal: the whole point is
    /// that the older descriptor did not apply, in either direction.
    /// </remarks>
    [Fact]
    public async Task A_boot_holding_an_older_descriptor_stands_down_instead_of_stopping_the_process()
    {
        using var world = OlderPod();

        (await world.BootAsync()).ShouldBeNull("a pod that is one deploy behind must be drained, not killed");

        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.State.AppliedRevision.ShouldBeNull();
        world.RecordedRevisions().ShouldBe([1, 2]);
        world.PrimedEntities.ShouldBeEmpty();
    }

    /// <summary>
    /// A stand-down says which revision this process is on and which one the database is at — the two facts a
    /// rollback crash-loop was never told — and says it once, as the only line the boot writes.
    /// </summary>
    /// <remarks>
    /// The single line is the half that pins the stand-down as an <em>end</em> of the boot: a stand-down that
    /// fell through would also write the ordinary "booted project" line, telling an operator reading the
    /// container's first ten lines that a process serving nothing had come up ready.
    /// </remarks>
    [Fact]
    public async Task A_stand_down_names_both_revisions_and_is_the_only_line_the_boot_writes()
    {
        using var world = OlderPod();

        await world.BootAsync();

        var reason = world.State.Failure.ShouldNotBeNull();
        reason.ShouldContain("revision 1");
        reason.ShouldContain("revision 2");
        world.Log.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Critical);
    }

    /// <summary>
    /// A replica that lost the cold-start race <b>retries instead of failing the boot</b>, which is what
    /// keeps the ordinary first deployment of a replica set from crash-looping.
    /// </summary>
    /// <remarks>
    /// <b>What this does NOT prove, stated so nobody reads more into it.</b>
    /// <c>ConflictingRuntimeSchemaWriter</c> refuses the attempt without advancing any simulated database
    /// state, so the retry re-reads a world nothing changed and re-plans the identical apply with
    /// <c>expectedRevision == 0</c>. The fact pinned here is therefore "the conflict is retried and the boot
    /// completes", not "the replica converged on what the winner wrote". The stronger case needs the fake to
    /// model a competitor's COMPLETE write — the append, the applied snapshot and the live schema — and is
    /// filed in #245 rather than approximated here, because a fake that advances only the append would prove
    /// something else again.
    /// </remarks>
    [Fact]
    public async Task A_lost_race_is_retried_instead_of_failing_the_boot()
    {
        using var world = new BootServiceWorld().BootingFrom(BootServiceWorld.City);
        world.ConflictsBeforeTheWriteLands = 1;

        await world.BootOrFailAsync();

        world.State.AppliedRevision.ShouldBe(1);
    }

    /// <summary>The lost race is recorded, naming the project and the conflict's type.</summary>
    /// <remarks>
    /// It is the only line that tells "this boot initialized the database" from "this boot found it
    /// initialized while trying to", and it is Information because on a replica set it is the expected
    /// outcome for every replica but one.
    /// </remarks>
    [Fact]
    public async Task A_lost_race_is_recorded_naming_the_conflict()
    {
        using var world = new BootServiceWorld().BootingFrom(BootServiceWorld.City);
        world.ConflictsBeforeTheWriteLands = 1;

        await world.BootOrFailAsync();

        world.Log.ShouldContain(entry => entry.Message.Contains(
            nameof(DescriptorConcurrencyException), StringComparison.Ordinal));
    }

    /// <summary>A failure that is not a lost race is not retried into silence — it fails the start.</summary>
    [Fact]
    public async Task A_failure_that_is_not_a_lost_race_fails_the_start()
    {
        using var world = DatabaseFailure();

        (await world.BootAsync()).ShouldBeOfType<InvalidOperationException>();
    }

    /// <summary>And it is recorded, so a probe reads Failed rather than Pending for a boot that ran.</summary>
    [Fact]
    public async Task A_failure_that_is_not_a_lost_race_is_still_published_as_a_failure()
    {
        using var world = DatabaseFailure();

        await world.BootAsync();

        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.State.Failure.ShouldNotBeNull().ShouldContain("the driver refused");
    }

    /// <summary>
    /// A host that configured <b>both</b> a descriptor source and a project name is refused, because the two
    /// can disagree and picking one silently is how a deployment serves a project nobody chose.
    /// </summary>
    [Fact]
    public async Task A_code_first_host_that_also_names_a_project_is_refused_naming_both_ways_out()
    {
        using var world = new BootServiceWorld().BootingFrom(BootServiceWorld.City);
        world.Options.Project = BootServiceWorld.Project;

        var refusal = (await world.BootAsync()).ShouldBeOfType<AlvoStartupRefusedException>();

        refusal.Message.ShouldContain("FromDescriptor");
        refusal.Message.ShouldContain(BootServiceWorld.Project);
        refusal.FixSuggestion.ShouldContain(AlvoSchemaOptions.ProjectEnvironmentVariable);
    }

    /// <summary>That refusal is recorded too, and logged where an operator reads it.</summary>
    [Fact]
    public async Task The_two_modes_at_once_refusal_is_recorded_before_it_propagates()
    {
        using var world = new BootServiceWorld().BootingFrom(BootServiceWorld.City);
        world.Options.Project = BootServiceWorld.Project;

        await world.BootAsync();

        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.State.Failure.ShouldNotBeNull().ShouldContain("FromDescriptor");
        world.Log.ShouldContain(entry => entry.Level == LogLevel.Critical);
    }

    /// <summary>
    /// A stage-0 refusal raised by the descriptor source itself is recorded before it propagates — the catch
    /// in <c>StartingAsync</c> rethrows it untouched, so nothing else would write it down.
    /// </summary>
    [Fact]
    public async Task A_descriptor_source_that_refuses_has_its_refusal_recorded()
    {
        using var world = new BootServiceWorld()
            .BootRefusedBy(new AlvoStartupRefusedException("the source refused", "attach a readable one"));

        (await world.BootAsync()).ShouldBeOfType<AlvoStartupRefusedException>();

        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.State.Failure.ShouldNotBeNull().ShouldContain("the source refused");
        world.Log.ShouldContain(entry => entry.Level == LogLevel.Critical);
    }

    /// <summary>
    /// An applied drift is recorded once, as a <b>warning</b>: this process used DDL rights against a database
    /// somebody was already running, and a deployment that meant to set <c>Verify</c> has this one chance to
    /// notice.
    /// </summary>
    [Fact]
    public async Task A_boot_that_applied_drift_warns_about_it_exactly_once()
    {
        using var world = new BootServiceWorld()
            .Applied(BootServiceWorld.City)
            .BootingFrom(BootServiceWorld.CityAndTown);

        await world.BootOrFailAsync();

        var line = world.Log.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Warning);
        line.Message.ShouldContain(BootServiceWorld.Project);
    }

    /// <summary>
    /// A boot that changed nothing records that at information level, and only once — routine, and told apart
    /// from the drift it is not.
    /// </summary>
    [Fact]
    public async Task A_boot_that_changed_nothing_records_that_exactly_once_at_information()
    {
        using var world = new BootServiceWorld()
            .Applied(BootServiceWorld.City)
            .BootingFrom(BootServiceWorld.City);

        await world.BootOrFailAsync();

        var line = world.Log.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Information);
        line.Message.ShouldContain(BootServiceWorld.Project);
    }

    /// <summary>The pod holding the descriptor the database has already moved past.</summary>
    private static BootServiceWorld OlderPod() =>
        new BootServiceWorld()
            .Applied(BootServiceWorld.City, BootServiceWorld.CityAndTown)
            .BootingFrom(BootServiceWorld.City);

    /// <summary>A database that fails the read stage 2 makes, in a way no retry can fix.</summary>
    private static BootServiceWorld DatabaseFailure()
    {
        var world = new BootServiceWorld().BootingFrom(BootServiceWorld.City);
        world.Introspection = new InvalidOperationException("the driver refused");

        return world;
    }

    /// <summary>Drift this mode will not apply — the arrangement four facts above refuse.</summary>
    private static BootServiceWorld VerifyOverDrift()
    {
        var world = new BootServiceWorld()
            .Applied(BootServiceWorld.City)
            .BootingFrom(BootServiceWorld.CityAndTown);
        world.Options.Startup = AlvoSchemaStartupMode.Verify;

        return world;
    }
}
