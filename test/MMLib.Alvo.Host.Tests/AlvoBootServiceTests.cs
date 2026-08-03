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

    /// <summary>Boots once over <paramref name="databasePath"/> so the boots above diff against something.</summary>
    private static async Task InitializeAsync(string databasePath)
    {
        await using var first = await AlvoBootWorld.StartAsync(databasePath: databasePath);

        first.BootState.AppliedRevision.ShouldBe(
            1, "the first boot must really initialize the database, or the boots below diff against nothing");
    }
}
