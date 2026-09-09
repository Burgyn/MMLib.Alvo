namespace MMLib.Alvo.Api.Tests.Invariants;

/// <summary>
/// Every generated project holds every behavioural invariant — the claim #26 actually makes: that they
/// hold <em>across</em> descriptors and not only for the demo they were written against.
/// </summary>
/// <remarks>
/// The bodies live in <see cref="BehaviourInvariants"/> rather than here, because
/// <c>SabotageTests</c> runs the same ones against a deliberately broken host and asserts they go red.
/// </remarks>
public class BehaviourInvariantTests
{
    /// <summary>The sixteen committed seeds as theory data, so each case is named by its seed.</summary>
    public static TheoryData<int> Seeds => [.. GeneratedProject.Seeds];

    /// <summary>
    /// Default-deny, the CRUD shape, replace idempotence, a replayed idempotency key, and the JSON
    /// <c>Content-Type</c> requirement.
    /// </summary>
    /// <param name="seed">The project's seed, which is how a failure is reproduced.</param>
    /// <remarks>
    /// One test per seed rather than one per invariant per seed: they share a host, and booting one per invariant
    /// would multiply the slowest part of this suite for no extra coverage. The
    /// failure names which invariant broke, because each carries its own assertion messages.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task A_generated_project_holds_every_behavioural_invariant(int seed)
    {
        var project = GeneratedProject.Generate(seed);
        await using var world = await project.StartAsync([project.Admin()]);

        await BehaviourInvariants.DefaultDenyAsync(world, project);
        await BehaviourInvariants.PerOperationDefaultDenyAsync(world, project);
        await BehaviourInvariants.TheStatementRecorderRecordsAsync(world, project);
        await BehaviourInvariants.CrudShapeAsync(world, project);
        await BehaviourInvariants.ReplaceIsIdempotentAsync(world, project);
        await BehaviourInvariants.IdempotencyKeyWritesOneRowAsync(world, project);
        await BehaviourInvariants.NonJsonBodiesAreRefusedAsync(world, project);
    }
}
