namespace MMLib.Alvo.Management;

/// <summary>
/// The one table mapping a <see cref="ManagementOperation"/> onto the <see cref="ManagementLevel"/> it
/// needs.
/// </summary>
/// <remarks>
/// <b>Deny by default, like the CEL profile table.</b> An operation missing from the table resolves to
/// <see cref="ManagementLevel.Admin"/> — the most restrictive answer, not the most convenient one — so a
/// future operation added without a decision is refused for everyone but an administrator rather than
/// opened to every viewer. A fact additionally asserts that every enum member really is listed, so the
/// fallback is unreachable in a correct build, and a second fact holds the fallback itself to its claim.
/// </remarks>
internal static class ManagementOperations
{
    private static readonly Dictionary<ManagementOperation, ManagementLevel> _requiredLevels = new()
    {
        [ManagementOperation.ListProjects] = ManagementLevel.Viewer,
        [ManagementOperation.GetDescriptor] = ManagementLevel.Viewer,
        [ManagementOperation.ListRevisions] = ManagementLevel.Viewer,
        [ManagementOperation.GetRevision] = ManagementLevel.Viewer,
        [ManagementOperation.GetSchema] = ManagementLevel.Viewer,
        [ManagementOperation.GetCapabilities] = ManagementLevel.Viewer,
        [ManagementOperation.GetInfo] = ManagementLevel.Viewer,
        [ManagementOperation.SimulatePolicy] = ManagementLevel.Viewer,
        [ManagementOperation.ApplyDescriptor] = ManagementLevel.Developer,
        [ManagementOperation.RollbackRevision] = ManagementLevel.Developer,
        [ManagementOperation.SetAiConnection] = ManagementLevel.Admin,
        [ManagementOperation.ManageApiKeys] = ManagementLevel.Admin,
        [ManagementOperation.ManageUsers] = ManagementLevel.Admin,
        [ManagementOperation.DeleteProject] = ManagementLevel.Admin,
    };

    /// <summary>The level <paramref name="operation"/> requires.</summary>
    /// <param name="operation">The operation about to be performed.</param>
    internal static ManagementLevel RequiredLevel(ManagementOperation operation) =>
        _requiredLevels.TryGetValue(operation, out var level) ? level : ManagementLevel.Admin;
}
