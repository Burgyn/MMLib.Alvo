namespace MMLib.Alvo.Management;

/// <summary>
/// Every operation the Management API exposes, as the F5 admin-dashboard design's level table lists
/// them.
/// </summary>
/// <remarks>
/// <para>
/// <b>An operation, not a route.</b> The same operation is reachable in-process (an embedded host calling
/// the management surface directly) and over HTTP, which is what makes "one path, two transports" a
/// composition fact rather than two implementations.
/// </para>
/// <para>
/// <b>The two transports are gated at two different moments, deliberately.</b> The <em>level</em> each
/// operation needs is decided here and enforced by <c>ManagementAccessEndpointFilter</c> on the HTTP
/// adapter — an in-process caller reached <see cref="IAlvoManagement"/> because whatever composed it
/// admitted it, which is the same decision made earlier. The one judgment that is <b>not</b> pre-decidable
/// at composition is whether a particular write changes the descriptor's <c>access</c> block, because it
/// depends on what was sent; that comparison lives inside <c>AlvoManagementService</c>, so both transports
/// meet it. See <c>AlvoManagementService.EnsureMayChangeAccess</c>.
/// </para>
/// </remarks>
internal enum ManagementOperation
{
    /// <summary>List the projects this instance serves.</summary>
    ListProjects,

    /// <summary>Read the current descriptor and its revision — this is also the export.</summary>
    GetDescriptor,

    /// <summary>Read the append-only revision history.</summary>
    ListRevisions,

    /// <summary>Read one past revision.</summary>
    GetRevision,

    /// <summary>Read the resolved schema the Data API actually serves.</summary>
    GetSchema,

    /// <summary>Read what this build honours, warns about and refuses.</summary>
    GetCapabilities,

    /// <summary>Read the build, mode, engine and startup mode.</summary>
    GetInfo,

    /// <summary>Evaluate a policy for a simulated caller. Writes nothing.</summary>
    SimulatePolicy,

    /// <summary>Apply a descriptor — including a <c>?dryRun=true</c> plan, which discloses the same thing.</summary>
    ApplyDescriptor,

    /// <summary>Roll back to a past revision.</summary>
    RollbackRevision,

    /// <summary>Issue or revoke an API key.</summary>
    ManageApiKeys,

    /// <summary>Administer users and their role memberships.</summary>
    ManageUsers,

    /// <summary>The danger zone: delete the project.</summary>
    DeleteProject,
}
