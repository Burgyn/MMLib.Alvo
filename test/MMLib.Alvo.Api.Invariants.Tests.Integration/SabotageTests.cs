namespace MMLib.Alvo.Api.Tests.Invariants;

/// <summary>
/// The invariants can fail: sabotage the behaviour each one watches and it goes red.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of #26's definition of done that keeps the rest from becoming a green check nobody
/// trusts. Every other fact in this project asserts that Alvo behaves; these two assert that the facts
/// themselves would notice if it did not — which is a different claim, and the only one that makes the first
/// worth anything.
/// </para>
/// <para>
/// <b>The assertion is only that the invariant fails, never how.</b> A sabotaged policy engine may answer 200
/// where a denial was due, or throw while applying one entity's predicate to another's rows; a forgetful
/// store writes a second row and the count is wrong. Pinning a particular failure would pin the sabotage
/// rather than the invariant, and the sabotage is not the thing under test.
/// </para>
/// </remarks>
public class SabotageTests
{
    /// <summary>The first committed seed — one project is enough to show an invariant can fail.</summary>
    private static GeneratedProject Project => GeneratedProject.Generate(GeneratedProject.Seeds[0]);

    /// <summary>Default-deny's invariant goes red when the policy engine stops denying.</summary>
    [Fact]
    public async Task Breaking_default_deny_makes_its_invariant_fail()
    {
        var project = Project;
        await using var world = await project.StartAsync(
            [project.Admin()], Sabotage.PolicyAdmitsEverything(project));

        var failure = await Record.ExceptionAsync(() => BehaviourInvariants.DefaultDenyAsync(world, project));

        failure.ShouldNotBeNull(
            "an engine that never denies must make the default-deny invariant fail; it passed, so that "
            + "invariant would not notice the framework's most important guarantee breaking");

        // The per-operation half is a different claim over a different entity, so it gets its own proof:
        // an engine that admits everything must break it too.
        var perOperation = await Record.ExceptionAsync(
            () => BehaviourInvariants.PerOperationDefaultDenyAsync(world, project));

        perOperation.ShouldNotBeNull(
            "an engine that never denies must also break per-operation default-deny, which is the shape a "
            + "real descriptor has");
    }

    /// <summary>The idempotency invariant goes red when the token stops reaching the store.</summary>
    [Fact]
    public async Task Breaking_idempotency_makes_its_invariant_fail()
    {
        var project = Project;
        await using var world = await project.StartAsync([project.Admin()], Sabotage.IdempotencyForgets());

        var failure = await Record.ExceptionAsync(
            () => BehaviourInvariants.IdempotencyKeyWritesOneRowAsync(world, project));

        failure.ShouldNotBeNull(
            "a store that drops the idempotency token must make the replay invariant fail; it passed, so a "
            + "duplicate write would go unnoticed");
    }

    /// <summary>
    /// And the same two invariants pass on the unsabotaged host, so the failures above are the sabotage.
    /// </summary>
    /// <remarks>
    /// Without this, both facts above would pass for a project whose invariants failed for some unrelated
    /// reason — a broken generator, a descriptor that will not apply — and the battery would be asserting
    /// that something, anything, goes wrong.
    /// </remarks>
    [Fact]
    public async Task Both_invariants_pass_on_the_unsabotaged_host()
    {
        var project = Project;
        await using var world = await project.StartAsync([project.Admin()]);

        await BehaviourInvariants.DefaultDenyAsync(world, project);
        await BehaviourInvariants.PerOperationDefaultDenyAsync(world, project);
        await BehaviourInvariants.IdempotencyKeyWritesOneRowAsync(world, project);
    }
}
