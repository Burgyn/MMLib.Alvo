using Microsoft.Extensions.Logging;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// What the boot decides for a <b>dashboard-first</b> host — one that attached no descriptor source and names
/// its project instead, so the descriptor it serves is the one the database already holds.
/// </summary>
/// <remarks>
/// The mode is not a flag: it is decided by what the host configured, so every fact here differs from
/// <see cref="AlvoBootServiceTests"/> in exactly one way — no <c>IDescriptorSource</c>, and
/// <c>Alvo__Schema__Project</c> set. The refusals are the interesting half: a descriptor that lives in the
/// database can only be corrected through the process that is refusing it, so this mode stands down where
/// code-first stops the start.
/// </remarks>
public sealed class AlvoBootServiceStoredDescriptorTests
{
    /// <summary>
    /// A host that attached a driver and neither a descriptor nor a project name is refused by name — and the
    /// refusal is <b>recorded</b>, so the phase is Failed rather than Pending.
    /// </summary>
    /// <remarks>
    /// <c>StartingAsync</c> rethrows an <c>AlvoStartupRefusedException</c> untouched so that it cannot
    /// overwrite a project-scoped failure with a project-less one — which means nothing further down writes
    /// this one down, and an embedded host holding the state could not tell "nothing is configured" from "the
    /// boot has not run".
    /// </remarks>
    [Fact]
    public async Task A_host_that_configured_neither_a_descriptor_nor_a_project_is_refused_by_name()
    {
        using var world = new BootServiceWorld();

        var refusal = (await world.BootAsync()).ShouldBeOfType<AlvoStartupRefusedException>();

        refusal.FixSuggestion.ShouldContain("FromDescriptor");
        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.State.Failure.ShouldNotBeNull().ShouldContain("FromDescriptor");
        world.Log.ShouldContain(entry => entry.Level == LogLevel.Critical);
    }

    /// <summary>A project name made of whitespace is no project name, and is refused the same way.</summary>
    /// <remarks>
    /// The shape an environment variable set to the empty string produces — which a container platform will
    /// hand over as happily as a real name. Serving it would publish readiness for a project called <c>"   "</c>.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_project_name_is_refused_rather_than_served(string blank)
    {
        using var world = new BootServiceWorld();
        world.Options.Project = blank;

        (await world.BootAsync()).ShouldBeOfType<AlvoStartupRefusedException>();
    }

    /// <summary>
    /// The expected first state of a fresh <c>docker run</c>: ready, serving nothing, and saying so — rather
    /// than refused, which would delete the zero-configuration mode the specification requires.
    /// </summary>
    [Fact]
    public async Task A_project_with_no_descriptor_history_is_ready_and_serving_nothing()
    {
        using var world = Dashboard();

        await world.BootOrFailAsync();

        world.State.Phase.ShouldBe(AlvoBootPhase.Ready);
        world.State.AppliedRevision.ShouldBeNull();
        world.PrimedEntities.ShouldBeEmpty();
    }

    /// <summary>
    /// And it warns, once — "ready" and "serving nothing" read as a contradiction to the operator who has just
    /// run the image for the first time.
    /// </summary>
    [Fact]
    public async Task A_project_with_no_descriptor_history_warns_about_it_exactly_once()
    {
        using var world = Dashboard();

        await world.BootOrFailAsync();

        var line = world.Log.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Warning);
        line.Message.ShouldContain(BootServiceWorld.Project);
    }

    /// <summary>
    /// A project that has a stored descriptor serves it: the revision the database is on is published and the
    /// catalog is primed from what stage 0 compiled out of it.
    /// </summary>
    /// <remarks>
    /// Nothing is applied and nothing is planned, and that is the finding rather than a shortcut: the stored
    /// descriptor is by construction the one the runtime apply last wrote <em>in the same transaction</em> as
    /// the schema, so there is nothing to converge on.
    /// </remarks>
    [Fact]
    public async Task A_stored_descriptor_is_served_at_the_revision_the_database_is_on()
    {
        using var world = Dashboard().Applied(BootServiceWorld.City);

        await world.BootOrFailAsync();

        world.State.Phase.ShouldBe(AlvoBootPhase.Ready);
        world.State.AppliedRevision.ShouldBe(1);
        world.PrimedEntities.ShouldContain("depots");
        world.RecordedRevisions().ShouldBe([1]);
    }

    /// <summary>Serving a stored descriptor is recorded once, at information level.</summary>
    [Fact]
    public async Task Serving_a_stored_descriptor_is_recorded_exactly_once()
    {
        using var world = Dashboard().Applied(BootServiceWorld.City);

        await world.BootOrFailAsync();

        var line = world.Log.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Information);
        line.Message.ShouldContain(BootServiceWorld.Project);
    }

    /// <summary>
    /// A stored descriptor this build refuses stands the host <b>up</b>, not ready — because the only
    /// sanctioned editor for it is in this process, and failing the start takes that tool away.
    /// </summary>
    /// <remarks>
    /// Reachable without anybody doing anything wrong: an older image against a row a newer one wrote, or a
    /// validator tightened between releases.
    /// </remarks>
    [Fact]
    public async Task A_stored_descriptor_this_build_refuses_stands_the_host_up_not_ready()
    {
        using var world = Dashboard().StoredVerbatim(BootServiceWorld.Unservable);

        (await world.BootAsync()).ShouldBeNull("the one tool that can fix the descriptor is in this process");

        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.State.AppliedRevision.ShouldBeNull();
        world.PrimedEntities.ShouldBeEmpty();
    }

    /// <summary>
    /// The same refusal through the <b>other</b> pass: a stored descriptor whose authorization rule does not
    /// compile. The case above fails JSON-Schema validation; this one passes it and is refused when the
    /// policy catalog is built, which is the arm a dashboard-first host actually depends on — the stored
    /// descriptor is a database row, so an uncompilable rule is a row somebody wrote.
    /// </summary>
    /// <remarks>
    /// Asserted separately rather than folded into a theory with the case above, because what is being
    /// claimed is that BOTH passes reach the same fail-up-not-ready outcome; a single row proves it for one
    /// pass and says nothing about the other, and the two are caught by one <c>catch</c> whose second
    /// exception type nothing else in this suite produces.
    /// </remarks>
    [Fact]
    public async Task A_stored_descriptor_whose_rule_does_not_compile_stands_the_host_up_not_ready()
    {
        using var world = Dashboard().StoredVerbatim(BootServiceWorld.UnservableRule);

        (await world.BootAsync()).ShouldBeNull("an uncompilable authorization rule must not be served");

        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.State.AppliedRevision.ShouldBeNull();
        world.PrimedEntities.ShouldBeEmpty();
    }

    /// <summary>
    /// That refusal names the project and the revision it was reading, and carries the reason the descriptor
    /// was refused — the three things an operator needs to find the row and repair it.
    /// </summary>
    [Fact]
    public async Task The_refusal_of_a_stored_descriptor_names_the_project_the_revision_and_the_reason()
    {
        using var world = Dashboard().StoredVerbatim(BootServiceWorld.Unservable);

        await world.BootAsync();

        var reason = world.State.Failure.ShouldNotBeNull();
        reason.ShouldContain(BootServiceWorld.Project);
        reason.ShouldContain("revision 1");
        reason.ShouldContain("entities");
        world.Log.ShouldContain(entry => entry.Level == LogLevel.Critical);
    }

    /// <summary>
    /// A stored descriptor that declares a different project than the key it is stored under is refused,
    /// naming both — priming it would publish the catalog under one name while the state and the history use
    /// the other.
    /// </summary>
    [Fact]
    public async Task A_stored_descriptor_that_declares_another_project_is_refused_naming_both()
    {
        using var world = Dashboard().StoredVerbatim(BootServiceWorld.NamedWarehouses);

        (await world.BootAsync()).ShouldBeNull();

        var reason = world.State.Failure.ShouldNotBeNull();
        reason.ShouldContain(BootServiceWorld.Project);
        reason.ShouldContain("warehouses");
        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.PrimedEntities.ShouldBeEmpty();
        world.Log.ShouldContain(entry => entry.Level == LogLevel.Critical);
    }

    /// <summary>A dashboard-first host: no descriptor source, and a project name instead.</summary>
    private static BootServiceWorld Dashboard()
    {
        var world = new BootServiceWorld();
        world.Options.Project = BootServiceWorld.Project;

        return world;
    }
}
