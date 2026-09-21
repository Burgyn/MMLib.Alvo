using MMLib.Alvo.Management;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// The one table that says which level each management operation needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>The completeness fact is the one that cannot go stale.</b> A table missing an operation would
/// leave that operation ungoverned, and an operation added without a level is exactly the change a
/// reviewer skims past — so the level is looked up for <em>every</em> enum member rather than for the
/// members somebody remembered to list.
/// </para>
/// <para>
/// <b>Each level's operations are pinned as a set, not one membership at a time.</b> A per-operation
/// theory would catch an operation leaving its level and miss one arriving — which is the direction that
/// widens the surface, since an operation that drifts down into <c>viewer</c> is read by nobody. A set
/// equality fails in both directions. It is also the shape that compiles: <see cref="ManagementOperation"/>
/// is <see langword="internal"/>, so a <c>public</c> theory method cannot take one as a parameter.
/// </para>
/// </remarks>
public class ManagementOperationsTests
{
    [Fact]
    public void Every_operation_has_a_required_level()
    {
        foreach (var operation in Enum.GetValues<ManagementOperation>())
        {
            ManagementOperations.RequiredLevel(operation).ShouldNotBe(
                ManagementLevel.None,
                $"'{operation}' would otherwise be reachable by a caller who matched no access level");
        }
    }

    /// <summary>
    /// The fallback is reachable only for a value outside the enum, and it answers with the most
    /// restrictive level rather than the most convenient one — so an operation added without a decision
    /// would be refused for everyone but an administrator.
    /// </summary>
    [Fact]
    public void An_operation_the_table_does_not_list_needs_the_highest_level()
        => ManagementOperations.RequiredLevel((ManagementOperation)9999).ShouldBe(ManagementLevel.Admin);

    [Fact]
    public void A_viewer_may_read_everything_including_the_simulator()
        => OperationsRequiring(ManagementLevel.Viewer).ShouldBe(
        [
            ManagementOperation.ListProjects,
            ManagementOperation.GetDescriptor,
            ManagementOperation.ListRevisions,
            ManagementOperation.GetRevision,
            ManagementOperation.GetSchema,
            ManagementOperation.GetCapabilities,
            ManagementOperation.GetInfo,
            ManagementOperation.SimulatePolicy,
        ]);

    /// <summary>
    /// <b>Applying a descriptor is one operation whether or not <c>?dryRun=true</c> is set</b>, and that
    /// is deliberate: a dry run returns the migration plan and the guardrail verdict for a descriptor
    /// the caller supplied, which is the same disclosure the real apply makes. A separate, cheaper level
    /// for it would let a viewer read a plan over a descriptor they may not write.
    /// </summary>
    [Fact]
    public void A_developer_may_write_configuration()
        => OperationsRequiring(ManagementLevel.Developer).ShouldBe(
            [ManagementOperation.ApplyDescriptor, ManagementOperation.RollbackRevision]);

    /// <summary>
    /// "Settings" is named rather than left to reading: it is the set <c>developer</c> is excluded from
    /// — API keys, user and role administration, and the danger zone. A developer edits <em>what the
    /// backend is</em>; an admin also decides <em>who may reach it</em>.
    /// </summary>
    [Fact]
    public void Only_an_admin_may_reach_settings()
        => OperationsRequiring(ManagementLevel.Admin).ShouldBe(
        [
            ManagementOperation.ManageApiKeys,
            ManagementOperation.ManageUsers,
            ManagementOperation.DeleteProject,
        ]);

    /// <summary>
    /// The surface is thirteen operations. A count rather than a comment, so adding a fourteenth fails
    /// here until somebody decides its level — which is the decision this table exists to force.
    /// </summary>
    [Fact]
    public void The_management_surface_is_thirteen_operations()
        => Enum.GetValues<ManagementOperation>().Length.ShouldBe(13);

    [Fact]
    public void The_levels_are_ordered_so_the_highest_match_is_the_greatest_value()
    {
        ((int)ManagementLevel.None).ShouldBeLessThan((int)ManagementLevel.Viewer);
        ((int)ManagementLevel.Viewer).ShouldBeLessThan((int)ManagementLevel.Developer);
        ((int)ManagementLevel.Developer).ShouldBeLessThan((int)ManagementLevel.Admin);
        ((int)ManagementLevel.None).ShouldBe(0, "an unset or mis-bound value must land on 'no access'");

        (ManagementLevel.Admin >= ManagementLevel.Viewer).ShouldBeTrue(
            "the gate answers 'may do' with this very comparison");
    }

    /// <summary>Every operation the table places at exactly <paramref name="level"/>, in declaration order.</summary>
    /// <param name="level">The level to collect the operations of.</param>
    private static IReadOnlyList<ManagementOperation> OperationsRequiring(ManagementLevel level) =>
        [.. Enum.GetValues<ManagementOperation>().Where(
            operation => ManagementOperations.RequiredLevel(operation) == level)];
}
