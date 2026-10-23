using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Tests.Extensions;

using NSubstitute;

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

    /// <summary>A host whose handlers do not fit the stored descriptor stays up, not ready, with the reason published.</summary>
    [Fact]
    public async Task Handlers_that_do_not_fit_a_stored_descriptor_stand_the_host_up_not_ready()
    {
        using var world = Dashboard().StoredVerbatim(BootServiceWorld.City);
        world.Extensions = MisfitHandlers();

        (await world.BootAsync()).ShouldBeNull("a stored-descriptor refusal never stops the process");

        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.State.Failure.ShouldNotBeNull().ShouldContain("'orphan-handler'");
        world.PrimedEntities.ShouldBeEmpty();
        world.Log.ShouldContain(entry => entry.Level == LogLevel.Critical);
    }

    /// <summary>The same handlers on a code-first boot refuse the start: the file is the host's, and so is the fix.</summary>
    [Fact]
    public async Task Handlers_that_do_not_fit_a_descriptor_file_refuse_the_start()
    {
        using var world = new BootServiceWorld { Extensions = MisfitHandlers() }.BootingFrom(BootServiceWorld.City);

        (await world.BootAsync()).ShouldBeOfType<AlvoStartupRefusedException>().Message.ShouldContain("'orphan-handler'");
        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.PrimedEntities.ShouldBeEmpty();
    }

    /// <summary>A stored descriptor under the wrong key is refused for the key first, whatever the handlers say.</summary>
    [Fact]
    public async Task A_stored_descriptor_under_another_name_is_refused_for_the_name_before_the_handlers()
    {
        using var world = Dashboard().StoredVerbatim(BootServiceWorld.NamedWarehouses);
        world.Extensions = MisfitHandlers();

        (await world.BootAsync()).ShouldBeNull();

        world.State.Failure.ShouldNotBeNull().ShouldContain("warehouses");
        world.State.Failure.ShouldNotContain("orphan-handler");
    }

    /// <summary>
    /// A host awaiting its first descriptor still refuses to start on a registration no descriptor can cure: there is
    /// nothing for the dashboard to repair, only the host to redeploy.
    /// </summary>
    [Theory]
    [InlineData("bad-id", "extensions/Bad_Id/id")]
    [InlineData("missing-service", "extensions/needs-a-clock/handler")]
    [InlineData("star-reads-a-field", "extensions/audit-all/when")]
    public async Task A_host_awaiting_its_descriptor_refuses_a_registration_no_descriptor_can_cure(string shape, string locator)
    {
        using var world = Dashboard();
        world.Extensions = Uncurable(shape);

        (await world.BootAsync()).ShouldBeOfType<AlvoStartupRefusedException>().Message.ShouldContain(locator);

        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.Log.ShouldContain(entry => entry.Level == LogLevel.Critical);
    }

    /// <summary>Every boot shape runs the catalog-free check exactly once: its order tie is logged once.</summary>
    [Theory]
    [InlineData("file")]
    [InlineData("stored")]
    [InlineData("awaiting")]
    public async Task Every_boot_shape_checks_the_registrations_once(string shape)
    {
        using var world = shape switch
        {
            "file" => new BootServiceWorld().BootingFrom(BootServiceWorld.City),
            "stored" => Dashboard().StoredVerbatim(BootServiceWorld.City),
            _ => Dashboard(),
        };
        world.Extensions = world.Binder(alvo => alvo.Entity("depots", e => e
            .OnCreated("first", HandlerCatalog.OnCreated, configure: o => o.Order = 1)
            .OnCreated("second", HandlerCatalog.OnCreated, configure: o => o.Order = 1)));

        (await world.BootAsync()).ShouldBeNull();

        world.Log.Count(entry => entry.EventName == "ExtensionOrderTie").ShouldBe(1);
    }

    /// <summary>
    /// Unfinished work no registration owns refuses the start on every shape that reaches Ready — a dashboard-first host
    /// included, because only a redeploy can register or retire the id — and the refusal is recorded first.
    /// </summary>
    [Theory]
    [InlineData("file")]
    [InlineData("stored")]
    [InlineData("awaiting")]
    public async Task Orphaned_work_refuses_the_start_on_every_shape_that_reaches_ready(string shape)
    {
        using var world = Shaped(shape);
        await PendingAsync(world, "old-sync");

        (await world.BootAsync()).ShouldBeOfType<AlvoStartupRefusedException>().Message.ShouldContain("'old-sync'");

        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.State.Failure.ShouldNotBeNull().ShouldContain("'old-sync'");
        world.Log.ShouldContain(entry => entry.EventName == "BootRefused" && entry.Message.Contains("'old-sync'"));
    }

    /// <summary>
    /// A refused start changes nothing: a build that retires one id and forgets another refuses before it ends the first
    /// id's work, so the rollback that follows finds that work where it left it.
    /// </summary>
    [Theory]
    [InlineData("file")]
    [InlineData("stored")]
    [InlineData("awaiting")]
    public async Task A_refused_start_applies_no_retirement(string shape)
    {
        using var world = Shaped(shape);
        world.Registrations = HandlerCatalog.Registry(alvo => alvo.AddRetiredExtension("old-sync", AlvoRetirement.DeadLetter));
        await PendingAsync(world, "old-sync");
        await PendingAsync(world, "forgotten");

        (await world.BootAsync()).ShouldBeOfType<AlvoStartupRefusedException>().Message.ShouldContain("'forgotten'");

        world.Deliveries.Rows.ShouldAllBe(row => row.Status == "pending");
        world.Log.ShouldNotContain(entry => entry.EventName == "ExtensionRetired");
    }

    /// <summary>On the file path the refusal comes before stage 2 applies anything: the refused build migrates nothing.</summary>
    [Fact]
    public async Task A_file_boot_refused_over_orphaned_work_leaves_the_schema_untouched()
    {
        using var world = new BootServiceWorld().BootingFrom(BootServiceWorld.City);
        await PendingAsync(world, "old-sync");

        (await world.BootAsync()).ShouldBeOfType<AlvoStartupRefusedException>();

        world.RecordedRevisions().ShouldBeEmpty();
        world.PrimedEntities.ShouldBeEmpty();
    }

    /// <summary>Every shape that reaches Ready applies the retirements before it publishes Ready.</summary>
    [Theory]
    [InlineData("file")]
    [InlineData("stored")]
    [InlineData("awaiting")]
    public async Task Every_shape_that_reaches_ready_applies_the_retirements_first(string shape)
    {
        using var world = Shaped(shape);
        world.Registrations = HandlerCatalog.Registry(alvo => alvo.AddRetiredExtension("old-sync", AlvoRetirement.DeadLetter));
        await PendingAsync(world, "old-sync");

        await world.BootOrFailAsync();

        world.State.Phase.ShouldBe(AlvoBootPhase.Ready);
        var row = world.Deliveries.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("retired");
    }

    /// <summary>A boot that stood down never reaches the guard: nothing in that process delivers, so nothing is decided.</summary>
    [Fact]
    public async Task A_stored_descriptor_refusal_decides_nothing_about_pending_work()
    {
        using var world = Dashboard().StoredVerbatim(BootServiceWorld.City);
        world.Extensions = MisfitHandlers();
        world.Registrations = HandlerCatalog.Registry(alvo => alvo.AddRetiredExtension("old-sync", AlvoRetirement.Discard));
        await PendingAsync(world, "old-sync");

        (await world.BootAsync()).ShouldBeNull();

        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.Deliveries.Rows.ShouldHaveSingleItem().Status.ShouldBe("pending");
    }

    private static BootServiceWorld Shaped(string shape) => shape switch
    {
        "file" => new BootServiceWorld().BootingFrom(BootServiceWorld.City),
        "stored" => Dashboard().StoredVerbatim(BootServiceWorld.City),
        _ => Dashboard(),
    };

    private static Task<int> PendingAsync(BootServiceWorld world, string subscriber) => world.Deliveries.FanOutAsync(
        [new MMLib.Alvo.Events.DeliveryRequest { EventId = Guid.NewGuid(), Subscriber = subscriber, PartitionKey = "depots:1", Kind = "handler" }],
        TestContext.Current.CancellationToken);

    private static ExtensionBinder MisfitHandlers() => new(
        HandlerCatalog.Registry(alvo => alvo.Entity("no-such-entity", e => e.OnCreated("orphan-handler", HandlerCatalog.OnCreated))),
        new CelCompiler(),
        HandlerCatalog.EveryService(),
        NullLogger<ExtensionBinder>.Instance);

    private static ExtensionBinder Uncurable(string shape)
    {
        var services = Substitute.For<IServiceProviderIsService>();
        services.IsService(Arg.Any<Type>()).Returns(call => call.Arg<Type>() != typeof(TimeProvider));
        Action<IAlvoBuilder> configure = shape switch
        {
            "bad-id" => alvo => alvo.Entity("depots", e => e.OnCreated("Bad_Id", HandlerCatalog.OnCreated)),
            "missing-service" => alvo => alvo.Entity("depots", e => e.OnCreated(
                "needs-a-clock", (MMLib.Alvo.Events.AlvoCreated<MMLib.Alvo.Data.AlvoRecord> _, TimeProvider clock) => Task.CompletedTask)),
            _ => alvo => alvo.Entity("*", all => all.OnSaved("audit-all", HandlerCatalog.OnSaved, when: "new.title == 'x'")),
        };

        return new(HandlerCatalog.Registry(configure), new CelCompiler(), services, NullLogger<ExtensionBinder>.Instance);
    }

    /// <summary>A dashboard-first host: no descriptor source, and a project name instead.</summary>
    private static BootServiceWorld Dashboard()
    {
        var world = new BootServiceWorld();
        world.Options.Project = BootServiceWorld.Project;

        return world;
    }
}
