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
/// <b>Both transports meet the same gate, in the same place.</b> The level an operation needs is
/// <c>ManagementOperations</c>' one table, and <c>AlvoManagementService</c> reads it at the head of every
/// member — so an in-process caller is judged exactly as an HTTP one is. That is not a courtesy: a
/// dashboard resolves one registered <see cref="IAlvoManagement"/> and serves many humans through it, so
/// "whatever composed this reference admitted the caller" admits the <em>process</em>, not the person.
/// <c>ManagementAccessEndpointFilter</c> reads the same table through the same evaluator before model
/// binding — an earlier, cheaper rejection of the same answer, never a second authority. See
/// <c>AlvoManagementService.EnsureMayPerform</c> and <c>EnsureMayChangeAccess</c>.
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

    /// <summary>Write the instance's AI connection, including its API key.</summary>
    SetAiConnection,

    /// <summary>The danger zone: delete the project.</summary>
    DeleteProject,
}
